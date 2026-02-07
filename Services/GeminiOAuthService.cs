using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DiskWise.Services;

/// <summary>
/// OAuth service for Google Gemini authentication
/// Implements the same OAuth flow as Gemini CLI
/// </summary>
public class GeminiOAuthService
{
    // OAuth Client credentials:
    //   Baked in at build time via: dotnet build -p:GoogleClientId=xxx -p:GoogleClientSecret=xxx
    //   Or override at runtime via env vars: DISKWISE_GOOGLE_CLIENT_ID / DISKWISE_GOOGLE_CLIENT_SECRET
    private static readonly string OAuthClientId =
        Environment.GetEnvironmentVariable("DISKWISE_GOOGLE_CLIENT_ID")
        ?? (string.IsNullOrEmpty(Secrets.GoogleClientId) ? "" : Secrets.GoogleClientId);
    private static readonly string OAuthClientSecret =
        Environment.GetEnvironmentVariable("DISKWISE_GOOGLE_CLIENT_SECRET")
        ?? (string.IsNullOrEmpty(Secrets.GoogleClientSecret) ? "" : Secrets.GoogleClientSecret);

    // OAuth Scopes
    private static readonly string[] OAuthScopes =
    [
        "https://www.googleapis.com/auth/cloud-platform",
        "https://www.googleapis.com/auth/userinfo.email",
        "https://www.googleapis.com/auth/userinfo.profile"
    ];

    private const string TokenEndpoint = "https://oauth2.googleapis.com/token";
    private const string AuthEndpoint = "https://accounts.google.com/o/oauth2/v2/auth";
    private const string SignInSuccessUrl = "https://developers.google.com/gemini-code-assist/auth_success_gemini";
    private const string SignInFailureUrl = "https://developers.google.com/gemini-code-assist/auth_failure_gemini";

    private readonly HttpClient _httpClient;
    private OAuthCredentials? _credentials;
    private readonly string _credentialsPath;

    public event Action<string>? OnStatusChanged;
    public event Action<string>? OnError;

    public GeminiOAuthService()
    {
        _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };

        // Store credentials in AppData
        var appDataPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "DiskWise");
        Directory.CreateDirectory(appDataPath);
        _credentialsPath = Path.Combine(appDataPath, "gemini-oauth.json");
    }

    /// <summary>
    /// Check if we have valid credentials
    /// </summary>
    public bool IsAuthenticated => _credentials != null &&
                                    !string.IsNullOrEmpty(_credentials.AccessToken) &&
                                    _credentials.ExpiryDate > DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    /// <summary>
    /// Get current access token (refreshing if needed)
    /// </summary>
    public async Task<string?> GetAccessTokenAsync(CancellationToken ct = default)
    {
        if (_credentials == null)
        {
            await LoadCredentialsAsync();
        }

        if (_credentials == null)
        {
            return null;
        }

        // Check if token is expired or about to expire (within 5 minutes)
        var expiryBuffer = 5 * 60 * 1000; // 5 minutes in milliseconds
        if (_credentials.ExpiryDate <= DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + expiryBuffer)
        {
            // Try to refresh
            if (!string.IsNullOrEmpty(_credentials.RefreshToken))
            {
                var refreshed = await RefreshTokenAsync(ct);
                if (!refreshed)
                {
                    return null;
                }
            }
            else
            {
                return null;
            }
        }

        return _credentials.AccessToken;
    }

    /// <summary>
    /// Load credentials from disk
    /// </summary>
    public async Task<bool> LoadCredentialsAsync()
    {
        try
        {
            if (!File.Exists(_credentialsPath))
            {
                return false;
            }

            var json = await File.ReadAllTextAsync(_credentialsPath);
            _credentials = JsonSerializer.Deserialize<OAuthCredentials>(json);
            return _credentials != null && !string.IsNullOrEmpty(_credentials.AccessToken);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Save credentials to disk
    /// </summary>
    private async Task SaveCredentialsAsync()
    {
        if (_credentials == null) return;

        try
        {
            var json = JsonSerializer.Serialize(_credentials, new JsonSerializerOptions
            {
                WriteIndented = true
            });
            await File.WriteAllTextAsync(_credentialsPath, json);
        }
        catch (Exception ex)
        {
            OnError?.Invoke($"Failed to save credentials: {ex.Message}");
        }
    }

    /// <summary>
    /// Clear stored credentials (logout)
    /// </summary>
    public async Task LogoutAsync()
    {
        _credentials = null;
        try
        {
            if (File.Exists(_credentialsPath))
            {
                File.Delete(_credentialsPath);
            }
        }
        catch
        {
            // Ignore
        }
    }

    /// <summary>
    /// Start OAuth login flow
    /// Opens browser for authentication
    /// </summary>
    public async Task<bool> LoginAsync(CancellationToken ct = default)
    {
        try
        {
            OnStatusChanged?.Invoke("Starting OAuth authentication...");

            // Find available port
            var port = GetAvailablePort();
            var redirectUri = $"http://localhost:{port}/oauth2callback";

            // Generate state for CSRF protection
            var state = GenerateRandomString(32);

            // Generate PKCE code verifier and challenge
            var codeVerifier = GenerateRandomString(64);
            var codeChallenge = GenerateCodeChallenge(codeVerifier);

            // Build auth URL
            var scope = string.Join(" ", OAuthScopes);
            var authUrl = $"{AuthEndpoint}?" +
                          $"client_id={Uri.EscapeDataString(OAuthClientId)}&" +
                          $"redirect_uri={Uri.EscapeDataString(redirectUri)}&" +
                          $"response_type=code&" +
                          $"scope={Uri.EscapeDataString(scope)}&" +
                          $"access_type=offline&" +
                          $"state={Uri.EscapeDataString(state)}&" +
                          $"code_challenge={Uri.EscapeDataString(codeChallenge)}&" +
                          $"code_challenge_method=S256&" +
                          $"prompt=consent";

            // Start local HTTP server to receive callback
            using var listener = new HttpListener();
            listener.Prefixes.Add($"http://localhost:{port}/");
            listener.Start();

            OnStatusChanged?.Invoke("Opening browser for authentication...");

            // Open browser
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = authUrl,
                    UseShellExecute = true
                });
            }
            catch
            {
                OnError?.Invoke($"Failed to open browser. Please navigate to:\n{authUrl}");
            }

            OnStatusChanged?.Invoke("Waiting for authentication...");

            // Wait for callback with timeout
            var getContextTask = listener.GetContextAsync();
            var timeoutTask = Task.Delay(TimeSpan.FromMinutes(5), ct);

            var completedTask = await Task.WhenAny(getContextTask, timeoutTask);

            if (completedTask == timeoutTask)
            {
                OnError?.Invoke("Authentication timed out");
                return false;
            }

            var context = await getContextTask;
            var request = context.Request;
            var response = context.Response;

            try
            {
                // Parse callback URL
                var query = request.Url?.Query;
                if (string.IsNullOrEmpty(query))
                {
                    await SendRedirect(response, SignInFailureUrl);
                    OnError?.Invoke("No query parameters in callback");
                    return false;
                }

                var queryParams = System.Web.HttpUtility.ParseQueryString(query);

                // Check for error
                var error = queryParams["error"];
                if (!string.IsNullOrEmpty(error))
                {
                    await SendRedirect(response, SignInFailureUrl);
                    OnError?.Invoke($"OAuth error: {error}");
                    return false;
                }

                // Verify state
                var returnedState = queryParams["state"];
                if (returnedState != state)
                {
                    await SendRedirect(response, SignInFailureUrl);
                    OnError?.Invoke("OAuth state mismatch (possible CSRF attack)");
                    return false;
                }

                // Get authorization code
                var code = queryParams["code"];
                if (string.IsNullOrEmpty(code))
                {
                    await SendRedirect(response, SignInFailureUrl);
                    OnError?.Invoke("No authorization code received");
                    return false;
                }

                OnStatusChanged?.Invoke("Exchanging authorization code...");

                // Exchange code for tokens
                var success = await ExchangeCodeForTokensAsync(code, redirectUri, codeVerifier, ct);

                if (success)
                {
                    await SendRedirect(response, SignInSuccessUrl);
                    OnStatusChanged?.Invoke("Authentication successful!");
                    return true;
                }
                else
                {
                    await SendRedirect(response, SignInFailureUrl);
                    return false;
                }
            }
            finally
            {
                response.Close();
            }
        }
        catch (OperationCanceledException)
        {
            OnStatusChanged?.Invoke("Authentication cancelled");
            return false;
        }
        catch (Exception ex)
        {
            OnError?.Invoke($"Authentication failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Exchange authorization code for tokens
    /// </summary>
    private async Task<bool> ExchangeCodeForTokensAsync(string code, string redirectUri, string codeVerifier, CancellationToken ct)
    {
        try
        {
            var content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["code"] = code,
                ["client_id"] = OAuthClientId,
                ["client_secret"] = OAuthClientSecret,
                ["redirect_uri"] = redirectUri,
                ["grant_type"] = "authorization_code",
                ["code_verifier"] = codeVerifier
            });

            var response = await _httpClient.PostAsync(TokenEndpoint, content, ct);

            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync(ct);
                OnError?.Invoke($"Token exchange failed: {errorContent}");
                return false;
            }

            var json = await response.Content.ReadAsStringAsync(ct);
            var tokenResponse = JsonSerializer.Deserialize<TokenResponse>(json);

            if (tokenResponse == null || string.IsNullOrEmpty(tokenResponse.AccessToken))
            {
                OnError?.Invoke("Invalid token response");
                return false;
            }

            // Calculate expiry time
            var expiryDate = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + (tokenResponse.ExpiresIn * 1000);

            _credentials = new OAuthCredentials
            {
                AccessToken = tokenResponse.AccessToken,
                RefreshToken = tokenResponse.RefreshToken,
                ExpiryDate = expiryDate,
                TokenType = tokenResponse.TokenType,
                Scope = tokenResponse.Scope
            };

            await SaveCredentialsAsync();
            return true;
        }
        catch (Exception ex)
        {
            OnError?.Invoke($"Token exchange error: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Refresh access token using refresh token
    /// </summary>
    private async Task<bool> RefreshTokenAsync(CancellationToken ct)
    {
        if (_credentials == null || string.IsNullOrEmpty(_credentials.RefreshToken))
        {
            return false;
        }

        try
        {
            OnStatusChanged?.Invoke("Refreshing access token...");

            var content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["refresh_token"] = _credentials.RefreshToken,
                ["client_id"] = OAuthClientId,
                ["client_secret"] = OAuthClientSecret,
                ["grant_type"] = "refresh_token"
            });

            var response = await _httpClient.PostAsync(TokenEndpoint, content, ct);

            if (!response.IsSuccessStatusCode)
            {
                OnError?.Invoke("Token refresh failed - please login again");
                return false;
            }

            var json = await response.Content.ReadAsStringAsync(ct);
            var tokenResponse = JsonSerializer.Deserialize<TokenResponse>(json);

            if (tokenResponse == null || string.IsNullOrEmpty(tokenResponse.AccessToken))
            {
                return false;
            }

            // Calculate expiry time
            var expiryDate = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + (tokenResponse.ExpiresIn * 1000);

            _credentials.AccessToken = tokenResponse.AccessToken;
            _credentials.ExpiryDate = expiryDate;

            // Refresh token might be updated
            if (!string.IsNullOrEmpty(tokenResponse.RefreshToken))
            {
                _credentials.RefreshToken = tokenResponse.RefreshToken;
            }

            await SaveCredentialsAsync();
            OnStatusChanged?.Invoke("Token refreshed successfully");
            return true;
        }
        catch (Exception ex)
        {
            OnError?.Invoke($"Token refresh error: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Get available port for OAuth callback
    /// </summary>
    private static int GetAvailablePort()
    {
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)socket.LocalEndPoint!).Port;
    }

    /// <summary>
    /// Generate random string for state/verifier
    /// </summary>
    private static string GenerateRandomString(int length)
    {
        var bytes = new byte[length];
        using var rng = RandomNumberGenerator.Create();
        rng.GetBytes(bytes);
        return Convert.ToBase64String(bytes)
            .Replace("+", "-")
            .Replace("/", "_")
            .Replace("=", "")
            [..length];
    }

    /// <summary>
    /// Generate PKCE code challenge from verifier
    /// </summary>
    private static string GenerateCodeChallenge(string codeVerifier)
    {
        using var sha256 = SHA256.Create();
        var bytes = sha256.ComputeHash(Encoding.ASCII.GetBytes(codeVerifier));
        return Convert.ToBase64String(bytes)
            .Replace("+", "-")
            .Replace("/", "_")
            .Replace("=", "");
    }

    /// <summary>
    /// Send redirect response
    /// </summary>
    private static async Task SendRedirect(HttpListenerResponse response, string url)
    {
        response.StatusCode = 301;
        response.Headers.Add("Location", url);
        var buffer = Encoding.UTF8.GetBytes($"<html><head><meta http-equiv='refresh' content='0;url={url}'></head></html>");
        response.ContentLength64 = buffer.Length;
        await response.OutputStream.WriteAsync(buffer);
    }
}

/// <summary>
/// OAuth credentials stored on disk
/// </summary>
internal class OAuthCredentials
{
    [JsonPropertyName("access_token")]
    public string? AccessToken { get; set; }

    [JsonPropertyName("refresh_token")]
    public string? RefreshToken { get; set; }

    [JsonPropertyName("expiry_date")]
    public long ExpiryDate { get; set; }

    [JsonPropertyName("token_type")]
    public string? TokenType { get; set; }

    [JsonPropertyName("scope")]
    public string? Scope { get; set; }
}

/// <summary>
/// OAuth token response from Google
/// </summary>
internal class TokenResponse
{
    [JsonPropertyName("access_token")]
    public string? AccessToken { get; set; }

    [JsonPropertyName("refresh_token")]
    public string? RefreshToken { get; set; }

    [JsonPropertyName("expires_in")]
    public int ExpiresIn { get; set; }

    [JsonPropertyName("token_type")]
    public string? TokenType { get; set; }

    [JsonPropertyName("scope")]
    public string? Scope { get; set; }
}

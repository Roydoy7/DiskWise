using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using DiskWise.Models;

namespace DiskWise.Services;

/// <summary>
/// Service for interacting with Google Gemini API using OAuth authentication
/// Uses Code Assist API (cloudcode-pa.googleapis.com) which is the same endpoint used by Gemini CLI
/// </summary>
public class GeminiService
{
    private readonly HttpClient _httpClient;
    private readonly GeminiOAuthService _oauthService;

    // Code Assist API endpoint - same as Gemini CLI uses
    private const string CodeAssistEndpoint = "https://cloudcode-pa.googleapis.com";
    private const string ApiVersion = "v1internal";

    // User setup state
    private string? _projectId;
    private UserTierId _userTier = UserTierId.Free;
    private bool _isSetupComplete;

    // JSON options that match the API expectations
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private const string SystemPrompt = """
        You are a Windows system cleanup expert. Analyze the given folder information and determine if it can be safely deleted.

        Respond in this EXACT format (no markdown, no extra text):
        LEVEL: SAFE|CAUTION|DANGER
        REASON: One sentence explanation

        Guidelines:
        - SAFE: Cache folders, temp files, build outputs, node_modules, .cache, __pycache__, Temp, tmp, browser caches, log files
        - CAUTION: .git folders (might be needed), old backups, downloads folder contents, vendor folders
        - DANGER: Windows system files, Program Files, user documents (Documents, Pictures, Videos), AppData/Roaming configs, source code
        """;

    /// <summary>
    /// OAuth service for authentication
    /// </summary>
    public GeminiOAuthService OAuth => _oauthService;

    public GeminiService()
    {
        _httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(60)
        };
        _oauthService = new GeminiOAuthService();
    }

    /// <summary>
    /// Check if authenticated with Gemini
    /// </summary>
    public bool IsAuthenticated => _oauthService.IsAuthenticated;

    /// <summary>
    /// Check if Gemini is available (has valid credentials)
    /// </summary>
    public async Task<bool> IsAvailableAsync(CancellationToken ct = default)
    {
        var token = await _oauthService.GetAccessTokenAsync(ct);
        return !string.IsNullOrEmpty(token);
    }

    /// <summary>
    /// Start OAuth login flow
    /// </summary>
    public Task<bool> LoginAsync(CancellationToken ct = default)
    {
        return _oauthService.LoginAsync(ct);
    }

    /// <summary>
    /// Logout and clear credentials
    /// </summary>
    public async Task LogoutAsync()
    {
        await _oauthService.LogoutAsync();
        _isSetupComplete = false;
        _projectId = null;
    }

    /// <summary>
    /// Setup user with Code Assist API (loadCodeAssist + onboardUser)
    /// This must be called before generateContent
    /// </summary>
    private async Task<bool> EnsureUserSetupAsync(string accessToken, CancellationToken ct)
    {
        if (_isSetupComplete && !string.IsNullOrEmpty(_projectId))
        {
            return true;
        }

        try
        {
            // Step 1: Call loadCodeAssist to check user status
            var loadRequest = new LoadCodeAssistRequest
            {
                Metadata = new ClientMetadata
                {
                    IdeType = "IDE_UNSPECIFIED",
                    Platform = "PLATFORM_UNSPECIFIED",
                    PluginType = "GEMINI"
                }
            };

            var loadMessage = new HttpRequestMessage(HttpMethod.Post,
                $"{CodeAssistEndpoint}/{ApiVersion}:loadCodeAssist");
            loadMessage.Headers.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
            loadMessage.Content = new StringContent(
                JsonSerializer.Serialize(loadRequest, JsonOptions),
                System.Text.Encoding.UTF8,
                "application/json");

            var loadResponse = await _httpClient.SendAsync(loadMessage, ct);
            var loadContent = await loadResponse.Content.ReadAsStringAsync(ct);

            if (!loadResponse.IsSuccessStatusCode)
            {
                throw new Exception($"loadCodeAssist failed ({loadResponse.StatusCode}): {loadContent}");
            }

            var loadResult = JsonSerializer.Deserialize<LoadCodeAssistResponse>(loadContent);

            // If user already has a project and tier, we're done
            if (loadResult?.CurrentTier != null)
            {
                _projectId = loadResult.CloudaicompanionProject;
                _userTier = ParseUserTier(loadResult.CurrentTier.Id);
                _isSetupComplete = true;
                return true;
            }

            // Step 2: Need to onboard user - find default tier
            var tierId = "free-tier"; // Default to free tier
            if (loadResult?.AllowedTiers != null)
            {
                foreach (var tier in loadResult.AllowedTiers)
                {
                    if (tier.IsDefault == true)
                    {
                        tierId = tier.Id ?? "free-tier";
                        break;
                    }
                }
            }

            // Step 3: Call onboardUser
            // For free tier, don't set cloudaicompanionProject (it causes Precondition Failed error)
            var onboardRequest = new OnboardUserRequest
            {
                TierId = tierId,
                Metadata = new ClientMetadata
                {
                    IdeType = "IDE_UNSPECIFIED",
                    Platform = "PLATFORM_UNSPECIFIED",
                    PluginType = "GEMINI"
                }
            };

            // Poll until onboarding is complete
            for (int i = 0; i < 12; i++) // Max 60 seconds (12 * 5s)
            {
                var onboardMessage = new HttpRequestMessage(HttpMethod.Post,
                    $"{CodeAssistEndpoint}/{ApiVersion}:onboardUser");
                onboardMessage.Headers.Authorization =
                    new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
                onboardMessage.Content = new StringContent(
                    JsonSerializer.Serialize(onboardRequest, JsonOptions),
                    System.Text.Encoding.UTF8,
                    "application/json");

                var onboardResponse = await _httpClient.SendAsync(onboardMessage, ct);
                var onboardContent = await onboardResponse.Content.ReadAsStringAsync(ct);

                if (!onboardResponse.IsSuccessStatusCode)
                {
                    throw new Exception($"onboardUser failed ({onboardResponse.StatusCode}): {onboardContent}");
                }

                var onboardResult = JsonSerializer.Deserialize<OnboardUserResponse>(onboardContent);

                if (onboardResult?.Done == true)
                {
                    _projectId = onboardResult.Response?.CloudaicompanionProject?.Id;
                    _userTier = ParseUserTier(tierId);
                    _isSetupComplete = true;
                    return true;
                }

                // Wait before polling again
                await Task.Delay(5000, ct);
            }

            throw new Exception("Onboarding timed out");
        }
        catch (Exception ex)
        {
            throw new Exception($"User setup failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Get AI advice for a file system item
    /// </summary>
    public async Task<AIAdvice> GetAdviceAsync(FileSystemItem item, CancellationToken ct = default)
    {
        var advice = new AIAdvice { IsAnalyzing = true };

        var accessToken = await _oauthService.GetAccessTokenAsync(ct);
        if (string.IsNullOrEmpty(accessToken))
        {
            advice.Error = "Not authenticated with Gemini. Please login first.";
            advice.IsAnalyzing = false;
            return advice;
        }

        try
        {
            // Ensure user is setup before calling generateContent
            await EnsureUserSetupAsync(accessToken, ct);

            var userPrompt = BuildEnhancedPrompt(item);

            // Code Assist API request format (same as Gemini CLI uses)
            var request = new GenerateContentRequest
            {
                Model = "gemini-2.0-flash",
                Project = _projectId,
                UserPromptId = Guid.NewGuid().ToString(),
                Request = new VertexGenerateContentRequest
                {
                    Contents =
                    [
                        new ContentItem
                        {
                            Role = "user",
                            Parts =
                            [
                                new ContentPart { Text = SystemPrompt + "\n\n" + userPrompt }
                            ]
                        }
                    ],
                    GenerationConfig = new GenerationConfig
                    {
                        Temperature = 0.3,
                        MaxOutputTokens = 256
                    }
                }
            };

            // Use Code Assist API endpoint (same as Gemini CLI)
            var requestMessage = new HttpRequestMessage(HttpMethod.Post,
                $"{CodeAssistEndpoint}/{ApiVersion}:generateContent");
            requestMessage.Headers.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
            requestMessage.Content = new StringContent(
                JsonSerializer.Serialize(request, JsonOptions),
                System.Text.Encoding.UTF8,
                "application/json");

            var response = await _httpClient.SendAsync(requestMessage, ct);

            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync(ct);

                // Provide more helpful error message
                var statusCode = (int)response.StatusCode;
                var errorMsg = statusCode switch
                {
                    400 => $"Bad Request - API format error: {errorContent}",
                    401 => "Unauthorized - Token may be expired. Try logging out and back in.",
                    403 => "Forbidden - Access denied to Gemini API.",
                    429 => "Rate limited - Too many requests. Please wait.",
                    500 => $"Server error - Gemini API internal error: {errorContent}",
                    _ => $"API error ({statusCode}): {errorContent}"
                };

                throw new HttpRequestException(errorMsg);
            }

            var result = await response.Content.ReadFromJsonAsync<CodeAssistResponse>(ct);

            if (result?.Response?.Candidates is { Count: > 0 })
            {
                var content = result.Response.Candidates[0].Content?.Parts?[0]?.Text ?? "";
                ParseAdviceResponse(content, advice);
            }
            else
            {
                advice.Error = "No response from Gemini API";
                advice.Level = AdviceLevel.Unknown;
            }
        }
        catch (Exception ex)
        {
            advice.Error = ex.Message;
            advice.Level = AdviceLevel.Unknown;
        }
        finally
        {
            advice.IsAnalyzing = false;
        }

        return advice;
    }

    /// <summary>
    /// Build enhanced prompt with context about siblings and children
    /// </summary>
    private static string BuildEnhancedPrompt(FileSystemItem item)
    {
        var sb = new System.Text.StringBuilder();

        // Basic info
        sb.AppendLine($"=== TARGET FOLDER ===");
        sb.AppendLine($"Path: {item.Path}");
        sb.AppendLine($"Name: {item.Name}");
        sb.AppendLine($"Size: {FileSystemItem.FormatSize(item.Size)}");
        sb.AppendLine($"Files: {item.FileCount}");
        sb.AppendLine($"Subfolders: {item.FolderCount}");
        sb.AppendLine($"Hidden: {item.IsHidden}");
        sb.AppendLine($"Last Modified: {item.LastModifiedDisplay}");

        // Add sibling context (other folders at same level)
        if (item.Parent?.Children.Count > 1)
        {
            sb.AppendLine();
            sb.AppendLine($"=== SIBLING FOLDERS (same directory) ===");
            var siblings = item.Parent.Children
                .Where(c => c != item && c.IsDirectory)
                .OrderByDescending(c => c.Size)
                .Take(5);
            foreach (var sibling in siblings)
            {
                sb.AppendLine($"- {sibling.Name} ({FileSystemItem.FormatSize(sibling.Size)})");
            }
        }

        // Add children context (subfolders)
        if (item.Children.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"=== SUBFOLDERS (top 8 by size) ===");
            var children = item.Children
                .Where(c => c.IsDirectory)
                .OrderByDescending(c => c.Size)
                .Take(8);
            foreach (var child in children)
            {
                sb.AppendLine($"- {child.Name} ({FileSystemItem.FormatSize(child.Size)})");
            }

            // Also list some files if present
            var files = item.Children
                .Where(c => !c.IsDirectory)
                .OrderByDescending(c => c.Size)
                .Take(5);
            if (files.Any())
            {
                sb.AppendLine();
                sb.AppendLine($"=== FILES IN FOLDER (top 5 by size) ===");
                foreach (var file in files)
                {
                    sb.AppendLine($"- {file.Name} ({FileSystemItem.FormatSize(file.Size)})");
                }
            }
        }

        return sb.ToString();
    }

    private static void ParseAdviceResponse(string content, AIAdvice advice)
    {
        var lines = content.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        foreach (var line in lines)
        {
            var trimmed = line.Trim();

            if (trimmed.StartsWith("LEVEL:", StringComparison.OrdinalIgnoreCase))
            {
                var level = trimmed[6..].Trim().ToUpperInvariant();
                advice.Level = level switch
                {
                    "SAFE" => AdviceLevel.Safe,
                    "CAUTION" => AdviceLevel.Caution,
                    "DANGER" => AdviceLevel.Danger,
                    _ => AdviceLevel.Unknown
                };
            }
            else if (trimmed.StartsWith("REASON:", StringComparison.OrdinalIgnoreCase))
            {
                advice.Reason = trimmed[7..].Trim();
            }
        }

        if (advice.Level == AdviceLevel.Unknown && !string.IsNullOrEmpty(content))
        {
            var upper = content.ToUpperInvariant();
            if (upper.Contains("SAFE"))
                advice.Level = AdviceLevel.Safe;
            else if (upper.Contains("CAUTION"))
                advice.Level = AdviceLevel.Caution;
            else if (upper.Contains("DANGER"))
                advice.Level = AdviceLevel.Danger;

            if (string.IsNullOrEmpty(advice.Reason))
                advice.Reason = content.Length > 200 ? content[..200] + "..." : content;
        }
    }

    private static UserTierId ParseUserTier(string? tierId)
    {
        return tierId switch
        {
            "free-tier" => UserTierId.Free,
            "legacy-tier" => UserTierId.Legacy,
            "standard-tier" => UserTierId.Standard,
            _ => UserTierId.Free
        };
    }

    #region AI Search

    private const string SearchSystemPrompt = """
        You are a Windows file system expert. Generate search keywords for finding files/folders.

        RULES:
        - Return ACTUAL folder/file names that exist on Windows disk
        - DO NOT return words like "安装", "位置", "location", "install", "find"
        - Include English names, abbreviations, technical identifiers

        RESPONSE FORMAT (no markdown, no JSON, just plain text):
        KEYWORDS: word1, word2, word3
        EXTENSIONS: .ext1, .ext2

        Examples:
        Q: "Steam游戏"
        KEYWORDS: Steam, steamapps, SteamLibrary, common
        EXTENSIONS: .exe, .acf

        Q: "Visual Studio Code"
        KEYWORDS: VSCode, Code, .vscode, Microsoft VS Code
        EXTENSIONS: .exe

        Q: "Chrome缓存"
        KEYWORDS: Chrome, Google, Cache, User Data
        EXTENSIONS:

        Q: "微信聊天"
        KEYWORDS: WeChat, Tencent, WeChat Files, MsgAttach
        EXTENSIONS: .db, .dat

        Q: "魔兽争霸存档"
        KEYWORDS: Warcraft, war3, WC3, Replays, SavedGames
        EXTENSIONS: .w3g, .w3z
        """;

    /// <summary>
    /// Generate search keywords from natural language description
    /// </summary>
    public async Task<AISearchResult> GenerateSearchKeywordsAsync(string userDescription, CancellationToken ct = default)
    {
        var result = new AISearchResult();

        var accessToken = await _oauthService.GetAccessTokenAsync(ct);
        if (string.IsNullOrEmpty(accessToken))
        {
            result.Error = "Not authenticated with Gemini. Please login first.";
            return result;
        }

        try
        {
            await EnsureUserSetupAsync(accessToken, ct);

            var request = new GenerateContentRequest
            {
                Model = "gemini-2.5-flash",
                Project = _projectId,
                UserPromptId = Guid.NewGuid().ToString(),
                Request = new VertexGenerateContentRequest
                {
                    Contents =
                    [
                        new ContentItem
                        {
                            Role = "user",
                            Parts =
                            [
                                new ContentPart { Text = SearchSystemPrompt + "\n\nUser query: " + userDescription }
                            ]
                        }
                    ],
                    GenerationConfig = new GenerationConfig
                    {
                        Temperature = 0.2,
                        MaxOutputTokens = 256
                    }
                }
            };

            var requestMessage = new HttpRequestMessage(HttpMethod.Post,
                $"{CodeAssistEndpoint}/{ApiVersion}:generateContent");
            requestMessage.Headers.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
            requestMessage.Content = new StringContent(
                JsonSerializer.Serialize(request, JsonOptions),
                System.Text.Encoding.UTF8,
                "application/json");

            var response = await _httpClient.SendAsync(requestMessage, ct);
            response.EnsureSuccessStatusCode();

            var apiResult = await response.Content.ReadFromJsonAsync<CodeAssistResponse>(ct);

            if (apiResult?.Response?.Candidates is { Count: > 0 })
            {
                var content = apiResult.Response.Candidates[0].Content?.Parts?[0]?.Text ?? "";
                ParseSearchResponse(content, result);
            }
        }
        catch (Exception ex)
        {
            result.Error = ex.Message;
        }

        return result;
    }

    private static void ParseSearchResponse(string content, AISearchResult result)
    {
        // Parse simple "KEYWORDS: a, b, c" format
        var lines = content.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        foreach (var line in lines)
        {
            var trimmed = line.Trim();

            if (trimmed.StartsWith("KEYWORDS:", StringComparison.OrdinalIgnoreCase))
            {
                var keywordsStr = trimmed[9..].Trim();
                result.Keywords = keywordsStr
                    .Split(',', StringSplitOptions.RemoveEmptyEntries)
                    .Select(k => k.Trim())
                    .Where(k => k.Length > 0)
                    .ToList();
            }
            else if (trimmed.StartsWith("EXTENSIONS:", StringComparison.OrdinalIgnoreCase))
            {
                var extStr = trimmed[11..].Trim();
                if (!string.IsNullOrEmpty(extStr))
                {
                    result.Extensions = extStr
                        .Split(',', StringSplitOptions.RemoveEmptyEntries)
                        .Select(e => e.Trim())
                        .Where(e => e.Length > 0)
                        .ToList();
                }
            }
        }

        // Fallback: if no KEYWORDS found, try to extract any useful words
        if (result.Keywords.Count == 0)
        {
            var words = content
                .Split([' ', ',', '\n', '\r', ':', '"', '[', ']', '{', '}'], StringSplitOptions.RemoveEmptyEntries)
                .Where(w => w.Length >= 2 &&
                            !w.Equals("KEYWORDS", StringComparison.OrdinalIgnoreCase) &&
                            !w.Equals("EXTENSIONS", StringComparison.OrdinalIgnoreCase) &&
                            !w.StartsWith("http"))
                .Take(6)
                .ToList();

            result.Keywords = words;
        }

        result.Success = result.Keywords.Count > 0;
    }

    #endregion
}

internal enum UserTierId
{
    Free,
    Legacy,
    Standard
}

#region API Request Types

// loadCodeAssist request
internal class LoadCodeAssistRequest
{
    [JsonPropertyName("cloudaicompanionProject")]
    public string? CloudaicompanionProject { get; set; }

    [JsonPropertyName("metadata")]
    public ClientMetadata? Metadata { get; set; }
}

internal class ClientMetadata
{
    [JsonPropertyName("ideType")]
    public string? IdeType { get; set; }

    [JsonPropertyName("platform")]
    public string? Platform { get; set; }

    [JsonPropertyName("pluginType")]
    public string? PluginType { get; set; }

    [JsonPropertyName("duetProject")]
    public string? DuetProject { get; set; }
}

// onboardUser request
internal class OnboardUserRequest
{
    [JsonPropertyName("tierId")]
    public string? TierId { get; set; }

    [JsonPropertyName("cloudaicompanionProject")]
    public string? CloudaicompanionProject { get; set; }

    [JsonPropertyName("metadata")]
    public ClientMetadata? Metadata { get; set; }
}

// generateContent request
internal class GenerateContentRequest
{
    [JsonPropertyName("model")]
    public string? Model { get; set; }

    [JsonPropertyName("project")]
    public string? Project { get; set; }

    [JsonPropertyName("user_prompt_id")]
    public string? UserPromptId { get; set; }

    [JsonPropertyName("request")]
    public VertexGenerateContentRequest? Request { get; set; }
}

internal class VertexGenerateContentRequest
{
    [JsonPropertyName("contents")]
    public List<ContentItem>? Contents { get; set; }

    [JsonPropertyName("generationConfig")]
    public GenerationConfig? GenerationConfig { get; set; }
}

internal class ContentItem
{
    [JsonPropertyName("role")]
    public string? Role { get; set; }

    [JsonPropertyName("parts")]
    public List<ContentPart>? Parts { get; set; }
}

internal class ContentPart
{
    [JsonPropertyName("text")]
    public string? Text { get; set; }
}

internal class GenerationConfig
{
    [JsonPropertyName("temperature")]
    public double? Temperature { get; set; }

    [JsonPropertyName("maxOutputTokens")]
    public int? MaxOutputTokens { get; set; }
}

#endregion

#region API Response Types

// loadCodeAssist response
internal class LoadCodeAssistResponse
{
    [JsonPropertyName("currentTier")]
    public GeminiUserTier? CurrentTier { get; set; }

    [JsonPropertyName("allowedTiers")]
    public List<GeminiUserTier>? AllowedTiers { get; set; }

    [JsonPropertyName("cloudaicompanionProject")]
    public string? CloudaicompanionProject { get; set; }
}

internal class GeminiUserTier
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("isDefault")]
    public bool? IsDefault { get; set; }
}

// onboardUser response (Long Running Operation)
internal class OnboardUserResponse
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("done")]
    public bool? Done { get; set; }

    [JsonPropertyName("response")]
    public OnboardUserResult? Response { get; set; }
}

internal class OnboardUserResult
{
    [JsonPropertyName("cloudaicompanionProject")]
    public CloudProject? CloudaicompanionProject { get; set; }
}

internal class CloudProject
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }
}

// generateContent response
internal class CodeAssistResponse
{
    [JsonPropertyName("response")]
    public GeminiResponse? Response { get; set; }
}

internal class GeminiResponse
{
    [JsonPropertyName("candidates")]
    public List<GeminiCandidate>? Candidates { get; set; }
}

internal class GeminiCandidate
{
    [JsonPropertyName("content")]
    public GeminiContent? Content { get; set; }
}

internal class GeminiContent
{
    [JsonPropertyName("parts")]
    public List<GeminiPart>? Parts { get; set; }
}

internal class GeminiPart
{
    [JsonPropertyName("text")]
    public string? Text { get; set; }
}

#endregion

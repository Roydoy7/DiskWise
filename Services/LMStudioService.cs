using System.Net.Http;
using System.Net.Http.Json;
using DiskWise.Models;

namespace DiskWise.Services;

/// <summary>
/// Service for interacting with LM Studio API
/// </summary>
public class LMStudioService
{
    private readonly HttpClient _httpClient;

    private const string SystemPrompt = """
        You are a Windows system cleanup expert. Analyze the given folder information and determine if it can be safely deleted.

        IMPORTANT: Respond in this EXACT format (no markdown, no extra text):
        LEVEL: SAFE|CAUTION|DANGER
        REASON: One sentence explanation

        Guidelines:
        - SAFE: Cache folders, temp files, build outputs, node_modules, .cache, __pycache__, Temp, tmp, browser caches, log files
        - CAUTION: .git folders (might be needed), old backups, downloads folder contents, vendor folders
        - DANGER: Windows system files, Program Files, user documents (Documents, Pictures, Videos), AppData/Roaming configs, source code

        Example response:
        LEVEL: SAFE
        REASON: npm cache directory that can be rebuilt by running npm install.
        """;

    public LMStudioService(string baseUrl = "http://localhost:1234")
    {
        _httpClient = new HttpClient
        {
            BaseAddress = new Uri(baseUrl),
            Timeout = TimeSpan.FromSeconds(60)
        };
    }

    public void UpdateBaseUrl(string baseUrl)
    {
        _httpClient.BaseAddress = new Uri(baseUrl);
    }

    public async Task<bool> IsAvailableAsync(CancellationToken ct = default)
    {
        try
        {
            var response = await _httpClient.GetAsync("/v1/models", ct);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    public async Task<List<LMStudioModel>> GetModelsAsync(CancellationToken ct = default)
    {
        try
        {
            var response = await _httpClient.GetAsync("/v1/models", ct);
            response.EnsureSuccessStatusCode();

            var result = await response.Content.ReadFromJsonAsync<LMStudioModelsResponse>(ct);
            return result?.Data ?? [];
        }
        catch
        {
            return [];
        }
    }

    public async Task<AIAdvice> GetAdviceAsync(FileSystemItem item, string? modelId = null, CancellationToken ct = default)
    {
        var advice = new AIAdvice { IsAnalyzing = true };

        try
        {
            var userPrompt = BuildEnhancedPrompt(item);

            var request = new ChatCompletionRequest
            {
                Model = modelId ?? "default",
                Messages =
                [
                    new ChatMessage { Role = "system", Content = SystemPrompt },
                    new ChatMessage { Role = "user", Content = userPrompt }
                ],
                Temperature = 0.3,
                MaxTokens = 256
            };

            var response = await _httpClient.PostAsJsonAsync("/v1/chat/completions", request, ct);
            response.EnsureSuccessStatusCode();

            var result = await response.Content.ReadFromJsonAsync<ChatCompletionResponse>(ct);

            if (result?.Choices is { Count: > 0 })
            {
                var content = result.Choices[0].Message.Content;
                ParseAdviceResponse(content, advice);
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
        sb.AppendLine($"System: {item.IsSystem}");
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

        // If parsing failed, try to extract from free-form text
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

    public async Task BatchAnalyzeAsync(
        IEnumerable<FileSystemItem> items,
        string? modelId = null,
        IProgress<int>? progress = null,
        CancellationToken ct = default)
    {
        int count = 0;
        foreach (var item in items)
        {
            if (ct.IsCancellationRequested) break;

            if (item.IsDirectory && item.IsScanned)
            {
                item.Advice = await GetAdviceAsync(item, modelId, ct);
                count++;
                progress?.Report(count);

                // Small delay to avoid overwhelming the API
                await Task.Delay(100, ct);
            }
        }
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
    public async Task<AISearchResult> GenerateSearchKeywordsAsync(string userDescription, string? modelId = null, CancellationToken ct = default)
    {
        var result = new AISearchResult();

        try
        {
            var request = new ChatCompletionRequest
            {
                Model = modelId ?? "default",
                Messages =
                [
                    new ChatMessage { Role = "system", Content = SearchSystemPrompt },
                    new ChatMessage { Role = "user", Content = userDescription }
                ],
                Temperature = 0.3,
                MaxTokens = 512
            };

            var response = await _httpClient.PostAsJsonAsync("/v1/chat/completions", request, ct);
            response.EnsureSuccessStatusCode();

            var chatResult = await response.Content.ReadFromJsonAsync<ChatCompletionResponse>(ct);

            if (chatResult?.Choices is { Count: > 0 })
            {
                var content = chatResult.Choices[0].Message.Content.Trim();
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

#region AI Search Models

public class AISearchResult
{
    public bool Success { get; set; }
    public List<string> Keywords { get; set; } = [];
    public List<string> Extensions { get; set; } = [];
    public string? Error { get; set; }
}

#endregion

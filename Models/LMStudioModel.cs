using System.Text.Json.Serialization;

namespace DiskWise.Models;

/// <summary>
/// LM Studio model information
/// </summary>
public class LMStudioModel
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("object")]
    public string Object { get; set; } = "model";

    [JsonPropertyName("created")]
    public long Created { get; set; }

    [JsonPropertyName("owned_by")]
    public string OwnedBy { get; set; } = string.Empty;

    public string? Path { get; set; }
    public string? Architecture { get; set; }
    public string? Quantization { get; set; }

    public bool IsLoaded { get; set; }

    /// <summary>
    /// Display name for UI
    /// </summary>
    public string DisplayName
    {
        get
        {
            if (string.IsNullOrEmpty(Id)) return "Unknown";
            var parts = Id.Split('/');
            return parts.Length > 1 ? parts[^1] : Id;
        }
    }

    public string StatusDisplay => IsLoaded ? "\u2705 Loaded" : "\u2B1C Not Loaded";
}

/// <summary>
/// LM Studio models list response
/// </summary>
public class LMStudioModelsResponse
{
    [JsonPropertyName("object")]
    public string Object { get; set; } = "list";

    [JsonPropertyName("data")]
    public List<LMStudioModel> Data { get; set; } = [];
}

/// <summary>
/// Chat completion request
/// </summary>
public class ChatCompletionRequest
{
    [JsonPropertyName("model")]
    public string Model { get; set; } = "default";

    [JsonPropertyName("messages")]
    public List<ChatMessage> Messages { get; set; } = [];

    [JsonPropertyName("temperature")]
    public double Temperature { get; set; } = 0.7;

    [JsonPropertyName("max_tokens")]
    public int MaxTokens { get; set; } = 1024;
}

/// <summary>
/// Chat message
/// </summary>
public class ChatMessage
{
    [JsonPropertyName("role")]
    public string Role { get; set; } = "user";

    [JsonPropertyName("content")]
    public string Content { get; set; } = string.Empty;
}

/// <summary>
/// Chat completion response
/// </summary>
public class ChatCompletionResponse
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("choices")]
    public List<ChatChoice> Choices { get; set; } = [];
}

/// <summary>
/// Chat choice in response
/// </summary>
public class ChatChoice
{
    [JsonPropertyName("message")]
    public ChatMessage Message { get; set; } = new();

    [JsonPropertyName("finish_reason")]
    public string FinishReason { get; set; } = string.Empty;
}

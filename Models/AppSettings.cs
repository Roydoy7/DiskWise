namespace DiskWise.Models;

/// <summary>
/// Application settings
/// </summary>
public class AppSettings
{
    // AI Settings
    public AIProvider CurrentAIProvider { get; set; } = AIProvider.LMStudio;
    public string LMStudioUrl { get; set; } = "http://localhost:1234";
    public string? SelectedLMStudioModelId { get; set; }
    public double AITemperature { get; set; } = 0.3;
    public int AIMaxTokens { get; set; } = 256;

    // Scan Settings
    public int CacheExpirationDays { get; set; } = 7;
    public string ExcludePatterns { get; set; } = "";

    // Display Settings
    public bool ShowHiddenFolders { get; set; } = true;
    public bool ShowFiles { get; set; } = true;
    public string SortBy { get; set; } = "Size"; // Size, Name, Date
    public bool SortDescending { get; set; } = true;

    // Language
    public string Language { get; set; } = "zh-CN";

    // Recent Folders (max 10)
    public List<RecentFolderEntry> RecentFolders { get; set; } = [];

    // Window State
    public double WindowWidth { get; set; } = 1200;
    public double WindowHeight { get; set; } = 800;
    public bool IsMaximized { get; set; } = false;
}

/// <summary>
/// Recent folder entry for persistence
/// </summary>
public class RecentFolderEntry
{
    public string Path { get; set; } = string.Empty;
    public DateTime LastAccessed { get; set; }
}

using CommunityToolkit.Mvvm.ComponentModel;

namespace DiskWise.Models;

/// <summary>
/// AI advice level for deletion safety
/// </summary>
public enum AdviceLevel
{
    Unknown,   // Not analyzed yet
    Safe,      // Can be safely deleted
    Caution,   // Delete with caution
    Danger     // Should not delete
}

/// <summary>
/// AI provider options
/// </summary>
public enum AIProvider
{
    None,
    LMStudio,
    Gemini
}

/// <summary>
/// Represents AI-generated advice for a file/folder
/// </summary>
public partial class AIAdvice : ObservableObject
{
    [ObservableProperty]
    private AdviceLevel _level = AdviceLevel.Unknown;

    [ObservableProperty]
    private string _reason = string.Empty;

    [ObservableProperty]
    private bool _isAnalyzing;

    [ObservableProperty]
    private string _error = string.Empty;

    /// <summary>
    /// Display icon based on advice level
    /// </summary>
    public string Icon => Level switch
    {
        AdviceLevel.Safe => "\u2705",      // ✅
        AdviceLevel.Caution => "\u26A0\uFE0F", // ⚠️
        AdviceLevel.Danger => "\u274C",    // ❌
        _ => "--"
    };

    /// <summary>
    /// Color for the advice level
    /// </summary>
    public string Color => Level switch
    {
        AdviceLevel.Safe => "#4CAF50",     // Green
        AdviceLevel.Caution => "#FF9800",  // Orange
        AdviceLevel.Danger => "#F44336",   // Red
        _ => "#808080"                      // Gray
    };
}

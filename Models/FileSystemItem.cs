using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;

namespace DiskWise.Models;

/// <summary>
/// Represents a file or folder in the file system with size information
/// </summary>
public partial class FileSystemItem : ObservableObject
{
    [ObservableProperty]
    private string _path = string.Empty;

    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    private long _size;

    [ObservableProperty]
    private bool _isDirectory;

    [ObservableProperty]
    private bool _isHidden;

    [ObservableProperty]
    private bool _isSystem;

    [ObservableProperty]
    private DateTime _lastModified;

    [ObservableProperty]
    private double _percentage;

    [ObservableProperty]
    private AIAdvice? _advice;

    [ObservableProperty]
    private bool _isScanned;

    [ObservableProperty]
    private bool _isScanning;

    [ObservableProperty]
    private int _fileCount;

    [ObservableProperty]
    private int _folderCount;

    public FileSystemItem? Parent { get; set; }

    public ObservableCollection<FileSystemItem> Children { get; } = [];

    /// <summary>
    /// Formatted size string (e.g., "2.3 GB")
    /// </summary>
    public string SizeDisplay => FormatSize(Size);

    /// <summary>
    /// Icon based on item type
    /// </summary>
    public string Icon => IsDirectory ? "\U0001F4C1" : "\U0001F4C4"; // 📁 or 📄

    /// <summary>
    /// Formatted last modified date (compact display)
    /// </summary>
    public string LastModifiedDisplay
    {
        get
        {
            if (LastModified == default) return "--";
            var diff = DateTime.Now - LastModified;
            if (diff.TotalDays < 1) return "Today";
            if (diff.TotalDays < 2) return "Yesterday";
            if (diff.TotalDays < 7) return $"{(int)diff.TotalDays}d ago";
            if (diff.TotalDays < 30) return $"{(int)(diff.TotalDays / 7)}w ago";
            if (diff.TotalDays < 365) return LastModified.ToString("MM-dd");
            return LastModified.ToString("yy-MM-dd");
        }
    }

    /// <summary>
    /// Format bytes to human-readable size
    /// </summary>
    public static string FormatSize(long bytes)
    {
        if (bytes < 0) return "--";
        if (bytes == 0) return "0 B";

        string[] sizes = ["B", "KB", "MB", "GB", "TB"];
        int order = 0;
        double size = bytes;

        while (size >= 1024 && order < sizes.Length - 1)
        {
            order++;
            size /= 1024;
        }

        return order == 0 ? $"{size:0} {sizes[order]}" : $"{size:0.##} {sizes[order]}";
    }
}

/// <summary>
/// Represents a drive/disk
/// </summary>
public partial class DriveItem : ObservableObject
{
    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    private string _rootPath = string.Empty;

    [ObservableProperty]
    private string _volumeLabel = string.Empty;

    [ObservableProperty]
    private long _totalSize;

    [ObservableProperty]
    private long _usedSize;

    [ObservableProperty]
    private long _freeSize;

    [ObservableProperty]
    private DriveType _driveType;

    [ObservableProperty]
    private bool _isReady;

    public double UsagePercentage => TotalSize > 0 ? (double)UsedSize / TotalSize * 100 : 0;

    public string UsageDisplay => $"{FileSystemItem.FormatSize(UsedSize)} / {FileSystemItem.FormatSize(TotalSize)}";

    public string Icon => DriveType switch
    {
        DriveType.Fixed => "\U0001F4BE",      // 💾
        DriveType.Removable => "\U0001F4BF",  // 💿
        DriveType.Network => "\U0001F5A7",    // 🖧
        DriveType.CDRom => "\U0001F4BF",      // 💿
        _ => "\U0001F4BE"                      // 💾
    };
}

/// <summary>
/// Represents a recently accessed folder
/// </summary>
public partial class RecentFolder : ObservableObject
{
    [ObservableProperty]
    private string _path = string.Empty;

    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    private DateTime _lastAccessed;

    public string LastAccessedDisplay
    {
        get
        {
            var diff = DateTime.Now - LastAccessed;
            if (diff.TotalMinutes < 1) return "Just now";
            if (diff.TotalHours < 1) return $"{(int)diff.TotalMinutes} min ago";
            if (diff.TotalDays < 1) return $"{(int)diff.TotalHours} hours ago";
            if (diff.TotalDays < 2) return "Yesterday";
            if (diff.TotalDays < 7) return $"{(int)diff.TotalDays} days ago";
            return LastAccessed.ToString("yyyy-MM-dd");
        }
    }
}

/// <summary>
/// Represents a segment in the breadcrumb navigation path
/// </summary>
public class BreadcrumbItem
{
    public string Name { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public bool IsLast { get; set; }
}

/// <summary>
/// Aggregated file type info for space breakdown
/// </summary>
public class FileTypeInfo
{
    public string Extension { get; set; } = string.Empty;
    public long TotalSize { get; set; }
    public int FileCount { get; set; }
    public double Percentage { get; set; }

    public string SizeDisplay => FileSystemItem.FormatSize(TotalSize);
}

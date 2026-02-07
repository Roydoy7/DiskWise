using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiskWise.Models;
using DiskWise.Services;

namespace DiskWise.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly NavigationService _navigationService;
    private readonly SettingsService _settingsService;
    private readonly DiskScanService _scanService;
    private readonly LMStudioService _lmStudioService;
    private readonly GeminiService _geminiService;
    private readonly FileOperationService _fileOperationService;
    private readonly ScanCacheService _cacheService;

    [ObservableProperty]
    private string _currentPath = NavigationService.HomePath;

    [ObservableProperty]
    private string _currentPathDisplay = "This PC";

    [ObservableProperty]
    private bool _isAtHome = true;

    [ObservableProperty]
    private bool _canGoBack;

    [ObservableProperty]
    private bool _canGoForward;

    [ObservableProperty]
    private bool _canGoUp;

    [ObservableProperty]
    private bool _isScanning;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    [ObservableProperty]
    private int _scannedItems;

    [ObservableProperty]
    private long _totalSize;

    [ObservableProperty]
    private long _cleanableSize;

    [ObservableProperty]
    private bool _showHiddenFolders = true;

    [ObservableProperty]
    private bool _showFiles = true;

    [ObservableProperty]
    private int _sortByIndex;

    private static readonly string[] SortOptions = ["Size", "Name", "Date"];

    public string SortBy => SortOptions[Math.Clamp(SortByIndex, 0, SortOptions.Length - 1)];

    [ObservableProperty]
    private FileSystemItem? _selectedItem;

    [ObservableProperty]
    private FileSystemItem? _currentFolder;

    [ObservableProperty]
    private AIProvider _currentAIProvider = AIProvider.LMStudio;

    [ObservableProperty]
    private LMStudioModel? _selectedLMStudioModel;

    [ObservableProperty]
    private bool _isLMStudioConnected;

    [ObservableProperty]
    private bool _isAIConnected;

    [ObservableProperty]
    private int _currentAIProviderIndex;

    [ObservableProperty]
    private bool _isAskingAI;

    /// <summary>
    /// Gets the AI advice to display - from SelectedItem if available, otherwise from CurrentFolder
    /// </summary>
    public AIAdvice? DisplayedAdvice => SelectedItem?.Advice ?? CurrentFolder?.Advice;

    /// <summary>
    /// Gets whether there is AI advice to display
    /// </summary>
    public bool HasDisplayedAdvice => DisplayedAdvice != null;

    partial void OnSelectedItemChanged(FileSystemItem? value)
    {
        // Update displayed advice when selection changes
        OnPropertyChanged(nameof(DisplayedAdvice));
        OnPropertyChanged(nameof(HasDisplayedAdvice));
    }

    partial void OnCurrentFolderChanged(FileSystemItem? value)
    {
        // Update displayed advice when current folder changes
        OnPropertyChanged(nameof(DisplayedAdvice));
        OnPropertyChanged(nameof(HasDisplayedAdvice));
    }

    public ObservableCollection<DriveItem> Drives { get; } = [];
    public ObservableCollection<RecentFolder> RecentFolders { get; } = [];
    public ObservableCollection<FileSystemItem> CurrentItems { get; } = [];
    public ObservableCollection<LMStudioModel> LMStudioModels { get; } = [];
    public ObservableCollection<BreadcrumbItem> Breadcrumbs { get; } = [];
    public ObservableCollection<FileSystemItem> SearchResults { get; } = [];

    // Search properties
    [ObservableProperty]
    private string _searchQuery = string.Empty;

    [ObservableProperty]
    private bool _isSearching;

    [ObservableProperty]
    private bool _isSearchMode;

    [ObservableProperty]
    private int _searchResultCount;

    private CancellationTokenSource? _searchCts;

    // Cache for scanned directories - key is path, value is the scanned FileSystemItem
    private readonly Dictionary<string, FileSystemItem> _scanCache = new(StringComparer.OrdinalIgnoreCase);

    public NavigationService Navigation => _navigationService;
    public SettingsService Settings => _settingsService;

    public MainViewModel()
    {
        _navigationService = new NavigationService();
        _settingsService = new SettingsService();
        _scanService = new DiskScanService();
        _lmStudioService = new LMStudioService();
        _geminiService = new GeminiService();
        _fileOperationService = new FileOperationService();
        _cacheService = new ScanCacheService();

        _navigationService.PropertyChanged += (s, e) =>
        {
            CanGoBack = _navigationService.CanGoBack;
            CanGoForward = _navigationService.CanGoForward;
            CanGoUp = _navigationService.CanGoUp;
            IsAtHome = _navigationService.IsAtHome;
        };

        _navigationService.NavigationRequested += async (s, path) =>
        {
            await OnNavigationRequested(path);
        };
    }

    // Handle search query changes with debounce
    partial void OnSearchQueryChanged(string value)
    {
        _ = PerformSearchAsync(value);
    }

    partial void OnCurrentAIProviderIndexChanged(int value)
    {
        CurrentAIProvider = value switch
        {
            0 => AIProvider.LMStudio,
            1 => AIProvider.Gemini,
            _ => AIProvider.None
        };
        _ = CheckAIConnectionAsync();
    }

    private async Task CheckAIConnectionAsync()
    {
        IsAIConnected = false;

        try
        {
            if (CurrentAIProvider == AIProvider.LMStudio)
            {
                IsAIConnected = await _lmStudioService.IsAvailableAsync();
                if (IsAIConnected)
                {
                    StatusMessage = "LM Studio connected";
                }
            }
            else if (CurrentAIProvider == AIProvider.Gemini)
            {
                IsAIConnected = await _geminiService.IsAvailableAsync();
                if (IsAIConnected)
                {
                    StatusMessage = "Gemini connected";
                }
                else
                {
                    StatusMessage = "Gemini: Click 'Ask AI' to login";
                }
            }
        }
        catch
        {
            IsAIConnected = false;
        }
    }

    [RelayCommand]
    private async Task LoginGeminiAsync()
    {
        if (CurrentAIProvider != AIProvider.Gemini)
        {
            StatusMessage = "Please select Gemini as AI provider first";
            return;
        }

        StatusMessage = "Opening browser for Gemini login...";
        var success = await _geminiService.LoginAsync();

        if (success)
        {
            IsAIConnected = true;
            StatusMessage = "Gemini login successful!";
        }
        else
        {
            StatusMessage = "Gemini login failed or cancelled";
        }
    }

    public async Task InitializeAsync()
    {
        await _settingsService.LoadAsync();
        await _cacheService.InitializeAsync();
        ApplySettings();
        await LoadDrivesAsync();
        LoadRecentFolders();
        _navigationService.NavigateTo(NavigationService.HomePath);
    }

    private void ApplySettings()
    {
        ShowHiddenFolders = _settingsService.Settings.ShowHiddenFolders;
        ShowFiles = _settingsService.Settings.ShowFiles;
        SortByIndex = Array.IndexOf(SortOptions, _settingsService.Settings.SortBy);
        if (SortByIndex < 0) SortByIndex = 0; // Default to Size

        // Set index instead of enum to trigger proper UI update
        CurrentAIProviderIndex = _settingsService.Settings.CurrentAIProvider switch
        {
            AIProvider.LMStudio => 0,
            AIProvider.Gemini => 1,
            _ => 2 // None
        };
    }

    private async Task LoadDrivesAsync()
    {
        await Task.Run(() =>
        {
            var drives = DriveInfo.GetDrives()
                .Where(d => d.IsReady)
                .Select(d => new DriveItem
                {
                    Name = d.Name.TrimEnd('\\'),
                    RootPath = d.Name,
                    VolumeLabel = string.IsNullOrEmpty(d.VolumeLabel) ? GetDriveTypeLabel(d.DriveType) : d.VolumeLabel,
                    TotalSize = d.TotalSize,
                    FreeSize = d.TotalFreeSpace,
                    UsedSize = d.TotalSize - d.TotalFreeSpace,
                    DriveType = d.DriveType,
                    IsReady = d.IsReady
                });

            App.Current.Dispatcher.Invoke(() =>
            {
                Drives.Clear();
                foreach (var drive in drives)
                {
                    Drives.Add(drive);
                }
            });
        });
    }

    private static string GetDriveTypeLabel(DriveType type) => type switch
    {
        DriveType.Fixed => "Local Disk",
        DriveType.Removable => "Removable",
        DriveType.Network => "Network",
        DriveType.CDRom => "CD/DVD",
        _ => "Drive"
    };

    private void LoadRecentFolders()
    {
        RecentFolders.Clear();
        foreach (var entry in _settingsService.Settings.RecentFolders)
        {
            if (Directory.Exists(entry.Path))
            {
                RecentFolders.Add(new RecentFolder
                {
                    Path = entry.Path,
                    Name = Path.GetFileName(entry.Path) ?? entry.Path,
                    LastAccessed = entry.LastAccessed
                });
            }
        }
    }

    private async Task OnNavigationRequested(string path)
    {
        CurrentPath = path;
        UpdateBreadcrumbs(path);

        if (path == NavigationService.HomePath)
        {
            CurrentPathDisplay = "This PC";
            IsAtHome = true;
            CurrentFolder = null;
            CurrentItems.Clear();
            await LoadDrivesAsync();
            LoadRecentFolders();
        }
        else
        {
            CurrentPathDisplay = path;
            IsAtHome = false;
            await LoadFolderContentsAsync(path);
            _settingsService.AddRecentFolder(path);
        }
    }

    private void UpdateBreadcrumbs(string path)
    {
        Breadcrumbs.Clear();

        if (path == NavigationService.HomePath)
        {
            Breadcrumbs.Add(new BreadcrumbItem { Name = "This PC", Path = NavigationService.HomePath, IsLast = true });
            return;
        }

        // Add "This PC" as root
        Breadcrumbs.Add(new BreadcrumbItem { Name = "This PC", Path = NavigationService.HomePath, IsLast = false });

        // Parse the path into segments
        var segments = new List<(string Name, string FullPath)>();
        var current = path;

        while (!string.IsNullOrEmpty(current))
        {
            var name = Path.GetFileName(current);
            if (string.IsNullOrEmpty(name))
            {
                // This is a drive root (e.g., "C:\")
                name = current.TrimEnd('\\');
            }
            segments.Insert(0, (name, current));

            var parent = Path.GetDirectoryName(current);
            if (parent == current) break; // Reached root
            current = parent;
        }

        // Add each segment as a breadcrumb
        for (int i = 0; i < segments.Count; i++)
        {
            var (name, fullPath) = segments[i];
            Breadcrumbs.Add(new BreadcrumbItem
            {
                Name = name,
                Path = fullPath,
                IsLast = i == segments.Count - 1
            });
        }
    }

    [RelayCommand]
    private void NavigateToBreadcrumb(BreadcrumbItem item)
    {
        if (item != null && !item.IsLast)
        {
            _navigationService.NavigateTo(item.Path);
        }
    }

    private async Task LoadFolderContentsAsync(string path)
    {
        CurrentItems.Clear();
        StatusMessage = "Loading...";

        try
        {
            // Check if we have cached scan results for this path
            FileSystemItem? cachedFolder = null;
            if (_scanCache.TryGetValue(path, out var cached))
            {
                cachedFolder = cached;
            }
            else
            {
                // Try to load from disk cache first
                var diskCached = await _cacheService.GetCachedScanAsync(path);
                if (diskCached != null)
                {
                    _scanCache[path] = diskCached;
                    cachedFolder = diskCached;
                    StatusMessage = "Loaded from cache";
                }
                else
                {
                    // Check if this path is a child of a cached folder
                    foreach (var kvp in _scanCache)
                    {
                        var found = FindItemByPath(kvp.Value, path);
                        if (found != null)
                        {
                            cachedFolder = found;
                            break;
                        }
                    }
                }
            }

            await Task.Run(() =>
            {
                var dirInfo = new DirectoryInfo(path);
                var items = new List<FileSystemItem>();

                // Load directories
                foreach (var d in dirInfo.EnumerateDirectories())
                {
                    try
                    {
                        if (!ShowHiddenFolders && (d.Attributes & FileAttributes.Hidden) != 0)
                            continue;

                        // Try to get cached data for this subdirectory
                        var cachedChild = cachedFolder?.Children.FirstOrDefault(c =>
                            c.Path.Equals(d.FullName, StringComparison.OrdinalIgnoreCase));

                        var item = new FileSystemItem
                        {
                            Path = d.FullName,
                            Name = d.Name,
                            IsDirectory = true,
                            IsHidden = (d.Attributes & FileAttributes.Hidden) != 0,
                            IsSystem = (d.Attributes & FileAttributes.System) != 0,
                            LastModified = d.LastWriteTime,
                            // Use cached data if available
                            Size = cachedChild?.Size ?? -1,
                            FileCount = cachedChild?.FileCount ?? 0,
                            FolderCount = cachedChild?.FolderCount ?? 0,
                            IsScanned = cachedChild?.IsScanned ?? false,
                            Advice = cachedChild?.Advice
                        };

                        // Copy children reference for further navigation
                        if (cachedChild != null)
                        {
                            foreach (var child in cachedChild.Children)
                            {
                                item.Children.Add(child);
                            }
                        }

                        items.Add(item);
                    }
                    catch { }
                }

                // Load files if enabled
                if (ShowFiles)
                {
                    foreach (var f in dirInfo.EnumerateFiles())
                    {
                        try
                        {
                            if (!ShowHiddenFolders && (f.Attributes & FileAttributes.Hidden) != 0)
                                continue;

                            items.Add(new FileSystemItem
                            {
                                Path = f.FullName,
                                Name = f.Name,
                                IsDirectory = false,
                                IsHidden = (f.Attributes & FileAttributes.Hidden) != 0,
                                IsSystem = (f.Attributes & FileAttributes.System) != 0,
                                LastModified = f.LastWriteTime,
                                Size = f.Length,
                                IsScanned = true
                            });
                        }
                        catch { }
                    }
                }

                // Calculate percentages if we have scanned data
                var totalSize = items.Where(i => i.Size > 0).Sum(i => i.Size);
                if (totalSize > 0)
                {
                    foreach (var item in items)
                    {
                        item.Percentage = item.Size > 0 ? (double)item.Size / totalSize * 100 : 0;
                    }
                }

                // Sort
                items = SortBy switch
                {
                    "Name" => items.OrderBy(i => !i.IsDirectory).ThenBy(i => i.Name).ToList(),
                    "Date" => items.OrderBy(i => !i.IsDirectory).ThenByDescending(i => i.LastModified).ToList(),
                    _ => items.OrderBy(i => !i.IsDirectory).ThenByDescending(i => i.Size).ToList() // Size
                };

                App.Current.Dispatcher.Invoke(() =>
                {
                    foreach (var item in items)
                    {
                        CurrentItems.Add(item);
                    }
                });
            });

            var folderCount = CurrentItems.Count(i => i.IsDirectory);
            var fileCount = CurrentItems.Count(i => !i.IsDirectory);
            var hasScannedItems = CurrentItems.Any(i => i.IsScanned && i.IsDirectory);

            if (hasScannedItems)
            {
                var total = CurrentItems.Where(i => i.Size > 0).Sum(i => i.Size);
                TotalSize = total;
                StatusMessage = $"{folderCount} folders, {fileCount} files | Total: {FileSystemItem.FormatSize(total)} (cached)";
            }
            else
            {
                StatusMessage = $"{folderCount} folders, {fileCount} files";
            }
        }
        catch (UnauthorizedAccessException)
        {
            StatusMessage = "Access denied";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error: {ex.Message}";
        }
    }

    private static FileSystemItem? FindItemByPath(FileSystemItem root, string path)
    {
        if (root.Path.Equals(path, StringComparison.OrdinalIgnoreCase))
            return root;

        foreach (var child in root.Children)
        {
            var found = FindItemByPath(child, path);
            if (found != null)
                return found;
        }

        return null;
    }

    [RelayCommand]
    private void GoBack() => _navigationService.GoBack();

    [RelayCommand]
    private void GoForward() => _navigationService.GoForward();

    [RelayCommand]
    private void GoUp() => _navigationService.GoUp();

    [RelayCommand]
    private void GoHome() => _navigationService.GoHome();

    [RelayCommand]
    private void NavigateTo(string path)
    {
        if (string.IsNullOrEmpty(path)) return;
        _navigationService.NavigateTo(path);
    }

    private CancellationTokenSource? _scanCts;
    private DateTime _lastProgressUpdate = DateTime.MinValue;

    [RelayCommand]
    private async Task ScanCurrentFolderAsync()
    {
        if (IsAtHome || string.IsNullOrEmpty(CurrentPath)) return;

        _scanCts?.Cancel();
        _scanCts = new CancellationTokenSource();

        IsScanning = true;
        ScannedItems = 0;
        TotalSize = 0;

        try
        {
            var progress = new Progress<ScanProgress>(p =>
            {
                // Throttle UI updates to max 10 per second
                var now = DateTime.Now;
                if ((now - _lastProgressUpdate).TotalMilliseconds < 100) return;
                _lastProgressUpdate = now;

                ScannedItems = p.ScannedItems;
                StatusMessage = $"Scanning: {Path.GetFileName(p.CurrentPath)}";
            });

            var result = await _scanService.ScanDirectoryAsync(CurrentPath, progress, _scanCts.Token);

            // Cache the scan result for future navigation (memory)
            _scanCache[CurrentPath] = result;

            // Persist to disk cache
            _ = _cacheService.SaveScanAsync(CurrentPath, result);

            // Update items with scan results in background
            var updatedItems = await Task.Run(() =>
            {
                var items = CurrentItems.ToList();
                foreach (var item in items)
                {
                    var scannedItem = result.Children.FirstOrDefault(c =>
                        c.Path.Equals(item.Path, StringComparison.OrdinalIgnoreCase));
                    if (scannedItem != null)
                    {
                        item.Size = scannedItem.Size;
                        item.FileCount = scannedItem.FileCount;
                        item.FolderCount = scannedItem.FolderCount;
                        item.IsScanned = true;
                        // Copy children for nested navigation
                        item.Children.Clear();
                        foreach (var child in scannedItem.Children)
                        {
                            item.Children.Add(child);
                        }
                    }
                }

                // Recalculate percentages
                var total = items.Where(i => i.Size > 0).Sum(i => i.Size);
                foreach (var item in items)
                {
                    item.Percentage = total > 0 ? (double)item.Size / total * 100 : 0;
                }

                // Re-sort by size
                return (items.OrderBy(i => !i.IsDirectory).ThenByDescending(i => i.Size).ToList(), total);
            });

            TotalSize = updatedItems.total;

            // Update UI
            CurrentItems.Clear();
            foreach (var item in updatedItems.Item1)
            {
                CurrentItems.Add(item);
            }

            StatusMessage = $"Scanned {ScannedItems} items | Total: {FileSystemItem.FormatSize(TotalSize)}";
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "Scan cancelled";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Scan error: {ex.Message}";
        }
        finally
        {
            IsScanning = false;
        }
    }

    [RelayCommand]
    private void StopScan()
    {
        _scanCts?.Cancel();
        _scanService.Cancel();
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (IsAtHome)
        {
            await LoadDrivesAsync();
            LoadRecentFolders();
        }
        else
        {
            await LoadFolderContentsAsync(CurrentPath);
        }
    }

    [RelayCommand]
    private void ItemDoubleClicked(FileSystemItem? item)
    {
        if (item == null) return;

        if (item.IsDirectory)
        {
            _navigationService.NavigateTo(item.Path);
        }
    }

    [RelayCommand]
    private void DriveDoubleClicked(DriveItem? drive)
    {
        if (drive == null) return;
        _navigationService.NavigateTo(drive.RootPath);
    }

    [RelayCommand]
    private void RecentFolderClicked(RecentFolder? folder)
    {
        if (folder == null) return;
        _navigationService.NavigateTo(folder.Path);
    }

    partial void OnShowHiddenFoldersChanged(bool value)
    {
        _settingsService.Settings.ShowHiddenFolders = value;
        if (!IsAtHome) _ = LoadFolderContentsAsync(CurrentPath);
    }

    partial void OnShowFilesChanged(bool value)
    {
        _settingsService.Settings.ShowFiles = value;
        if (!IsAtHome) _ = LoadFolderContentsAsync(CurrentPath);
    }

    partial void OnSortByIndexChanged(int value)
    {
        _settingsService.Settings.SortBy = SortBy;
        if (!IsAtHome) _ = LoadFolderContentsAsync(CurrentPath);
    }

    partial void OnCurrentAIProviderChanged(AIProvider value)
    {
        _settingsService.Settings.CurrentAIProvider = value;
        // Reset connection status when provider changes
        IsAIConnected = false;
    }

    public async Task SaveSettingsAsync()
    {
        await _settingsService.SaveAsync();
    }

    [RelayCommand]
    private async Task AskAIAsync()
    {
        // If no item selected, analyze the current directory
        FileSystemItem? targetItem = SelectedItem;

        if (targetItem == null || !targetItem.IsDirectory)
        {
            // Create a temporary item for the current folder
            if (string.IsNullOrEmpty(CurrentPath) || CurrentPath == NavigationService.HomePath)
            {
                StatusMessage = "Please navigate to a folder first";
                return;
            }

            targetItem = new FileSystemItem
            {
                Path = CurrentPath,
                Name = Path.GetFileName(CurrentPath) ?? CurrentPath,
                IsDirectory = true,
                Size = TotalSize,
                IsScanned = TotalSize > 0
            };

            // Store as CurrentFolder for display
            CurrentFolder = targetItem;
        }

        if (CurrentAIProvider == AIProvider.None)
        {
            StatusMessage = "Please select an AI provider";
            return;
        }

        IsAskingAI = true;
        targetItem.Advice = new AIAdvice { IsAnalyzing = true };
        StatusMessage = $"Asking AI about: {targetItem.Name}...";

        try
        {
            AIAdvice advice;

            if (CurrentAIProvider == AIProvider.LMStudio)
            {
                if (!await _lmStudioService.IsAvailableAsync())
                {
                    StatusMessage = "LM Studio is not available. Please make sure it's running.";
                    targetItem.Advice = null;
                    return;
                }
                advice = await _lmStudioService.GetAdviceAsync(targetItem);
            }
            else if (CurrentAIProvider == AIProvider.Gemini)
            {
                if (!await _geminiService.IsAvailableAsync())
                {
                    // Try to login
                    StatusMessage = "Gemini login required. Opening browser...";
                    var loginSuccess = await _geminiService.LoginAsync();
                    if (!loginSuccess)
                    {
                        StatusMessage = "Gemini login failed or cancelled.";
                        targetItem.Advice = null;
                        return;
                    }
                    StatusMessage = "Gemini login successful!";
                    IsAIConnected = true;
                }
                advice = await _geminiService.GetAdviceAsync(targetItem);
            }
            else
            {
                return;
            }

            targetItem.Advice = advice;

            // Notify UI about advice changes
            if (SelectedItem != null && ReferenceEquals(targetItem, SelectedItem))
            {
                OnPropertyChanged(nameof(SelectedItem));
            }
            else if (SelectedItem == null && CurrentFolder != null)
            {
                // If we analyzed the current folder (not a selected item), update UI
                CurrentFolder.Advice = advice;
                OnPropertyChanged(nameof(CurrentFolder));
            }

            // Always notify DisplayedAdvice changed
            OnPropertyChanged(nameof(DisplayedAdvice));
            OnPropertyChanged(nameof(HasDisplayedAdvice));

            if (!string.IsNullOrEmpty(advice.Error))
            {
                StatusMessage = $"AI Error: {advice.Error}";
            }
            else
            {
                StatusMessage = $"AI: {advice.Level} - {advice.Reason}";
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"AI Error: {ex.Message}";
            targetItem.Advice = new AIAdvice
            {
                Level = AdviceLevel.Unknown,
                Error = ex.Message
            };
        }
        finally
        {
            IsAskingAI = false;
        }
    }

    [RelayCommand]
    private void OpenInExplorer()
    {
        // If an item is selected, open its location; otherwise open the current folder
        var pathToOpen = SelectedItem?.Path ?? CurrentPath;

        // Don't open if at home or path is invalid
        if (string.IsNullOrEmpty(pathToOpen) || pathToOpen == NavigationService.HomePath)
        {
            StatusMessage = "Please navigate to a folder first";
            return;
        }

        _fileOperationService.OpenInExplorer(pathToOpen);
    }

    [RelayCommand]
    private async Task DeleteSelectedAsync()
    {
        if (SelectedItem == null) return;

        // Show warning if AI advises against deletion
        if (SelectedItem.Advice?.Level == AdviceLevel.Danger)
        {
            StatusMessage = "Warning: AI advises against deleting this item!";
        }

        // For now, just move to recycle bin
        var (success, error) = await _fileOperationService.MoveToRecycleBinAsync(SelectedItem.Path);

        if (success)
        {
            StatusMessage = $"Moved to Recycle Bin: {SelectedItem.Name}";
            CurrentItems.Remove(SelectedItem);
            SelectedItem = null;
        }
        else
        {
            StatusMessage = $"Delete failed: {error}";
        }
    }

    #region Context Menu Commands

    [RelayCommand]
    private void CopyPath(FileSystemItem? item)
    {
        var path = item?.Path ?? SelectedItem?.Path;
        if (!string.IsNullOrEmpty(path))
        {
            Clipboard.SetText(path);
            StatusMessage = $"Copied: {path}";
        }
    }

    [RelayCommand]
    private void CopyName(FileSystemItem? item)
    {
        var name = item?.Name ?? SelectedItem?.Name;
        if (!string.IsNullOrEmpty(name))
        {
            Clipboard.SetText(name);
            StatusMessage = $"Copied: {name}";
        }
    }

    [RelayCommand]
    private void OpenFile(FileSystemItem? item)
    {
        item ??= SelectedItem;
        if (item == null) return;

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = item.Path,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            StatusMessage = $"Failed to open: {ex.Message}";
        }
    }

    [RelayCommand]
    private void OpenInTerminal(FileSystemItem? item)
    {
        var path = item?.Path ?? CurrentPath;
        if (string.IsNullOrEmpty(path) || path == NavigationService.HomePath) return;

        // If it's a file, use its parent directory
        var folderPath = item != null && !item.IsDirectory
            ? Path.GetDirectoryName(item.Path) ?? path
            : path;

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "wt.exe", // Windows Terminal
                Arguments = $"-d \"{folderPath}\"",
                UseShellExecute = true
            });
        }
        catch
        {
            // Fallback to cmd if Windows Terminal not available
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    WorkingDirectory = folderPath,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                StatusMessage = $"Failed to open terminal: {ex.Message}";
            }
        }
    }

    [RelayCommand]
    private void OpenInExplorerItem(FileSystemItem? item)
    {
        var pathToOpen = item?.Path ?? SelectedItem?.Path ?? CurrentPath;
        if (string.IsNullOrEmpty(pathToOpen) || pathToOpen == NavigationService.HomePath)
        {
            StatusMessage = "Please navigate to a folder first";
            return;
        }
        _fileOperationService.OpenInExplorer(pathToOpen);
    }

    [RelayCommand]
    private async Task DeletePermanentlyAsync(FileSystemItem? item)
    {
        item ??= SelectedItem;
        if (item == null) return;

        var result = MessageBox.Show(
            $"Permanently delete \"{item.Name}\"?\n\nThis action cannot be undone!",
            "Confirm Permanent Delete",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (result != MessageBoxResult.Yes) return;

        var (success, error) = await _fileOperationService.DeletePermanentlyAsync(item.Path);

        if (success)
        {
            StatusMessage = $"Permanently deleted: {item.Name}";
            CurrentItems.Remove(item);
            if (SelectedItem == item) SelectedItem = null;
        }
        else
        {
            StatusMessage = $"Delete failed: {error}";
        }
    }

    [RelayCommand]
    private async Task DeleteToRecycleBinAsync(FileSystemItem? item)
    {
        item ??= SelectedItem;
        if (item == null) return;

        if (item.Advice?.Level == AdviceLevel.Danger)
        {
            StatusMessage = "Warning: AI advises against deleting this item!";
        }

        var (success, error) = await _fileOperationService.MoveToRecycleBinAsync(item.Path);

        if (success)
        {
            StatusMessage = $"Moved to Recycle Bin: {item.Name}";
            CurrentItems.Remove(item);
            if (SelectedItem == item) SelectedItem = null;
        }
        else
        {
            StatusMessage = $"Delete failed: {error}";
        }
    }

    [RelayCommand]
    private void ScanFolder(FileSystemItem? item)
    {
        if (item == null || !item.IsDirectory) return;
        _navigationService.NavigateTo(item.Path);
        // Trigger scan after navigation
        _ = ScanCurrentFolderAsync();
    }

    [RelayCommand]
    private async Task AskAIForItemAsync(FileSystemItem? item)
    {
        if (item == null) return;

        // Temporarily select this item so the existing AI flow works
        SelectedItem = item;
        await AskAIAsync();
    }

    #endregion

    #region Search Functionality

    private async Task PerformSearchAsync(string query)
    {
        // Cancel previous search
        _searchCts?.Cancel();
        _searchCts = new CancellationTokenSource();
        var token = _searchCts.Token;

        // Clear search if query is empty
        if (string.IsNullOrWhiteSpace(query))
        {
            IsSearchMode = false;
            SearchResults.Clear();
            SearchResultCount = 0;
            return;
        }

        // Wait for user to stop typing (debounce 300ms)
        try
        {
            await Task.Delay(300, token);
        }
        catch (TaskCanceledException)
        {
            return;
        }

        IsSearching = true;
        IsSearchMode = true;
        SearchResults.Clear();

        try
        {
            var results = new List<FileSystemItem>();
            var searchLower = query.ToLowerInvariant();

            // Search in current folder's scanned data
            if (_scanCache.TryGetValue(CurrentPath, out var cachedItem))
            {
                SearchInItem(cachedItem, searchLower, results, token);
            }
            else
            {
                // Try to load from disk cache
                var diskCached = await _cacheService.GetCachedScanAsync(CurrentPath);
                if (diskCached != null)
                {
                    _scanCache[CurrentPath] = diskCached;
                    SearchInItem(diskCached, searchLower, results, token);
                }
                else
                {
                    // No cache, search only in currently displayed items
                    foreach (var item in CurrentItems)
                    {
                        if (token.IsCancellationRequested) break;
                        if (item.Name.Contains(searchLower, StringComparison.OrdinalIgnoreCase))
                        {
                            results.Add(item);
                        }
                    }
                }
            }

            if (token.IsCancellationRequested) return;

            // Sort results by size descending
            var sortedResults = results
                .OrderBy(r => !r.IsDirectory)
                .ThenByDescending(r => r.Size)
                .Take(500) // Limit results
                .ToList();

            SearchResultCount = results.Count;

            foreach (var result in sortedResults)
            {
                SearchResults.Add(result);
            }

            StatusMessage = $"Found {SearchResultCount} items matching \"{query}\"";
        }
        catch (OperationCanceledException)
        {
            // Search was cancelled
        }
        catch (Exception ex)
        {
            StatusMessage = $"Search error: {ex.Message}";
        }
        finally
        {
            IsSearching = false;
        }
    }

    private void SearchInItem(FileSystemItem item, string query, List<FileSystemItem> results, CancellationToken token)
    {
        if (token.IsCancellationRequested) return;

        // Check current item's name
        if (item.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
        {
            results.Add(item);
        }

        // Search in children
        foreach (var child in item.Children)
        {
            SearchInItem(child, query, results, token);
        }
    }

    [RelayCommand]
    private void ClearSearch()
    {
        SearchQuery = string.Empty;
        IsSearchMode = false;
        SearchResults.Clear();
        SearchResultCount = 0;
    }

    [RelayCommand]
    private void NavigateToSearchResult(FileSystemItem? item)
    {
        if (item == null) return;

        // Clear search first
        ClearSearch();

        if (item.IsDirectory)
        {
            // Navigate to the folder
            _navigationService.NavigateTo(item.Path);
        }
        else
        {
            // Navigate to parent folder and select the file
            var parentPath = Path.GetDirectoryName(item.Path);
            if (!string.IsNullOrEmpty(parentPath))
            {
                _navigationService.NavigateTo(parentPath);
                // Note: Would need to implement delayed selection after navigation
            }
        }
    }

    [RelayCommand]
    private async Task ClearCacheAsync()
    {
        await _cacheService.ClearAllCacheAsync();
        _scanCache.Clear();
        StatusMessage = "Cache cleared";
    }

    /// <summary>
    /// Check if current path has cached scan data
    /// </summary>
    public bool HasCachedScan => _scanCache.ContainsKey(CurrentPath) || _cacheService.HasValidCache(CurrentPath);

    #endregion

    #region AI Search

    [ObservableProperty]
    private string _aiSearchQuery = string.Empty;

    [ObservableProperty]
    private bool _isAISearching;

    [ObservableProperty]
    private List<string> _aiSearchKeywords = [];

    [RelayCommand]
    private async Task AISearchAsync()
    {
        if (string.IsNullOrWhiteSpace(AiSearchQuery))
        {
            StatusMessage = "Please enter a description of what you're looking for";
            return;
        }

        if (CurrentAIProvider == AIProvider.None)
        {
            StatusMessage = "Please select an AI provider first";
            return;
        }

        if (!IsAIConnected)
        {
            StatusMessage = "AI not connected. Please check your connection.";
            return;
        }

        IsAISearching = true;
        StatusMessage = "AI is analyzing your request...";

        try
        {
            AISearchResult result;

            if (CurrentAIProvider == AIProvider.LMStudio)
            {
                result = await _lmStudioService.GenerateSearchKeywordsAsync(AiSearchQuery);
            }
            else
            {
                result = await _geminiService.GenerateSearchKeywordsAsync(AiSearchQuery);
            }

            if (!result.Success)
            {
                StatusMessage = $"AI Search failed: {result.Error ?? "Unknown error"}";
                return;
            }

            // Store and display AI's keywords
            AiSearchKeywords = result.Keywords;

            // Perform search with AI-generated keywords
            await PerformAISearchWithKeywordsAsync(result.Keywords, result.Extensions);

            StatusMessage = $"Found {SearchResultCount} items";
        }
        catch (Exception ex)
        {
            StatusMessage = $"AI Search error: {ex.Message}";
        }
        finally
        {
            IsAISearching = false;
        }
    }

    private async Task PerformAISearchWithKeywordsAsync(List<string> keywords, List<string> extensions)
    {
        _searchCts?.Cancel();
        _searchCts = new CancellationTokenSource();
        var token = _searchCts.Token;

        IsSearchMode = true;
        SearchResults.Clear();

        var results = new List<FileSystemItem>();

        // Search in current folder's scanned data
        FileSystemItem? cachedItem = null;
        if (_scanCache.TryGetValue(CurrentPath, out var cached))
        {
            cachedItem = cached;
        }
        else
        {
            var diskCached = await _cacheService.GetCachedScanAsync(CurrentPath);
            if (diskCached != null)
            {
                _scanCache[CurrentPath] = diskCached;
                cachedItem = diskCached;
            }
        }

        if (cachedItem != null)
        {
            SearchWithMultipleKeywords(cachedItem, keywords, extensions, results, token);
        }
        else
        {
            // Search only in currently displayed items
            foreach (var item in CurrentItems)
            {
                if (token.IsCancellationRequested) break;
                if (MatchesAnyKeyword(item.Name, keywords, extensions))
                {
                    results.Add(item);
                }
            }
        }

        // Sort and limit results
        var sortedResults = results
            .OrderBy(r => !r.IsDirectory)
            .ThenByDescending(r => r.Size)
            .Take(500)
            .ToList();

        SearchResultCount = results.Count;

        foreach (var result in sortedResults)
        {
            SearchResults.Add(result);
        }
    }

    private void SearchWithMultipleKeywords(FileSystemItem item, List<string> keywords, List<string> extensions, List<FileSystemItem> results, CancellationToken token)
    {
        if (token.IsCancellationRequested) return;

        if (MatchesAnyKeyword(item.Name, keywords, extensions))
        {
            results.Add(item);
        }

        foreach (var child in item.Children)
        {
            SearchWithMultipleKeywords(child, keywords, extensions, results, token);
        }
    }

    private static bool MatchesAnyKeyword(string name, List<string> keywords, List<string> extensions)
    {
        // Check if name matches any keyword
        foreach (var keyword in keywords)
        {
            if (name.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        // Check if name has any of the specified extensions
        foreach (var ext in extensions)
        {
            if (name.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    [RelayCommand]
    private void ClearAISearch()
    {
        AiSearchQuery = string.Empty;
        AiSearchKeywords = [];
        ClearSearch();
    }

    #endregion
}

public class ScanProgress
{
    public int ScannedItems { get; set; }
    public string CurrentPath { get; set; } = string.Empty;
}

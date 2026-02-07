using System.IO;
using DiskWise.Models;
using DiskWise.ViewModels;

namespace DiskWise.Services;

/// <summary>
/// High-performance disk scanning service
/// </summary>
public class DiskScanService
{
    private CancellationTokenSource? _cts;
    private int _scannedItems;

    public void Cancel()
    {
        _cts?.Cancel();
    }

    public async Task<FileSystemItem> ScanDirectoryAsync(
        string path,
        IProgress<ScanProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _scannedItems = 0;

        var root = new FileSystemItem
        {
            Path = path,
            Name = Path.GetFileName(path) ?? path,
            IsDirectory = true
        };

        try
        {
            await ScanDirectoryRecursiveAsync(root, progress, _cts.Token);
        }
        catch (OperationCanceledException)
        {
            // Scan was cancelled
        }

        return root;
    }

    private async Task ScanDirectoryRecursiveAsync(
        FileSystemItem item,
        IProgress<ScanProgress>? progress,
        CancellationToken ct)
    {
        if (ct.IsCancellationRequested) return;

        try
        {
            var dirInfo = new DirectoryInfo(item.Path);

            // Get subdirectories
            var subDirs = new List<FileSystemItem>();
            try
            {
                foreach (var dir in dirInfo.EnumerateDirectories())
                {
                    if (ct.IsCancellationRequested) return;

                    var subItem = new FileSystemItem
                    {
                        Path = dir.FullName,
                        Name = dir.Name,
                        IsDirectory = true,
                        IsHidden = (dir.Attributes & FileAttributes.Hidden) != 0,
                        IsSystem = (dir.Attributes & FileAttributes.System) != 0,
                        LastModified = dir.LastWriteTime,
                        Parent = item
                    };
                    subDirs.Add(subItem);
                    item.Children.Add(subItem);
                }
            }
            catch (UnauthorizedAccessException) { }
            catch (DirectoryNotFoundException) { }

            // Get files
            long totalFileSize = 0;
            int fileCount = 0;
            try
            {
                foreach (var file in dirInfo.EnumerateFiles())
                {
                    if (ct.IsCancellationRequested) return;

                    try
                    {
                        totalFileSize += file.Length;
                        fileCount++;
                        _scannedItems++;

                        // Report progress periodically
                        if (_scannedItems % 100 == 0)
                        {
                            progress?.Report(new ScanProgress
                            {
                                ScannedItems = _scannedItems,
                                CurrentPath = item.Path
                            });
                        }
                    }
                    catch { }
                }
            }
            catch (UnauthorizedAccessException) { }
            catch (DirectoryNotFoundException) { }

            item.FileCount = fileCount;

            // Scan subdirectories in parallel (with limited concurrency)
            var options = new ParallelOptions
            {
                MaxDegreeOfParallelism = Environment.ProcessorCount,
                CancellationToken = ct
            };

            await Parallel.ForEachAsync(subDirs, options, async (subDir, token) =>
            {
                await ScanDirectoryRecursiveAsync(subDir, progress, token);
            });

            // Calculate total size (files + subdirectories)
            item.Size = totalFileSize + item.Children.Sum(c => c.Size);
            item.FolderCount = subDirs.Count + item.Children.Sum(c => c.FolderCount);
            item.IsScanned = true;

            _scannedItems++;
            progress?.Report(new ScanProgress
            {
                ScannedItems = _scannedItems,
                CurrentPath = item.Path
            });
        }
        catch (UnauthorizedAccessException)
        {
            item.Size = 0;
            item.IsScanned = true;
        }
        catch (Exception)
        {
            item.Size = 0;
            item.IsScanned = true;
        }
    }

    /// <summary>
    /// Quick scan to get immediate children sizes only (not recursive)
    /// </summary>
    public async Task<List<FileSystemItem>> QuickScanAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        var items = new List<FileSystemItem>();

        await Task.Run(() =>
        {
            try
            {
                var dirInfo = new DirectoryInfo(path);

                // Scan directories
                foreach (var dir in dirInfo.EnumerateDirectories())
                {
                    if (cancellationToken.IsCancellationRequested) break;

                    var item = new FileSystemItem
                    {
                        Path = dir.FullName,
                        Name = dir.Name,
                        IsDirectory = true,
                        IsHidden = (dir.Attributes & FileAttributes.Hidden) != 0,
                        IsSystem = (dir.Attributes & FileAttributes.System) != 0,
                        LastModified = dir.LastWriteTime,
                        Size = GetDirectorySize(dir, cancellationToken),
                        IsScanned = true
                    };
                    items.Add(item);
                }

                // Scan files
                foreach (var file in dirInfo.EnumerateFiles())
                {
                    if (cancellationToken.IsCancellationRequested) break;

                    try
                    {
                        var item = new FileSystemItem
                        {
                            Path = file.FullName,
                            Name = file.Name,
                            IsDirectory = false,
                            IsHidden = (file.Attributes & FileAttributes.Hidden) != 0,
                            IsSystem = (file.Attributes & FileAttributes.System) != 0,
                            LastModified = file.LastWriteTime,
                            Size = file.Length,
                            IsScanned = true
                        };
                        items.Add(item);
                    }
                    catch { }
                }
            }
            catch { }
        }, cancellationToken);

        return items;
    }

    private static long GetDirectorySize(DirectoryInfo dir, CancellationToken ct)
    {
        long size = 0;

        try
        {
            // Sum file sizes
            foreach (var file in dir.EnumerateFiles("*", SearchOption.AllDirectories))
            {
                if (ct.IsCancellationRequested) break;
                try
                {
                    size += file.Length;
                }
                catch { }
            }
        }
        catch { }

        return size;
    }
}

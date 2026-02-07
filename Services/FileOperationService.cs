using System.IO;
using System.Runtime.InteropServices;

namespace DiskWise.Services;

/// <summary>
/// Service for file operations (delete, move to recycle bin)
/// </summary>
public class FileOperationService
{
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperation(ref SHFILEOPSTRUCT lpFileOp);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEOPSTRUCT
    {
        public IntPtr hwnd;
        public uint wFunc;
        public string pFrom;
        public string pTo;
        public ushort fFlags;
        public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        public string lpszProgressTitle;
    }

    private const uint FO_DELETE = 0x0003;
    private const ushort FOF_ALLOWUNDO = 0x0040;        // Move to recycle bin
    private const ushort FOF_NOCONFIRMATION = 0x0010;  // No confirmation dialog
    private const ushort FOF_SILENT = 0x0004;          // No progress dialog

    /// <summary>
    /// Move file or folder to recycle bin
    /// </summary>
    public Task<(bool Success, string Error)> MoveToRecycleBinAsync(string path)
    {
        return Task.Run(() =>
        {
            try
            {
                var fileOp = new SHFILEOPSTRUCT
                {
                    wFunc = FO_DELETE,
                    pFrom = path + '\0' + '\0', // Double null terminated
                    fFlags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION
                };

                int result = SHFileOperation(ref fileOp);

                if (result != 0)
                {
                    return (false, $"Operation failed with code: {result}");
                }

                return (true, string.Empty);
            }
            catch (Exception ex)
            {
                return (false, ex.Message);
            }
        });
    }

    /// <summary>
    /// Permanently delete file or folder
    /// </summary>
    public Task<(bool Success, string Error)> DeletePermanentlyAsync(string path)
    {
        return Task.Run(() =>
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
                else if (Directory.Exists(path))
                {
                    Directory.Delete(path, recursive: true);
                }
                else
                {
                    return (false, "Path does not exist");
                }

                return (true, string.Empty);
            }
            catch (UnauthorizedAccessException)
            {
                return (false, "Access denied");
            }
            catch (IOException ex)
            {
                return (false, ex.Message);
            }
            catch (Exception ex)
            {
                return (false, ex.Message);
            }
        });
    }

    /// <summary>
    /// Batch delete multiple paths
    /// </summary>
    public async Task<BatchDeleteResult> BatchDeleteAsync(
        IEnumerable<string> paths,
        bool permanent,
        IProgress<DeleteProgress>? progress = null,
        CancellationToken ct = default)
    {
        var result = new BatchDeleteResult();
        var pathList = paths.ToList();
        int processed = 0;

        foreach (var path in pathList)
        {
            if (ct.IsCancellationRequested) break;

            var (success, error) = permanent
                ? await DeletePermanentlyAsync(path)
                : await MoveToRecycleBinAsync(path);

            if (success)
            {
                result.SuccessCount++;
            }
            else
            {
                result.FailedCount++;
                result.Errors.Add($"{path}: {error}");
            }

            processed++;
            progress?.Report(new DeleteProgress
            {
                Current = processed,
                Total = pathList.Count,
                CurrentPath = path
            });
        }

        return result;
    }

    /// <summary>
    /// Open path in Windows Explorer
    /// </summary>
    public void OpenInExplorer(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                // Select the file in Explorer
                System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{path}\"");
            }
            else if (Directory.Exists(path))
            {
                // Open the folder
                System.Diagnostics.Process.Start("explorer.exe", $"\"{path}\"");
            }
        }
        catch
        {
            // Ignore errors
        }
    }
}

public class BatchDeleteResult
{
    public int SuccessCount { get; set; }
    public int FailedCount { get; set; }
    public List<string> Errors { get; } = [];
}

public class DeleteProgress
{
    public int Current { get; set; }
    public int Total { get; set; }
    public string CurrentPath { get; set; } = string.Empty;
}

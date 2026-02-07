using System.IO;
using System.Text.Json;
using DiskWise.Models;

namespace DiskWise.Services;

/// <summary>
/// Manages application settings persistence
/// </summary>
public class SettingsService
{
    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "DiskWise",
        "settings.json");

    private AppSettings _settings = new();

    public AppSettings Settings => _settings;

    public async Task LoadAsync()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var json = await File.ReadAllTextAsync(SettingsPath);
                _settings = JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
            }
        }
        catch
        {
            _settings = new AppSettings();
        }
    }

    public async Task SaveAsync()
    {
        try
        {
            var directory = Path.GetDirectoryName(SettingsPath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var json = JsonSerializer.Serialize(_settings, new JsonSerializerOptions
            {
                WriteIndented = true
            });
            await File.WriteAllTextAsync(SettingsPath, json);
        }
        catch
        {
            // Ignore save errors
        }
    }

    public void AddRecentFolder(string path)
    {
        var existing = _settings.RecentFolders.FirstOrDefault(r => r.Path.Equals(path, StringComparison.OrdinalIgnoreCase));
        if (existing != null)
        {
            _settings.RecentFolders.Remove(existing);
        }

        _settings.RecentFolders.Insert(0, new RecentFolderEntry
        {
            Path = path,
            LastAccessed = DateTime.Now
        });

        // Keep only last 10
        while (_settings.RecentFolders.Count > 10)
        {
            _settings.RecentFolders.RemoveAt(_settings.RecentFolders.Count - 1);
        }
    }
}

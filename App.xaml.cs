using System.Windows;

namespace DiskWise;

public partial class App : Application
{
    private static readonly string[] SupportedLanguages = ["en-US", "zh-CN", "ja-JP"];

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
    }

    /// <summary>
    /// Switch UI language by loading the corresponding Strings resource dictionary
    /// </summary>
    public static void ApplyLanguage(string language)
    {
        if (!SupportedLanguages.Contains(language))
            language = "en-US";

        var uri = new Uri($"Resources/Strings.{language}.xaml", UriKind.Relative);
        var dict = new ResourceDictionary { Source = uri };

        // Remove any previously loaded Strings dictionary
        var merged = Current.Resources.MergedDictionaries;
        for (int i = merged.Count - 1; i >= 0; i--)
        {
            var source = merged[i].Source?.OriginalString ?? "";
            if (source.Contains("Strings."))
                merged.RemoveAt(i);
        }

        merged.Add(dict);
    }
}

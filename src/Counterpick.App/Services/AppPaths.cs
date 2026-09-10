using System.IO;

namespace Counterpick.App.Services;

/// <summary>
/// Everything the app writes lives under %APPDATA%\Counterpick, never beside the exe
/// and never in the repo. That keeps the API key out of git by construction and means
/// a rebuild or a reinstall does not touch your notes.
/// </summary>
public static class AppPaths
{
    public static string Root { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Counterpick");

    /// <summary>API key and preferences. Created on first run.</summary>
    public static string ConfigFile => Path.Combine(Root, "config.json");

    /// <summary>Champion pool, matchup notes, game results, cached briefs.</summary>
    public static string DatabaseFile => Path.Combine(Root, "counterpick.db");

    /// <summary>Data Dragon art and metadata, cached per patch. Safe to delete.</summary>
    public static string CacheDir => Path.Combine(Root, "cache");

    /// <summary>WebView2 keeps its profile here rather than next to the exe.</summary>
    public static string WebViewDir => Path.Combine(Root, "webview");

    public static void EnsureCreated()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(CacheDir);
        Directory.CreateDirectory(WebViewDir);
    }
}

using System.IO;

namespace Counterpick.App.Services;

/// <summary>
/// Everything the app writes lives under %APPDATA%\Counterpick, never beside the exe
/// and never in the repo. That keeps the API key out of git by construction and means
/// a rebuild or a reinstall does not touch your notes.
///
/// The two databases are deliberately separate. counterpick.db is yours and is
/// irreplaceable; cache.db is Claude's output and can be thrown away at any time. Only
/// the first is backed up, and only the second is safe to delete.
/// </summary>
public static class AppPaths
{
    public static string Root { get; } = ResolveRoot();

    /// <summary>
    /// Normally %APPDATA%\Counterpick, but COUNTERPICK_DATA_DIR overrides it.
    ///
    /// That override exists because <c>SpecialFolder.ApplicationData</c> resolves the
    /// Windows known folder and deliberately ignores the APPDATA environment variable -
    /// so tests cannot redirect it by setting APPDATA, and without an explicit hook they
    /// would write into the real profile. It also allows a portable install on a stick.
    ///
    /// Resolved once per process, so set it before the app touches any path.
    /// </summary>
    private static string ResolveRoot()
    {
        var overridden = Environment.GetEnvironmentVariable("COUNTERPICK_DATA_DIR");
        return string.IsNullOrWhiteSpace(overridden)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Counterpick")
            : Path.GetFullPath(overridden);
    }

    /// <summary>API key and preferences. Created on first run.</summary>
    public static string ConfigFile => Path.Combine(Root, "config.json");

    /// <summary>
    /// YOUR data: champion pool, matchup notes, game results. Irreplaceable, backed up.
    /// </summary>
    public static string DatabaseFile => Path.Combine(Root, "counterpick.db");

    /// <summary>
    /// Claude's output: generated briefs. Regenerable for pennies, never backed up.
    /// </summary>
    public static string CacheDatabaseFile => Path.Combine(Root, "cache.db");

    /// <summary>Timestamped snapshots of the notes database.</summary>
    public static string BackupsDir => Path.Combine(Root, "backups");

    /// <summary>Human-readable exports. Overwritten each time.</summary>
    public static string ExportsDir => Path.Combine(Root, "exports");

    /// <summary>Data Dragon art and metadata, cached per patch. Safe to delete.</summary>
    public static string CacheDir => Path.Combine(Root, "cache");

    /// <summary>WebView2 keeps its profile here rather than next to the exe.</summary>
    public static string WebViewDir => Path.Combine(Root, "webview");

    /// <summary>
    /// Off-machine mirror. Null when OneDrive is not set up - local snapshots protect
    /// against mistakes and corruption, but only this protects against losing the disk.
    /// </summary>
    public static string? OneDriveMirrorDir
    {
        get
        {
            var root = Environment.GetEnvironmentVariable("OneDrive")
                       ?? Environment.GetEnvironmentVariable("OneDriveConsumer")
                       ?? Environment.GetEnvironmentVariable("OneDriveCommercial");
            return string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)
                ? null
                : Path.Combine(root, "Counterpick");
        }
    }

    public static void EnsureCreated()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(CacheDir);
        Directory.CreateDirectory(WebViewDir);
        Directory.CreateDirectory(BackupsDir);
        Directory.CreateDirectory(ExportsDir);
    }
}

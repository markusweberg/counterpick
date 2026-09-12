using System.IO;

namespace Counterpick.App.Services;

/// <summary>
/// Almost everything the app writes lives under %APPDATA%\Counterpick, never beside the
/// exe and never in the repo. That keeps the API key out of git by construction and means
/// a rebuild or a reinstall does not touch your notes.
///
/// The one exception is counterpick.db, which lives in Documents\Counterpick. Windows
/// syncs Documents to OneDrive, so the live database - not just its snapshots - is
/// off-machine from the moment you write a note.
///
/// The two databases are deliberately separate. counterpick.db is yours and is
/// irreplaceable; cache.db is Claude's output and can be thrown away at any time. Only
/// the first is backed up, only the first is worth syncing, and only the second is safe
/// to delete.
/// </summary>
public static class AppPaths
{
    public static string Root { get; } = ResolveRoot();

    /// <summary>
    /// Where the notes database lives: Documents\Counterpick, so OneDrive carries it
    /// continuously rather than only at snapshot time. Everything else stays in
    /// <see cref="Root"/> - a WebView2 profile and a Data Dragon art cache have no
    /// business in anyone's Documents folder, let alone their cloud storage.
    ///
    /// COUNTERPICK_DATA_DIR overrides this too: a portable install on a stick keeps all
    /// of its files in the one folder, and tests stay inside their sandbox.
    /// </summary>
    public static string NotesRoot { get; } = ResolveNotesRoot();

    /// <summary>
    /// YOUR data: champion pool, matchup notes, game results. Irreplaceable, backed up.
    ///
    /// Resolved once, because resolving it is also what moves a pre-Documents database up
    /// to its new home - that has to happen before anything opens the file.
    /// </summary>
    public static string DatabaseFile { get; } = ResolveDatabaseFile();

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

    private static string ResolveNotesRoot()
    {
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("COUNTERPICK_DATA_DIR")))
            return Root;

        // Empty on a profile where the known folder cannot be resolved at all. Falling
        // back to Root loses the sync, not the notes.
        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        return string.IsNullOrWhiteSpace(documents)
            ? Root
            : Path.Combine(documents, "Counterpick");
    }

    private static string ResolveDatabaseFile()
    {
        var target = Path.Combine(NotesRoot, "counterpick.db");
        var legacy = Path.Combine(Root, "counterpick.db");

        return string.Equals(NotesRoot, Root, StringComparison.OrdinalIgnoreCase)
            ? target
            : Relocate(legacy, target);
    }

    /// <summary>
    /// Move a database written by an older version, which kept it in %APPDATA%, up to
    /// Documents. Runs at most once: afterwards the new file exists and this is a no-op.
    ///
    /// Returns the file the app should actually open. If the move cannot be completed the
    /// answer is the old one - an app that quietly keeps using %APPDATA% is a missing
    /// feature, whereas an app that opens an empty database next to your real one looks
    /// exactly like losing every note you have written.
    /// </summary>
    internal static string Relocate(string legacyFile, string targetFile)
    {
        if (File.Exists(targetFile) || !File.Exists(legacyFile)) return targetFile;

        var claimed = false;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(targetFile)!);

            // Claim the name first: CreateNew throws rather than truncating, so a target
            // that appeared underneath us is never something the catch below deletes.
            using (File.Open(targetFile, FileMode.CreateNew)) { }
            claimed = true;

            // Copy, verify, and only then delete. The -wal sidecar holds commits that are
            // not in the .db yet, so a database moved without it silently rolls back to
            // whenever SQLite last checkpointed. Nothing has opened either file at this
            // point, so the pair is consistent.
            File.Copy(legacyFile, targetFile, overwrite: true);
            if (File.Exists(legacyFile + "-wal")) File.Copy(legacyFile + "-wal", targetFile + "-wal", overwrite: true);

            TryDelete(legacyFile + "-shm");   // scratch; SQLite rebuilds it
            TryDelete(legacyFile + "-wal");
            TryDelete(legacyFile);
            return targetFile;
        }
        catch (Exception)
        {
            // Documents unreachable, OneDrive holding a lock, a full disk. Clear away the
            // half-written copy so the next launch does not mistake it for a database.
            if (claimed)
            {
                TryDelete(targetFile + "-wal");
                TryDelete(targetFile);
            }
            return legacyFile;
        }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (Exception) { /* leftovers are harmless */ }
    }

    /// <summary>API key and preferences. Created on first run.</summary>
    public static string ConfigFile => Path.Combine(Root, "config.json");

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
    ///
    /// Still worth having now that the database itself sits in a synced folder: this is
    /// what holds the dated snapshots, and a note deleted in a synced file is deleted
    /// everywhere within seconds.
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
        Directory.CreateDirectory(Path.GetDirectoryName(DatabaseFile)!);
        Directory.CreateDirectory(CacheDir);
        Directory.CreateDirectory(WebViewDir);
        Directory.CreateDirectory(BackupsDir);
        Directory.CreateDirectory(ExportsDir);
    }
}

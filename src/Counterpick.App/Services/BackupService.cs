using System.IO;

namespace Counterpick.App.Services;

public sealed record BackupInfo(string Path, DateTimeOffset TakenAt, long Bytes, string Reason);

public sealed record BackupStatus(
    int Notes,
    int Games,
    int Pool,
    DateTimeOffset? LastBackupAt,
    int SnapshotCount,
    string BackupsDir,
    string? MirrorDir,
    bool MirrorHealthy);

/// <summary>
/// Snapshots of the notes database.
///
/// Three layers, each covering a failure the others do not:
///   1. Timestamped local snapshots  - undo a mistake, a bad migration, or corruption.
///   2. A OneDrive mirror            - survive losing the disk.
///   3. JSON/Markdown export         - survive losing the app (see <see cref="DataTransfer"/>).
///
/// Snapshots are taken with VACUUM INTO rather than a file copy: the database runs in
/// WAL mode, so the newest commits can be sitting in a -wal sidecar that a naive copy
/// would miss.
/// </summary>
public sealed class BackupService(Storage storage)
{
    /// <summary>Snapshots kept before the oldest routine ones are pruned.</summary>
    private const int KeepSnapshots = 30;

    /// <summary>Snapshots taken for these reasons are never pruned.</summary>
    private static readonly string[] Permanent = ["migration", "restore"];

    private static string FingerprintFile => Path.Combine(AppPaths.BackupsDir, ".fingerprint");

    /// <summary>
    /// Take a snapshot only if the data actually changed since the last one. Called at
    /// startup and shutdown, so an evening of League produces one snapshot rather than
    /// thirty identical ones.
    /// </summary>
    public BackupInfo? SnapshotIfChanged(string reason = "auto")
    {
        var fingerprint = storage.Fingerprint();
        var previous = ReadFingerprint();
        if (previous == fingerprint && LatestSnapshot() is not null) return null;

        var info = Snapshot(reason);
        File.WriteAllText(FingerprintFile, fingerprint);
        return info;
    }

    /// <summary>Take a snapshot unconditionally, mirror it, and prune old ones.</summary>
    public BackupInfo Snapshot(string reason = "manual")
    {
        AppPaths.EnsureCreated();

        // Two snapshots can easily land in the same second - migration and startup do it
        // on every upgrade. Returning the existing file on a collision would hand back a
        // stale snapshot while the caller recorded it as saved, so disambiguate instead.
        var stamp = DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss");
        var basePath = Path.Combine(AppPaths.BackupsDir, $"counterpick-{stamp}-{Sanitize(reason)}");
        var path = basePath + ".db";
        for (var n = 2; File.Exists(path); n++) path = $"{basePath}-{n}.db";

        storage.SnapshotTo(path);
        Prune();
        TryMirror(path);
        return Describe(path);
    }

    public IReadOnlyList<BackupInfo> List()
    {
        if (!Directory.Exists(AppPaths.BackupsDir)) return [];
        return Directory.GetFiles(AppPaths.BackupsDir, "counterpick-*.db")
                        .Select(Describe)
                        .OrderByDescending(b => b.TakenAt)
                        .ToList();
    }

    public BackupInfo? LatestSnapshot() => List().FirstOrDefault();

    public BackupStatus Status()
    {
        var counts = storage.Counts();
        var snapshots = List();
        var mirror = AppPaths.OneDriveMirrorDir;

        return new BackupStatus(
            counts.Notes, counts.Games, counts.Pool,
            snapshots.Count > 0 ? snapshots[0].TakenAt : null,
            snapshots.Count,
            AppPaths.BackupsDir,
            mirror,
            mirror is not null && Directory.Exists(mirror) &&
                File.Exists(Path.Combine(mirror, "counterpick-latest.db")));
    }

    /// <summary>
    /// Restore a snapshot by merging it into the live database. Merging rather than
    /// replacing means a restore can never destroy notes written since the snapshot -
    /// the unique indexes make importing the same rows twice a no-op.
    /// </summary>
    public DataCounts Restore(string snapshotPath)
    {
        if (!File.Exists(snapshotPath))
            throw new FileNotFoundException("Snapshot not found.", snapshotPath);

        // Insure against the restore itself going wrong.
        Snapshot("restore");
        return storage.ImportFrom(snapshotPath);
    }

    /// <summary>
    /// On a fresh install with an empty database, pull the OneDrive mirror back in
    /// automatically. This is the whole point of the mirror, and with nothing local to
    /// lose there is no risk in doing it without asking.
    /// </summary>
    public DataCounts? RestoreFromMirrorIfEmpty()
    {
        if (!storage.IsEmpty()) return null;

        var mirror = AppPaths.OneDriveMirrorDir;
        if (mirror is null) return null;

        var latest = Path.Combine(mirror, "counterpick-latest.db");
        if (!File.Exists(latest)) return null;

        var restored = storage.ImportFrom(latest);
        return restored.Notes + restored.Games + restored.Pool > 0 ? restored : null;
    }

    // ── internals ────────────────────────────────────────────────────────

    /// <summary>
    /// Mirror off-machine. Keeps a stable "latest" file so restore has one obvious
    /// target, plus a dated copy. OneDrive layers its own version history on top.
    /// </summary>
    private void TryMirror(string snapshotPath)
    {
        var mirror = AppPaths.OneDriveMirrorDir;
        if (mirror is null) return;

        try
        {
            Directory.CreateDirectory(mirror);
            File.Copy(snapshotPath, Path.Combine(mirror, "counterpick-latest.db"), overwrite: true);
            File.Copy(snapshotPath, Path.Combine(mirror, Path.GetFileName(snapshotPath)), overwrite: true);

            // A readable copy alongside it, so the notes are legible without the app.
            DataTransfer.WriteMarkdown(storage, Path.Combine(mirror, "notes.md"));
            DataTransfer.WriteJson(storage, Path.Combine(mirror, "notes.json"));

            PruneDirectory(mirror);
        }
        catch (Exception)
        {
            // OneDrive being offline, paused, or full must never stop the app or lose the
            // local snapshot that already succeeded. The next snapshot retries.
        }
    }

    private void Prune() => PruneDirectory(AppPaths.BackupsDir);

    private static void PruneDirectory(string dir)
    {
        if (!Directory.Exists(dir)) return;

        var routine = Directory.GetFiles(dir, "counterpick-*.db")
                               .Select(Describe)
                               .Where(b => !Permanent.Contains(b.Reason))
                               .OrderByDescending(b => b.TakenAt)
                               .Skip(KeepSnapshots);

        foreach (var old in routine)
        {
            try { File.Delete(old.Path); }
            catch (IOException) { /* locked by a sync client; it goes next time */ }
        }
    }

    private static BackupInfo Describe(string path)
    {
        var info = new FileInfo(path);
        // counterpick-20260910-224500-auto.db
        var parts = Path.GetFileNameWithoutExtension(path).Split('-');
        var reason = parts.Length >= 4 ? parts[3] : "unknown";
        return new BackupInfo(path, new DateTimeOffset(info.LastWriteTime), info.Length, reason);
    }

    private static string ReadFingerprint()
    {
        try { return File.Exists(FingerprintFile) ? File.ReadAllText(FingerprintFile).Trim() : ""; }
        catch (IOException) { return ""; }
    }

    private static string Sanitize(string reason) =>
        new(reason.Where(char.IsLetterOrDigit).DefaultIfEmpty('x').ToArray());
}

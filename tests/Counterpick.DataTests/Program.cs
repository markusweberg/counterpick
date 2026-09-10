using Counterpick.App.Services;

// AppPaths resolves the Windows known folder, which ignores the APPDATA environment
// variable - so redirection has to go through the explicit override, set before the
// app touches any path.
var sandbox = Path.Combine(Path.GetTempPath(), "cp-test-" + Guid.NewGuid().ToString("N")[..8]);
var fakeOneDrive = Path.Combine(sandbox, "OneDrive");
Directory.CreateDirectory(fakeOneDrive);
Environment.SetEnvironmentVariable("COUNTERPICK_DATA_DIR", Path.Combine(sandbox, "Counterpick"));
Environment.SetEnvironmentVariable("OneDrive", fakeOneDrive);

int passed = 0, failed = 0;
void Check(string label, bool ok, string detail = "")
{
    Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}  {label}{(detail.Length > 0 ? "   " + detail : "")}");
    if (ok) passed++; else failed++;
}

Console.WriteLine($"sandbox: {sandbox}");
Console.WriteLine($"resolved root: {AppPaths.Root}");
if (!AppPaths.Root.StartsWith(sandbox, StringComparison.OrdinalIgnoreCase))
{
    Console.WriteLine("ABORT: isolation failed - refusing to write to the real profile.");
    return 2;
}

// ── 1. fresh install ─────────────────────────────────────────────────────
var storage = new Storage();
var backups = new BackupService(storage);
storage.EnsureSchema();

Console.WriteLine("\n1. fresh install");
Check("database starts empty", storage.IsEmpty());
Check("no snapshots yet", backups.List().Count == 0);

// ── 2. write real data ───────────────────────────────────────────────────
Console.WriteLine("\n2. writing notes, games and a pool");
storage.SetPool("Top", ["Gwen", "Jax", "Ornn", "Camille", "Malphite", "Aatrox"]);
storage.AddNote("Gwen", "Darius", "Top", "Count his stacks out loud. At 4, walk away.");
storage.AddNote("Gwen", "Darius", "Top", "W does nothing if he stands inside it with me.");
storage.AddNote("Jax", "Darius", "Top", "Let him throw Q first, then E.");
storage.RecordGame("Gwen", "Darius", "Top", won: true, playedAt: DateTimeOffset.Now.AddDays(-2));
storage.RecordGame("Gwen", "Darius", "Top", won: false, playedAt: DateTimeOffset.Now.AddDays(-1));

Check("3 notes, 2 games, 6 pool",
      storage.Counts() is { Notes: 3, Games: 2, Pool: 6 }, $"got {storage.Counts()}");
Check("record reads 1-1", storage.GetRecord("Gwen", "Darius", "Top") is { Wins: 1, Losses: 1 });

// ── 3. note identity ─────────────────────────────────────────────────────
// Identity is (champion, opponent, role, created_at, body): re-importing an export must
// not duplicate, but relearning the same lesson weeks later is a genuinely new note.
Console.WriteLine("\n3. note identity");
var when = DateTimeOffset.Now.AddDays(-3);
var n1 = storage.AddNote("Ornn", "Darius", "Top", "R is not a lane spell here.", when);
var afterFirst = storage.Counts().Notes;
var n2 = storage.AddNote("Ornn", "Darius", "Top", "R is not a lane spell here.", when);
Check("re-adding the very same note is rejected",
      n1 != 0 && n2 == 0 && storage.Counts().Notes == afterFirst);
var n3 = storage.AddNote("Ornn", "Darius", "Top", "R is not a lane spell here.", when.AddDays(1));
Check("the same lesson on a later day is kept",
      n3 != 0 && storage.Counts().Notes == afterFirst + 1);

// ── 4. snapshots ─────────────────────────────────────────────────────────
Console.WriteLine("\n4. snapshots");
var first = backups.SnapshotIfChanged("test");
Check("snapshot taken after changes", first is not null);
Check("snapshot file is non-empty", first is not null && new FileInfo(first.Path).Length > 0);
Check("no second snapshot when nothing changed", backups.SnapshotIfChanged("test") is null);

storage.AddNote("Camille", "Darius", "Top", "Hookshot out over the wall, not down the lane.");
var snap = backups.SnapshotIfChanged("test");
Check("snapshot taken again after a new note", snap is not null);

var atSnapshot = storage.Counts();
// The critical one: WAL means the newest commits can sit in a -wal sidecar that a plain
// file copy would miss. VACUUM INTO has to fold them in.
Check("snapshot contains the note written just before it",
      new Storage(snap!.Path).Counts().Notes == atSnapshot.Notes,
      $"snapshot {new Storage(snap.Path).Counts().Notes} vs live {atSnapshot.Notes}");

// ── 5. off-machine mirror ────────────────────────────────────────────────
Console.WriteLine("\n5. off-machine mirror");
var mirror = AppPaths.OneDriveMirrorDir!;
Check("mirror directory created", Directory.Exists(mirror));
Check("counterpick-latest.db mirrored", File.Exists(Path.Combine(mirror, "counterpick-latest.db")));
Check("notes.json mirrored", File.Exists(Path.Combine(mirror, "notes.json")));
Check("notes.md mirrored", File.Exists(Path.Combine(mirror, "notes.md")));
Check("mirrored json has no BOM",
      File.ReadAllBytes(Path.Combine(mirror, "notes.json")) is [not 0xEF, ..]);

// ── 6. export ────────────────────────────────────────────────────────────
Console.WriteLine("\n6. export");
var (jsonPath, mdPath) = DataTransfer.ExportAll(storage);
var md = File.ReadAllText(mdPath);
Check("json written", File.Exists(jsonPath));
Check("markdown groups by matchup", md.Contains("### Gwen into Darius"));
Check("markdown shows the record", md.Contains("1–1"), "expected 1-1 in the heading");
Check("markdown carries a note body", md.Contains("Count his stacks out loud"));

// ── 7. total loss, rebuilt from JSON ─────────────────────────────────────
Console.WriteLine("\n7. disk lost: rebuild from the JSON export");
var rebuilt = new Storage(Path.Combine(sandbox, "rebuilt.db"));
rebuilt.EnsureSchema();
var expected = storage.Counts();
var imported = DataTransfer.ImportJson(rebuilt, jsonPath);
Check("every note restored", rebuilt.Counts().Notes == expected.Notes,
      $"got {rebuilt.Counts().Notes} of {expected.Notes}");
Check("every game restored", rebuilt.Counts().Games == expected.Games);
Check("pool restored", rebuilt.Counts().Pool == expected.Pool);
Check("import reports what it added", imported == expected, $"got {imported}");
Check("record survived the round trip",
      rebuilt.GetRecord("Gwen", "Darius", "Top") is { Wins: 1, Losses: 1 },
      $"got {rebuilt.GetRecord("Gwen", "Darius", "Top")}");

// ── 8. import is idempotent ──────────────────────────────────────────────
Console.WriteLine("\n8. import is idempotent");
var again = DataTransfer.ImportJson(rebuilt, jsonPath);
Check("second import adds nothing", again is { Notes: 0, Games: 0, Pool: 0 }, $"got {again}");
Check("note count unchanged", rebuilt.Counts().Notes == expected.Notes);

// ── 9. restore merges rather than overwrites ─────────────────────────────
Console.WriteLine("\n9. restore merges rather than overwrites");
storage.AddNote("Aatrox", "Darius", "Top", "Written AFTER the snapshot - must survive a restore.");
var withNewer = storage.Counts().Notes;
var restored = backups.Restore(snap.Path);
Check("restoring an older snapshot loses nothing", storage.Counts().Notes == withNewer,
      $"got {storage.Counts().Notes}, expected {withNewer}");
Check("the newer note is still there",
      storage.AllNotes().Any(n => n.Body.Contains("must survive")));
Check("restore introduced no duplicates", restored is { Notes: 0 }, $"got {restored}");
Check("a pre-restore snapshot was taken", backups.List().Any(b => b.Reason == "restore"));

// ── 10. new machine pulls the mirror back ────────────────────────────────
Console.WriteLine("\n10. new machine: empty database pulls the mirror back");
var fresh = new Storage(Path.Combine(sandbox, "newmachine.db"));
fresh.EnsureSchema();
Check("new database starts empty", fresh.IsEmpty());
var pulled = new BackupService(fresh).RestoreFromMirrorIfEmpty();
Check("mirror restored automatically", pulled is not null && pulled.Notes > 0, $"got {pulled}");
// Section 9's restore took its own snapshot, which re-mirrored; compare against live.
var live = storage.Counts().Notes;
Check("notes are back", fresh.Counts().Notes == live,
      $"got {fresh.Counts().Notes}, live holds {live}");

// ── 11. a populated database is never auto-overwritten ───────────────────
Console.WriteLine("\n11. auto-restore only touches an empty database");
var beforeGuard = storage.Counts().Notes;
Check("auto-restore declines when data exists",
      new BackupService(storage).RestoreFromMirrorIfEmpty() is null
      && storage.Counts().Notes == beforeGuard);

// ── 12. retention labels ─────────────────────────────────────────────────
Console.WriteLine("\n12. retention");
Check("permanent snapshots labelled", backups.List().Any(b => b.Reason == "restore"));
Check("routine snapshots labelled", backups.List().Any(b => b.Reason == "test"));

// ── 13. cache is separate and disposable ─────────────────────────────────
Console.WriteLine("\n13. brief cache is separate and disposable");
var cache = new BriefCache();
cache.EnsureSchema();
var fp = BriefCache.FingerprintFor(storage, "Gwen", "Darius", "Top");
cache.Put("Gwen", "Darius", "Top", fp, "a generated brief");
Check("brief cached and read back", cache.Get("Gwen", "Darius", "Top", fp) == "a generated brief");
Check("cache lives in its own file", Path.GetFileName(cache.DatabaseFile) == "cache.db");

var beforeWipe = storage.Counts().Notes;
storage.AddNote("Gwen", "Darius", "Top", "Something new I learned.");
var fp2 = BriefCache.FingerprintFor(storage, "Gwen", "Darius", "Top");
Check("a new note invalidates the cached brief",
      fp2 != fp && cache.Get("Gwen", "Darius", "Top", fp2) is null);

cache.Clear();
Check("cache can be wiped", cache.Count() == 0);
Check("wiping the cache left notes untouched", storage.Counts().Notes == beforeWipe + 1,
      $"got {storage.Counts().Notes}, expected {beforeWipe + 1}");

Console.WriteLine($"\n{passed} passed, {failed} failed");
try { Directory.Delete(sandbox, recursive: true); } catch { /* sqlite may still hold handles */ }
return failed == 0 ? 0 : 1;

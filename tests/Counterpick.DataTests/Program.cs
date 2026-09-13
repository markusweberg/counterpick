using Counterpick.App.Services;
using Counterpick.App.Services.Lcu;

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
Console.WriteLine($"resolved database: {AppPaths.DatabaseFile}");
if (!AppPaths.Root.StartsWith(sandbox, StringComparison.OrdinalIgnoreCase) ||
    !AppPaths.DatabaseFile.StartsWith(sandbox, StringComparison.OrdinalIgnoreCase))
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

// ── 14. editing and deleting notes ───────────────────────────────────────
Console.WriteLine("\n14. editing and deleting notes");
var target = storage.AddNote("Jax", "Garen", "Top", "Original text.");
Check("note created for editing", target != 0);

var fpBeforeEdit = storage.Fingerprint();
Check("edit succeeds", storage.UpdateNote(target, "Rewritten text."));
Check("body actually changed",
      storage.GetNotes("Jax", "Garen", "Top").Any(n => n.Body == "Rewritten text."));
Check("original text is gone",
      !storage.GetNotes("Jax", "Garen", "Top").Any(n => n.Body == "Original text."));

// The reason updated_at exists: an edit changes neither the row count nor the highest
// id, so without it a rewritten note would never trigger a backup.
Check("an edit changes the backup fingerprint", storage.Fingerprint() != fpBeforeEdit,
      "otherwise edits would go unsnapshotted");
Check("an edit triggers a snapshot", backups.SnapshotIfChanged("test") is not null);

var edited = storage.GetNotes("Jax", "Garen", "Top").First(n => n.Body == "Rewritten text.");
Check("created date is preserved by an edit", edited.CreatedAt < edited.UpdatedAt);
Check("editing a missing note reports failure", !storage.UpdateNote(999999, "nope"));

// An edit that would collide with another note in the same matchup must be refused
// rather than silently dropped or throwing.
var other = storage.AddNote("Jax", "Garen", "Top", "A second note.", edited.CreatedAt);
Check("second note added for the collision test", other != 0);
Check("an edit that would duplicate is refused",
      !storage.UpdateNote(other, "Rewritten text."),
      "same matchup, same created_at, same body");
Check("the refused edit left the note alone",
      storage.GetNotes("Jax", "Garen", "Top").Any(n => n.Body == "A second note."));

var fpBeforeDelete = storage.Fingerprint();
var countBeforeDelete = storage.Counts().Notes;
Check("delete succeeds", storage.DeleteNote(target));
Check("note is gone", storage.Counts().Notes == countBeforeDelete - 1);
Check("a delete changes the backup fingerprint", storage.Fingerprint() != fpBeforeDelete);
Check("deleting a missing note reports failure", !storage.DeleteNote(999999));

// A deleted note stays recoverable, which is what makes offering deletion safe.
var recovered = backups.Restore(backups.List().First(b => b.Reason == "test").Path);
Check("a deleted note can be brought back from a snapshot",
      storage.GetNotes("Jax", "Garen", "Top").Any(n => n.Body == "Rewritten text."),
      $"restore added {recovered.Notes} note(s)");

// ── 15. the move from %APPDATA% to Documents ──────────────────────
Console.WriteLine("\n15. relocating the notes database to Documents");
{
    // COUNTERPICK_DATA_DIR keeps this run in one folder, so drive the move directly
    // rather than through the process-wide resolution.
    var appdata = Path.Combine(sandbox, "old-appdata");
    var documents = Path.Combine(sandbox, "old-documents", "Counterpick");
    Directory.CreateDirectory(appdata);
    var from = Path.Combine(appdata, "counterpick.db");
    var to = Path.Combine(documents, "counterpick.db");

    var moved = new Storage(from);
    moved.EnsureSchema();
    moved.AddNote("Gwen", "Darius", "Top", "Written before the move.");
    File.WriteAllText(from + "-wal", "stands in for uncheckpointed commits");

    Check("the database moves to Documents",
          AppPaths.Relocate(from, to) == to && File.Exists(to) && !File.Exists(from));
    Check("the -wal sidecar moves with it, rather than rolling the newest notes back",
          File.Exists(to + "-wal") && !File.Exists(from + "-wal"));

    File.Delete(to + "-wal");   // it was never a real sidecar
    Check("the notes came along", new Storage(to).Counts().Notes == 1);

    Check("a later launch has nothing left to move", AppPaths.Relocate(from, to) == to);

    // The dangerous case: a stale copy left in %APPDATA% must never overwrite the one
    // you have been writing to since.
    File.WriteAllText(from, "an older database");
    Check("an existing Documents database is never overwritten",
          AppPaths.Relocate(from, to) == to && new Storage(to).Counts().Notes == 1);

    // Nothing to move at all: a fresh install just uses the new location.
    var newInstall = Path.Combine(sandbox, "fresh-documents", "counterpick.db");
    Check("a fresh install resolves straight to Documents",
          AppPaths.Relocate(Path.Combine(sandbox, "nothing-here.db"), newInstall) == newInstall);
}

// ── 13. importing a database from before a migration ─────────────────────
// A OneDrive mirror or an old snapshot can predate the updated_at column. Found the
// hard way on 2026-09-11: the automatic mirror restore crashed the app at startup.
Console.WriteLine("\n13. import from an older schema");
var oldFile = Path.Combine(sandbox, "schema-v2.db");
using (var old = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={oldFile}"))
{
    old.Open();
    using var mk = old.CreateCommand();
    mk.CommandText = """
        CREATE TABLE pool (champion_key TEXT NOT NULL, role TEXT NOT NULL, added_at TEXT NOT NULL,
                           PRIMARY KEY (champion_key, role));
        CREATE TABLE notes (id INTEGER PRIMARY KEY AUTOINCREMENT, champion_key TEXT NOT NULL,
                            opponent_key TEXT NOT NULL, role TEXT NOT NULL, body TEXT NOT NULL,
                            created_at TEXT NOT NULL);
        CREATE TABLE games (id INTEGER PRIMARY KEY AUTOINCREMENT, champion_key TEXT NOT NULL,
                            opponent_key TEXT NOT NULL, role TEXT NOT NULL, won INTEGER NOT NULL,
                            played_at TEXT NOT NULL);
        INSERT INTO notes (champion_key, opponent_key, role, body, created_at)
            VALUES ('Gwen', 'Sett', 'Top', 'From the old schema.', '2026-08-01T10:00:00+00:00');
        INSERT INTO pool VALUES ('Gwen', 'Top', '2026-08-01T10:00:00+00:00');
        PRAGMA user_version = 2;
        """;
    mk.ExecuteNonQuery();
}
var intoV3 = new Storage(Path.Combine(sandbox, "v3-target.db"));
intoV3.EnsureSchema();
DataCounts? fromOld = null;
try { fromOld = intoV3.ImportFrom(oldFile); } catch (Exception ex) { Check("import from schema v2 throws", false, ex.Message); }
Check("import from schema v2 succeeds", fromOld is { Notes: 1, Pool: 1 }, $"got {fromOld}");
var oldNote = intoV3.GetNotes("Gwen", "Sett", "Top").FirstOrDefault();
Check("updated_at is filled from created_at", oldNote is not null && oldNote.UpdatedAt == oldNote.CreatedAt);

// ── League client: endpoint discovery ────────────────────────────────────
// The port and password come off LeagueClientUx.exe's command line, with the lockfile
// as the documented fallback. Neither is available without the client running, so the
// parsers are tested on captured shapes.
Console.WriteLine("\nLCU. endpoint discovery");
// Captured from a live client (2026-09-11): every argument is quoted as a whole, so the
// quote sits before the dashes and the install path runs unquoted to the closing quote.
var cmd = "\"C:/Riot Games/League of Legends/LeagueClientUx.exe\" \"--riotclient-auth-token=abc\" " +
          "\"--riotclient-app-port=59482\" \"--region=EUW\" \"--remoting-auth-token=AbCdEf-123_456\" " +
          "\"--app-port=52001\" \"--install-directory=C:\\Riot Games\\League of Legends\" " +
          "\"--app-name=LeagueClient\" \"--output-base-dir=C:\\Riot Games\\League of Legends\"";
var ep = LcuEndpoint.ParseCommandLine(cmd);
Check("command line yields port and token",
      ep is { Port: 52001, Password: "AbCdEf-123_456" }, $"got {ep}");
Check("install directory parsed with its spaces",
      LcuEndpoint.InstallDirectoryFrom(cmd) == "C:\\Riot Games\\League of Legends",
      $"got {LcuEndpoint.InstallDirectoryFrom(cmd)}");
Check("install directory parsed when the value itself is quoted",
      LcuEndpoint.InstallDirectoryFrom("--install-directory=\"D:\\Games\\LoL\" --x=1") == "D:\\Games\\LoL");
Check("install directory parsed when nothing is quoted",
      LcuEndpoint.InstallDirectoryFrom("--install-directory=D:\\Games\\LoL --x=1") == "D:\\Games\\LoL");
Check("a command line without the token yields nothing",
      LcuEndpoint.ParseCommandLine("LeagueClientUx.exe --app-port=52001") is null);
Check("lockfile parses", LcuEndpoint.ParseLockfile("LeagueClient:12345:52001:AbCdEf123456:https\n")
      is { Port: 52001, Password: "AbCdEf123456" });
Check("garbage lockfile yields nothing", LcuEndpoint.ParseLockfile("nope") is null);
Check("missing lockfile yields nothing", LcuEndpoint.ReadLockfile(sandbox) is null);

// ── League client: champion catalog ──────────────────────────────────────
Console.WriteLine("\nLCU. champion catalog");
const string championJson = """
    {"type":"champion","version":"16.18.1","data":{
      "Aatrox":{"id":"Aatrox","key":"266","name":"Aatrox","tags":["Fighter","Tank"]},
      "Darius":{"id":"Darius","key":"122","name":"Darius","tags":["Fighter","Tank"]},
      "Gwen":{"id":"Gwen","key":"887","name":"Gwen","tags":["Fighter","Assassin"]},
      "Jax":{"id":"Jax","key":"24","name":"Jax","tags":["Fighter"]},
      "MonkeyKing":{"id":"MonkeyKing","key":"62","name":"Wukong","tags":["Fighter","Tank"]},
      "Nidalee":{"id":"Nidalee","key":"76","name":"Nidalee","tags":["Assassin","Mage"],"stats":{"attackrange":550}},
      "Orianna":{"id":"Orianna","key":"61","name":"Orianna","tags":["Mage","Support"],"stats":{"attackrange":550}},
      "Sejuani":{"id":"Sejuani","key":"113","name":"Sejuani","tags":["Tank","Fighter"]},
      "Amumu":{"id":"Amumu","key":"32","name":"Amumu","tags":["Tank","Mage"]},
      "Diana":{"id":"Diana","key":"131","name":"Diana","tags":["Fighter","Mage"]},
      "Sett":{"id":"Sett","key":"875","name":"Sett","tags":["Fighter","Tank"]},
      "Sona":{"id":"Sona","key":"37","name":"Sona","tags":["Support","Mage"],"stats":{"attackrange":550}},
      "Varus":{"id":"Varus","key":"110","name":"Varus","tags":["Marksman","Mage"],"stats":{"attackrange":550}},
      "Brand":{"id":"Brand","key":"63","name":"Brand","tags":["Mage"],"stats":{"attackrange":550}},
      "Garen":{"id":"Garen","key":"86","name":"Garen","tags":["Fighter","Tank"]},
      "Hwei":{"id":"Hwei","key":"910","name":"Hwei","tags":["Mage"],"stats":{"attackrange":550}},
      "Lulu":{"id":"Lulu","key":"147","name":"Lulu","tags":["Support","Mage"],"stats":{"attackrange":550}},
      "Nunu":{"id":"Nunu","key":"20","name":"Nunu & Willump","tags":["Tank","Fighter"]},
      "Thresh":{"id":"Thresh","key":"412","name":"Thresh","tags":["Support","Fighter"],"stats":{"attackrange":550}},
      "Veigar":{"id":"Veigar","key":"45","name":"Veigar","tags":["Mage"],"stats":{"attackrange":550}},
      "Warwick":{"id":"Warwick","key":"19","name":"Warwick","tags":["Fighter","Tank"]},
      "Yone":{"id":"Yone","key":"777","name":"Yone","tags":["Assassin","Fighter"]},
      "Akali":{"id":"Akali","key":"84","name":"Akali","tags":["Assassin"]},
      "Morgana":{"id":"Morgana","key":"25","name":"Morgana","tags":["Mage","Support"],"stats":{"attackrange":550}},
      "Caitlyn":{"id":"Caitlyn","key":"51","name":"Caitlyn","tags":["Marksman"],"stats":{"attackrange":550}},
      "Khazix":{"id":"Khazix","key":"121","name":"Kha'Zix","tags":["Assassin"]},
      "Tryndamere":{"id":"Tryndamere","key":"23","name":"Tryndamere","tags":["Fighter","Assassin"]},
      "Jhin":{"id":"Jhin","key":"202","name":"Jhin","tags":["Marksman","Mage"],"stats":{"attackrange":550}},
      "Singed":{"id":"Singed","key":"27","name":"Singed","tags":["Tank","Fighter"]},
      "Fizz":{"id":"Fizz","key":"105","name":"Fizz","tags":["Assassin","Fighter"]},
      "Teemo":{"id":"Teemo","key":"17","name":"Teemo","tags":["Marksman","Assassin"],"stats":{"attackrange":550}},
      "Pyke":{"id":"Pyke","key":"555","name":"Pyke","tags":["Support","Assassin"]}
    }}
    """;
var catalog = ChampionCatalog.FromJson("16.18.1", championJson);
Check("catalog loads every champion", catalog.Champions.Count == 32, $"got {catalog.Champions.Count}");
Check("numeric id maps to the Data Dragon key", catalog.ById(62)?.Key == "MonkeyKing");
Check("display name survives", catalog.ById(62)?.Name == "Wukong");
Check("id 0 means nothing picked", catalog.ById(0) is null);
Check("unknown id is null, not a crash", catalog.ById(999999) is null);
Check("key lookup is case-insensitive", catalog.ByKey("monkeyking")?.NumericId == 62);
Check("attack range marks ranged champions; no stats means melee",
      catalog.ByKey("Veigar")!.Ranged && catalog.ByKey("Thresh")!.Ranged && !catalog.ByKey("Diana")!.Ranged && !catalog.ByKey("Nunu")!.Ranged);

// ── League client: session mapping ───────────────────────────────────────
// The worked example's draft, as the client would describe it: blue side, you top in
// cell 0, pick B3 in progress, Darius and Nidalee locked for red, their mid hovering
// Orianna, red's third pick still to come.
Console.WriteLine("\nLCU. session mapping");
const string sessionJson = """
    {
      "localPlayerCellId": 0,
      "benchEnabled": false,
      "timer": { "adjustedTimeLeftInPhase": 27000, "phase": "BAN_PICK" },
      "myTeam": [
        { "cellId": 0, "championId": 0, "championPickIntent": 887, "assignedPosition": "top" },
        { "cellId": 1, "championId": 113, "championPickIntent": 0, "assignedPosition": "jungle" },
        { "cellId": 2, "championId": 61, "championPickIntent": 0, "assignedPosition": "middle" },
        { "cellId": 3, "championId": 0, "championPickIntent": 0, "assignedPosition": "bottom" },
        { "cellId": 4, "championId": 0, "championPickIntent": 0, "assignedPosition": "utility" }
      ],
      "theirTeam": [
        { "cellId": 5, "championId": 122, "championPickIntent": 0, "assignedPosition": "top" },
        { "cellId": 6, "championId": 76, "championPickIntent": 0, "assignedPosition": "jungle" },
        { "cellId": 7, "championId": 0, "championPickIntent": 61, "assignedPosition": "middle" },
        { "cellId": 8, "championId": 0, "championPickIntent": 0, "assignedPosition": "bottom" },
        { "cellId": 9, "championId": 0, "championPickIntent": 0, "assignedPosition": "utility" }
      ],
      "actions": [
        [ { "actorCellId": 0, "championId": 24, "completed": true, "isInProgress": false, "type": "ban" },
          { "actorCellId": 5, "championId": 266, "completed": true, "isInProgress": false, "type": "ban" } ],
        [ { "actorCellId": 5, "championId": 122, "completed": true, "isInProgress": false, "type": "pick" } ],
        [ { "actorCellId": 1, "championId": 113, "completed": true, "isInProgress": false, "type": "pick" },
          { "actorCellId": 2, "championId": 61, "completed": true, "isInProgress": false, "type": "pick" } ],
        [ { "actorCellId": 6, "championId": 76, "completed": true, "isInProgress": false, "type": "pick" },
          { "actorCellId": 7, "championId": 0, "completed": false, "isInProgress": true, "type": "pick" } ],
        [ { "actorCellId": 0, "championId": 0, "completed": false, "isInProgress": true, "type": "pick" },
          { "actorCellId": 3, "championId": 0, "completed": false, "isInProgress": false, "type": "pick" } ],
        [ { "actorCellId": 8, "championId": 0, "completed": false, "isInProgress": false, "type": "pick" },
          { "actorCellId": 9, "championId": 0, "completed": false, "isInProgress": false, "type": "pick" } ],
        [ { "actorCellId": 4, "championId": 0, "completed": false, "isInProgress": false, "type": "pick" } ]
      ]
    }
    """;
var session = System.Text.Json.Nodes.JsonNode.Parse(sessionJson)!;
var draft = DraftMapper.Map(session, catalog);
Check("session maps", draft is not null);
if (draft is not null)
{
    Check("you are cell 0, top", draft.Ally[0] is { IsYou: true, Role: "Top" });
    Check("your hover shows but is not a lock",
          draft is { LockedKey: null, HoverKey: "Gwen" } && draft.Ally[0].Hovering);
    Check("allied locks map by numeric id",
          draft.Ally[1].ChampionKey == "Sejuani" && draft.Ally[2].ChampionKey == "Orianna");
    Check("enemy locks land in enemyRoles",
          draft.EnemyRoles.Count == 2 && draft.EnemyRoles["Darius"] == "Top" && draft.EnemyRoles["Nidalee"] == "Jungle");
    Check("an enemy hover is on the board but not in enemyRoles",
          draft.Enemy[2] is { ChampionKey: "Orianna", Hovering: true } && !draft.EnemyRoles.ContainsKey("Orianna"));
    Check("the enemy seat on the clock is marked", draft.Enemy[2].OnTheClock && !draft.Enemy[3].OnTheClock);
    Check("it is your turn", draft.YourTurn);
    Check("timer comes through in ms", draft is { TimerMs: 27000, TimerPhase: "BAN_PICK" });
    Check("bans are mapped", draft.Bans.SequenceEqual(["Jax", "Aatrox"]));
    Check("three enemy picks remain", draft.EnemyPicksRemaining == 3);
}

// The same session after you lock: championId set, action completed.
var locked = System.Text.Json.Nodes.JsonNode.Parse(sessionJson)!;
locked["myTeam"]![0]!["championId"] = 887;
locked["actions"]![4]![0]!["championId"] = 887;
locked["actions"]![4]![0]!["completed"] = true;
locked["actions"]![4]![0]!["isInProgress"] = false;
var afterLock = DraftMapper.Map(locked, catalog);
Check("a lock reports lockedKey and no hover",
      afterLock is { LockedKey: "Gwen", HoverKey: null, YourTurn: false });

// Blind pick: no assigned positions. Roles fall back to seat order so the board still
// has five distinct lanes and the dropdown can fix any that are wrong.
var blind = System.Text.Json.Nodes.JsonNode.Parse(sessionJson)!;
foreach (var m in blind["theirTeam"]!.AsArray()) m!["assignedPosition"] = "";
var blindDraft = DraftMapper.Map(blind, catalog);
Check("empty positions fall back to distinct roles",
      blindDraft is not null && blindDraft.Enemy.Select(s => s.Role).Distinct().Count() == 5);

// ARAM has a bench; that is not a draft this app understands.
var aram = System.Text.Json.Nodes.JsonNode.Parse(sessionJson)!;
aram["benchEnabled"] = true;
Check("ARAM sessions are rejected", DraftMapper.Map(aram, catalog) is null);

Check("position names map", DraftMapper.MapPosition("utility") == "Support" && DraftMapper.MapPosition("") is null);

// ── League client: real captures ─────────────────────────────────────────
// A custom game against bots, captured 2026-09-11: you in cell 0 with no assigned
// position, four bots with positions, an enemy side with no actions at all, and a
// 90-second clock. Three moments: before hovering, hovering Darius, locked Darius.
Console.WriteLine("\nLCU. captured payloads");
DraftPayload? Fixture(string name) =>
    DraftMapper.Map(System.Text.Json.Nodes.JsonNode.Parse(
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", name)))!, catalog);

var before = Fixture("session-custom-hover-none.json");
Check("capture maps", before is not null);
if (before is not null)
{
    Check("you are cell 0 with nothing shown", before.Ally[0] is { IsYou: true, ChampionKey: null });
    Check("bots keep their assigned roles",
          before.Ally[1].Role == "Jungle" && before.Ally[2].Role == "Bot" &&
          before.Ally[3].Role == "Top" && before.Ally[4].Role == "Support",
          string.Join(",", before.Ally.Select(s => s.Role)));
    Check("your unassigned seat gets the role left over, not the first one",
          before.Ally[0].Role == "Mid", $"got {before.Ally[0].Role}");
    Check("bot champions map", before.Ally[1].ChampionKey == "Diana" && before.Ally[3].ChampionKey == "Sett");
    Check("the empty enemy side is five unassigned seats with distinct roles",
          before.Enemy.Count == 5 && before.Enemy.All(s => s.ChampionKey is null) &&
          before.Enemy.Select(s => s.Role).Distinct().Count() == 5);
    Check("no enemy actions: picks remaining falls back to empty seats", before.EnemyPicksRemaining == 5);
    Check("your pick is in progress", before.YourTurn && before.Ally[0].OnTheClock);
    Check("90 s custom clock, not infinite", before is { TimerMs: 90000, TimerInfinite: false, TimerPhase: "BAN_PICK" });
    Check("no bans", before.Bans.Count == 0);
    Check("no assigned position means no role to follow", before is { YourRole: null, Autofilled: false });
}

var hovering = Fixture("session-custom-hover.json");
Check("a real hover is reported as a hover",
      hovering is { HoverKey: "Darius", LockedKey: null } && hovering.Ally[0].Hovering);

var lockedIn = Fixture("session-custom-locked.json");
Check("a real lock is reported as a lock",
      lockedIn is { LockedKey: "Darius", HoverKey: null, YourTurn: false, TimerPhase: "FINALIZATION" });
Check("after locking, the hero seat shows the champion", lockedIn?.Ally[0].ChampionKey == "Darius");

System.Text.Json.Nodes.JsonNode Raw(string name) => System.Text.Json.Nodes.JsonNode.Parse(
    File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", name)))!;
Check("lock-in finds your pick action on the clock",
      DraftMapper.YourPickAction(Raw("session-custom-hover-none.json")) is { Id: 0, InProgress: true });
Check("once locked there is no pick left to lock",
      DraftMapper.YourPickAction(Raw("session-custom-locked.json")) is null);

// A normal draft (queue 400), captured 2026-09-11. Your team has assigned positions; the
// enemy side has none at all, so their roles are unknown until inferred. Ten bans, all
// in the actions and none in the summary object.
// The same draft with the client putting you somewhere you did not queue for.
var autofilled = System.Text.Json.Nodes.JsonNode.Parse(
    File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "session-normal-midpick.json")))!;
foreach (var m in autofilled["myTeam"]!.AsArray())
    if (m!["cellId"]!.GetValue<int>() == autofilled["localPlayerCellId"]!.GetValue<int>())
    {
        m["assignedPosition"] = "utility";
        m["isAutofilled"] = true;
    }
foreach (var m in autofilled["myTeam"]!.AsArray())
    if (m!["assignedPosition"]!.GetValue<string>() == "utility" && !m["isAutofilled"]!.GetValue<bool>())
        m["assignedPosition"] = "top";
var autofilledDraft = DraftMapper.Map(autofilled, catalog);
Check("an autofill reports the role the client gave you, flagged",
      autofilledDraft is { YourRole: "Support", Autofilled: true },
      $"got {autofilledDraft?.YourRole} autofilled={autofilledDraft?.Autofilled}");

var normalMid = Fixture("session-normal-midpick.json");
Check("normal draft maps", normalMid is not null);
if (normalMid is not null)
{
    Check("your team's roles come from the client and are marked known",
          normalMid.Ally.All(s => s.RoleKnown) && normalMid.Ally.Single(s => s.IsYou).Role == "Top",
          string.Join(",", normalMid.Ally.Select(s => $"{s.Role}{(s.RoleKnown ? "" : "?")}")));
    Check("enemy roles are unknown, so none land in enemyRoles",
          normalMid.Enemy.All(s => !s.RoleKnown) && normalMid.EnemyRoles.Count == 0);
    Check("your assigned role is reported for the app to follow",
          normalMid is { YourRole: "Top", Autofilled: false });
    Check("enemy seats still get five distinct placeholder roles",
          normalMid.Enemy.Select(s => s.Role).Distinct().Count() == 5);
    Check("a locked enemy shows as locked", normalMid.Enemy[0] is { ChampionKey: "Diana", Hovering: false });
    Check("an enemy locking with the intent still set is not a hover",
          normalMid.Enemy[1] is { ChampionKey: "Thresh", Hovering: false });
    Check("ten bans from the action rounds", normalMid.Bans.Count == 10, $"got {normalMid.Bans.Count}");
    Check("enemy picks remaining counts their pending actions", normalMid.EnemyPicksRemaining == 3,
          $"got {normalMid.EnemyPicksRemaining}");
}

var normalDone = Fixture("session-normal-locked.json");
Check("fully locked normal draft: nothing remaining, you locked Garen",
      normalDone is { EnemyPicksRemaining: 0, LockedKey: "Garen", YourTurn: false });
Check("fully locked normal draft: all five enemies present and none known",
      normalDone is not null && normalDone.Enemy.All(s => s.ChampionKey is not null && !s.Hovering && !s.RoleKnown));

// ── Enemy roles: play rates and placement ────────────────────────────────
// The client never says who on the enemy side plays where. The app places each locked
// enemy from the play-rate table (Meraki Analytics championrates.json, the bundled
// snapshot here) plus one-of-each-role, and the running game confirms once it loads.
Console.WriteLine("\nroles. play-rate table");
var rates = RoleRates.FromJson(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "championrates.json")));
Check("snapshot parses with a patch and the whole roster", rates.Patch is not null && rates.Count > 150,
      $"patch {rates.Patch}, {rates.Count} champions");
Check("Diana is mostly jungle, sometimes mid, never top",
      rates.RatesFor(131) is { } diana && diana["Jungle"] > diana["Mid"] && diana["Mid"] > 0 && diana["Top"] == 0);
Check("an unknown id has no rates", rates.RatesFor(999999) is null);
Check("a broken body is refused", ((Func<bool>)(() =>
{
    try { RoleRates.FromJson("{}"); return false; } catch (InvalidOperationException) { return true; }
}))());

Console.WriteLine("\nroles. placement");
RolePick Pick(string key, string? fixedRole = null)
{
    var info = catalog.ByKey(key) ?? throw new InvalidOperationException($"{key} is not in the test catalog");
    return new RolePick(info.Key, RoleInference.WeightsFor(rates.RatesFor(info.NumericId), info.Tags, info.Ranged), fixedRole);
}
RoleGuess Of(IReadOnlyList<RoleGuess> guesses, string key) => guesses.Single(g => g.ChampionKey == key);
string Show(IReadOnlyList<RoleGuess> guesses) => string.Join(" ", guesses.Select(g => $"{g.ChampionKey}:{g.Role}@{g.Confidence:0.00}"));

Check("nothing locked, nothing placed", RoleInference.Infer([]).Count == 0);

// One pick: just its own rates. Diana leans jungle but mid is live, so not certain.
var alone = RoleInference.Infer([Pick("Diana")]);
Check("Diana alone is jungle, but not sure", Of(alone, "Diana") is { Role: "Jungle", Confidence: > 0.6 and < 0.9 }, Show(alone));

// The same Diana next to a jungler is mid, and surer than she was jungle on her own.
var withKha = RoleInference.Infer([Pick("Diana"), Pick("Khazix")]);
Check("Diana next to Kha'Zix moves to mid",
      Of(withKha, "Diana") is { Role: "Mid", Confidence: > 0.7 } && Of(withKha, "Khazix").Role == "Jungle", Show(withKha));

// A one-role champion is sure of itself.
var kha = RoleInference.Infer([Pick("Khazix")]);
Check("a one-role champion is placed with high confidence", Of(kha, "Khazix") is { Role: "Jungle", Confidence: > 0.9 }, Show(kha));

// The real enemy side from the captured normal draft: Diana, Thresh, Veigar, Nunu, Yone.
var five = RoleInference.Infer([Pick("Diana"), Pick("Thresh"), Pick("Veigar"), Pick("Nunu"), Pick("Yone")]);
Check("five picks fill five distinct roles", five.Select(g => g.Role).Distinct().Count() == 5, Show(five));
Check("the captured enemy side: Nunu jungle, Diana mid, Yone top, Thresh support, Veigar left for bot",
      Of(five, "Nunu").Role == "Jungle" && Of(five, "Diana").Role == "Mid" && Of(five, "Yone").Role == "Top" &&
      Of(five, "Thresh").Role == "Support" && Of(five, "Veigar").Role == "Bot", Show(five));
Check("Thresh support is near certain; Veigar bot, placed by elimination, is not",
      Of(five, "Thresh").Confidence > 0.95 && Of(five, "Veigar").Confidence < Of(five, "Thresh").Confidence, Show(five));
Check("Yone top is settled enough to brief on (over 0.85)", Of(five, "Yone").Confidence > 0.85, Show(five));

// A role you fixed by hand is kept at full confidence and the rest are placed around it.
var fixedTop = RoleInference.Infer([Pick("Diana", "Top"), Pick("Yone"), Pick("Khazix")]);
Check("a fixed role is honoured at confidence 1",
      Of(fixedTop, "Diana") is { Role: "Top", Confidence: 1.0 } && Of(fixedTop, "Yone").Role == "Mid" && Of(fixedTop, "Khazix").Role == "Jungle",
      Show(fixedTop));

// Two picks fixed to the same role cannot both be honoured; best effort, no exception.
var clash = RoleInference.Infer([Pick("Diana", "Top"), Pick("Yone", "Top")]);
Check("contradictory fixes fall back to the rates rather than throwing",
      clash.Count == 2 && clash.Select(g => g.Role).Distinct().Count() == 2, Show(clash));

// More picks than roles is nonsense from the caller; the first five are placed.
var six = RoleInference.Infer([Pick("Diana"), Pick("Thresh"), Pick("Veigar"), Pick("Nunu"), Pick("Yone"), Pick("Garen")]);
Check("a sixth pick is ignored, not an error", six.Count == 5);

// A champion the feed does not know yet (a new release) falls back to its classes.
var newMarksman = RoleInference.WeightsFor(null, ["Marksman"], ranged: true);
Check("no rates: a Marksman is placed bot from its class",
      RoleInference.Infer([new RolePick("Brand-new", newMarksman, null)])[0] is { Role: "Bot", Confidence: > 0.5 });
var nothingKnown = RoleInference.WeightsFor(null, [], ranged: false);
Check("no rates and no classes: an even spread",
      nothingKnown.Values.All(v => v == 1.0) &&
      RoleInference.Infer([new RolePick("Mystery", nothingKnown, null)])[0].Confidence == 0.2);
var dianaWeights = RoleInference.WeightsFor(rates.RatesFor(131), [], ranged: false);
Check("zero rates are floored, not impossible", dianaWeights["Top"] == RoleInference.Floor);
Check("a melee champion gets a tenth of the floor for bot",
      dianaWeights["Bot"] == RoleInference.Floor * RoleInference.MeleeBotFactor &&
      RoleInference.WeightsFor(rates.RatesFor(45), [], ranged: true)["Bot"] == RoleInference.Floor);

// ── The open field: best pick in the role, pool or not ───────────────────
// The shortlist ranks the player's pool; the same call also ranks everything played in
// the role this patch, so the app can say whether the draft wanted something else. The
// list offered to the model is built here, and nothing outside it survives the answer.
Console.WriteLine("\nopen. the field for a role");
var myPool = new[] { "Gwen", "Jax", "Aatrox" };
var openTop = OpenPool.For(catalog, rates, "Top", [], myPool);
Check("only champions the feed sees in the role", openTop.Count > 0 && openTop.All(c => c.PlayRate > 0),
      $"{openTop.Count} candidates");
Check("richest play rate first",
      openTop.Select(c => c.PlayRate).SequenceEqual(openTop.Select(c => c.PlayRate).OrderByDescending(r => r)));
Check("Darius is on the top list, Sona is not",
      openTop.Any(c => c.ChampionKey == "Darius") && openTop.All(c => c.ChampionKey != "Sona"));
Check("the player's own pool is marked, not removed",
      openTop.Single(c => c.ChampionKey == "Gwen").InPool && !openTop.Single(c => c.ChampionKey == "Darius").InPool);
Check("display names come along for champions whose key is not their name",
      OpenPool.For(catalog, rates, "Jungle", [], []).FirstOrDefault(c => c.ChampionKey == "MonkeyKing")?.Name == "Wukong");

var openAfterBans = OpenPool.For(catalog, rates, "Top", ["darius", "Jax"], myPool);
Check("banned and already-picked champions are gone, case regardless",
      openAfterBans.All(c => c.ChampionKey is not ("Darius" or "Jax")) &&
      openAfterBans.Count == openTop.Count - 2, $"{openTop.Count} then {openAfterBans.Count}");

Check("a different role is a different field",
      OpenPool.For(catalog, rates, "Support", [], []).Any(c => c.ChampionKey == "Sona") &&
      OpenPool.For(catalog, rates, "Support", [], []).All(c => c.ChampionKey != "Darius"));
Check("the ceiling holds", OpenPool.For(catalog, rates, "Top", [], [], max: 5).Count == 5);
Check("no play-rate table at all means no open field, not a guessed one",
      OpenPool.For(catalog, RoleRates.FromJson("""{"data":{"1":{"TOP":{"playRate":0}}}}"""), "Top", [], []).Count == 0);

// ── Live game: positions from the running game ───────────────────────────
// The Live Client Data API carries a position for all ten players. The fixture is
// hand-written from Riot's documentation; a real capture should replace it.
Console.WriteLine("\ngame. positions from the running game");
var gameJson = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "allgamedata-synthetic.json"));
var game = LiveGameMapper.Map(System.Text.Json.Nodes.JsonNode.Parse(gameJson), catalog);
Check("player list maps", game is not null);
if (game is not null)
{
    Check("your team is found through the active player, and you are top",
          game.YourRole == "Top" && game.AllyRoles.Count == 5 && game.AllyRoles["Garen"] == "Top");
    Check("all five enemy positions come through",
          game.EnemyRoles.Count == 5 && game.EnemyRoles["Diana"] == "Mid" && game.EnemyRoles["Yone"] == "Top" &&
          game.EnemyRoles["Veigar"] == "Bot" && game.EnemyRoles["Thresh"] == "Support",
          string.Join(",", game.EnemyRoles.Select(kv => $"{kv.Key}:{kv.Value}")));
    Check("the internal name maps to the Data Dragon key (Wukong is MonkeyKing)", game.AllyRoles.ContainsKey("MonkeyKing"));
    Check("a player without the internal name is matched by display name (Nunu & Willump)", game.EnemyRoles.ContainsKey("Nunu"));
}

var noPositions = System.Text.Json.Nodes.JsonNode.Parse(gameJson)!;
foreach (var p in noPositions["allPlayers"]!.AsArray()) p!["position"] = "";
Check("a mode without positions yields nothing, so the draft-time guess stands",
      LiveGameMapper.Map(noPositions, catalog) is null);

var stranger = System.Text.Json.Nodes.JsonNode.Parse(gameJson)!;
stranger["activePlayer"]!["riotId"] = "Somebody#Else";
stranger["activePlayer"]!["summonerName"] = "Somebody";
Check("not finding yourself in the list yields nothing", LiveGameMapper.Map(stranger, catalog) is null);
Check("an empty payload yields nothing", LiveGameMapper.Map(System.Text.Json.Nodes.JsonNode.Parse("{}"), catalog) is null);

// ── API key: encrypted at rest ───────────────────────────────────────────
// Everyone brings their own key, and config.json must never hold it in the clear.
Console.WriteLine("\nconfig. the API key on disk");
const string fakeKey = "sk-ant-test-0123456789abcdef";
var keyed = AppConfig.Load();
keyed.ApiKey = fakeKey;
keyed.Save();
var onDisk = File.ReadAllText(AppPaths.ConfigFile);
Check("the key is not written in the clear", !onDisk.Contains(fakeKey));
Check("an encrypted blob is written instead", onDisk.Contains("\"ApiKeyProtected\""));
Check("no plain ApiKey field is written", !onDisk.Contains("\"ApiKey\""));
var reloaded = AppConfig.Load();
Check("the key reads back on the same account", reloaded.ApiKey == fakeKey && !reloaded.KeyUnreadable);

File.WriteAllText(AppPaths.ConfigFile, $$"""{ "ApiKey": "{{fakeKey}}", "PrimaryRole": "Mid" }""");
var migrated = AppConfig.Load();
var afterMigration = File.ReadAllText(AppPaths.ConfigFile);
Check("a plain key from an older build is read", migrated.ApiKey == fakeKey && migrated.PrimaryRole == "Mid");
Check("and the file is rewritten without it", !afterMigration.Contains(fakeKey) && afterMigration.Contains("\"ApiKeyProtected\""));

File.WriteAllText(AppPaths.ConfigFile, """{ "ApiKeyProtected": "AQAAANCMnd8BFdERjHoAwE/Cl+sBAAAA", "PrimaryRole": "Jungle" }""");
var foreign = AppConfig.Load();
Check("a blob from another machine yields no key and says so",
      foreign.ApiKey is null && foreign.KeyUnreadable && foreign.PrimaryRole == "Jungle");

migrated.ApiKey = null;
migrated.Save();
Check("removing the key removes the blob", !File.ReadAllText(AppPaths.ConfigFile).Contains("ApiKeyProtected"));

Console.WriteLine($"\n{passed} passed, {failed} failed");
try { Directory.Delete(sandbox, recursive: true); } catch { /* sqlite may still hold handles */ }
return failed == 0 ? 0 : 1;

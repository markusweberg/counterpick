using Microsoft.Data.Sqlite;

namespace Counterpick.App.Services;

public sealed record PoolEntry(string ChampionKey, string Role);
public sealed record Note(long Id, string ChampionKey, string OpponentKey, string Role, string Body,
                         DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);
public sealed record GameResult(string ChampionKey, string OpponentKey, string Role, bool Won, DateTimeOffset PlayedAt);
public sealed record MatchupRecord(int Wins, int Losses);
public sealed record DataCounts(int Notes, int Games, int Pool);

/// <summary>
/// YOUR data - champion pool, matchup notes, game results. One SQLite file under
/// %APPDATA%\Counterpick, and the only thing in the app that cannot be regenerated.
///
/// Generated briefs deliberately live in a separate database (see <see cref="BriefCache"/>)
/// so backups stay small and cache corruption can never reach your notes.
/// </summary>
public sealed class Storage
{
    // v1 also held a `briefs` table; v2 moved it out to cache.db.
    // v3 adds notes.updated_at, so an edit is visible to the backup fingerprint.
    private const int SchemaVersion = 3;

    public string DatabaseFile { get; }
    private readonly string _connectionString;

    public Storage(string? databaseFile = null)
    {
        AppPaths.EnsureCreated();
        DatabaseFile = databaseFile ?? AppPaths.DatabaseFile;
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = DatabaseFile,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false      // so a snapshot or restore can replace the file cleanly
        }.ToString();
    }

    private SqliteConnection Open()
    {
        var c = new SqliteConnection(_connectionString);
        c.Open();
        using var pragma = c.CreateCommand();
        // WAL keeps reads from blocking writes. It also means the newest commits can sit
        // in a -wal sidecar file, which is exactly why backups go through VACUUM INTO
        // rather than copying the .db by hand.
        pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA foreign_keys=ON; PRAGMA synchronous=NORMAL;";
        pragma.ExecuteNonQuery();
        return c;
    }

    /// <param name="beforeMigration">
    /// Called with the current schema version before any upgrade runs, so a snapshot can
    /// be taken first. Migration is the single most dangerous thing the app does to your
    /// data, and it is the cheapest possible moment to insure against.
    /// </param>
    public void EnsureSchema(Action<int>? beforeMigration = null)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();

        cmd.CommandText = "PRAGMA user_version;";
        var current = Convert.ToInt32(cmd.ExecuteScalar() ?? 0);
        if (current >= SchemaVersion) return;

        if (current > 0) beforeMigration?.Invoke(current);

        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS pool (
                champion_key TEXT NOT NULL,
                role         TEXT NOT NULL,
                added_at     TEXT NOT NULL,
                PRIMARY KEY (champion_key, role)
            );

            CREATE TABLE IF NOT EXISTS notes (
                id           INTEGER PRIMARY KEY AUTOINCREMENT,
                champion_key TEXT NOT NULL,
                opponent_key TEXT NOT NULL,
                role         TEXT NOT NULL,
                body         TEXT NOT NULL,
                created_at   TEXT NOT NULL,
                updated_at   TEXT
            );
            CREATE INDEX IF NOT EXISTS idx_notes_matchup
                ON notes (champion_key, opponent_key, role);

            -- Guards against a restore or a re-import duplicating what is already here.
            CREATE UNIQUE INDEX IF NOT EXISTS idx_notes_identity
                ON notes (champion_key, opponent_key, role, created_at, body);

            CREATE TABLE IF NOT EXISTS games (
                id           INTEGER PRIMARY KEY AUTOINCREMENT,
                champion_key TEXT NOT NULL,
                opponent_key TEXT NOT NULL,
                role         TEXT NOT NULL,
                won          INTEGER NOT NULL,
                played_at    TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS idx_games_matchup
                ON games (champion_key, opponent_key, role);
            CREATE UNIQUE INDEX IF NOT EXISTS idx_games_identity
                ON games (champion_key, opponent_key, role, played_at);
            """;
        cmd.ExecuteNonQuery();

        if (current == 1)
        {
            // Briefs were only ever a cache; they now live in cache.db.
            cmd.CommandText = "DROP TABLE IF EXISTS briefs;";
            cmd.ExecuteNonQuery();
        }

        if (current is 1 or 2)
        {
            // Nullable, so ADD COLUMN needs no default; reads coalesce to created_at.
            cmd.CommandText = "ALTER TABLE notes ADD COLUMN updated_at TEXT;";
            cmd.ExecuteNonQuery();
        }

        cmd.CommandText = $"PRAGMA user_version={SchemaVersion};";
        cmd.ExecuteNonQuery();
    }

    // ── pool ─────────────────────────────────────────────────────────────

    public List<PoolEntry> GetPool(string? role = null)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = role is null
            ? "SELECT champion_key, role FROM pool ORDER BY added_at;"
            : "SELECT champion_key, role FROM pool WHERE role = $r ORDER BY added_at;";
        if (role is not null) cmd.Parameters.AddWithValue("$r", role);

        var list = new List<PoolEntry>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(new PoolEntry(r.GetString(0), r.GetString(1)));
        return list;
    }

    public void SetPool(string role, IEnumerable<string> championKeys)
    {
        using var c = Open();
        using var tx = c.BeginTransaction();

        using (var del = c.CreateCommand())
        {
            del.CommandText = "DELETE FROM pool WHERE role = $r;";
            del.Parameters.AddWithValue("$r", role);
            del.ExecuteNonQuery();
        }

        foreach (var key in championKeys)
        {
            using var ins = c.CreateCommand();
            ins.CommandText =
                "INSERT OR IGNORE INTO pool (champion_key, role, added_at) VALUES ($k, $r, $t);";
            ins.Parameters.AddWithValue("$k", key);
            ins.Parameters.AddWithValue("$r", role);
            ins.Parameters.AddWithValue("$t", DateTimeOffset.UtcNow.ToString("O"));
            ins.ExecuteNonQuery();
        }

        tx.Commit();
    }

    // ── notes ────────────────────────────────────────────────────────────

    public List<Note> GetNotes(string championKey, string opponentKey, string role)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            SELECT id, champion_key, opponent_key, role, body, created_at,
                   COALESCE(updated_at, created_at) FROM notes
            WHERE champion_key = $c AND opponent_key = $o AND role = $r
            ORDER BY created_at DESC, id DESC;
            """;
        cmd.Parameters.AddWithValue("$c", championKey);
        cmd.Parameters.AddWithValue("$o", opponentKey);
        cmd.Parameters.AddWithValue("$r", role);
        return ReadNotes(cmd);
    }

    public List<Note> AllNotes()
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            SELECT id, champion_key, opponent_key, role, body, created_at,
                   COALESCE(updated_at, created_at) FROM notes
            ORDER BY role, champion_key, opponent_key, created_at DESC, id DESC;
            """;
        return ReadNotes(cmd);
    }

    private static List<Note> ReadNotes(SqliteCommand cmd)
    {
        var list = new List<Note>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(new Note(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3),
                              r.GetString(4), DateTimeOffset.Parse(r.GetString(5)),
                              DateTimeOffset.Parse(r.GetString(6))));
        return list;
    }

    /// <summary>Returns the new note's id, or 0 if an identical note already existed.</summary>
    public long AddNote(string championKey, string opponentKey, string role, string body,
                        DateTimeOffset? createdAt = null)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            INSERT OR IGNORE INTO notes (champion_key, opponent_key, role, body, created_at, updated_at)
            VALUES ($c, $o, $r, $b, $t, $t);
            SELECT CASE WHEN changes() = 0 THEN 0 ELSE last_insert_rowid() END;
            """;
        cmd.Parameters.AddWithValue("$c", championKey);
        cmd.Parameters.AddWithValue("$o", opponentKey);
        cmd.Parameters.AddWithValue("$r", role);
        cmd.Parameters.AddWithValue("$b", body);
        cmd.Parameters.AddWithValue("$t", (createdAt ?? DateTimeOffset.UtcNow).ToString("O"));
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    // ── results ──────────────────────────────────────────────────────────

    /// <summary>
    /// Rewrite a note's text, keeping its original date. Returns false if the note is
    /// gone, or if the edit would make it identical to another note in the same matchup.
    /// </summary>
    public bool UpdateNote(long id, string body)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            UPDATE OR IGNORE notes SET body = $b, updated_at = $t WHERE id = $i;
            SELECT changes();
            """;
        cmd.Parameters.AddWithValue("$b", body);
        cmd.Parameters.AddWithValue("$t", DateTimeOffset.UtcNow.ToString("O"));
        cmd.Parameters.AddWithValue("$i", id);
        return Convert.ToInt32(cmd.ExecuteScalar()) > 0;
    }

    /// <summary>
    /// Delete a note. Snapshots keep the old copy, so this is recoverable through the
    /// Data panel rather than being final.
    /// </summary>
    public bool DeleteNote(long id)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "DELETE FROM notes WHERE id = $i; SELECT changes();";
        cmd.Parameters.AddWithValue("$i", id);
        return Convert.ToInt32(cmd.ExecuteScalar()) > 0;
    }

    public void RecordGame(string championKey, string opponentKey, string role, bool won,
                           DateTimeOffset? playedAt = null)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            INSERT OR IGNORE INTO games (champion_key, opponent_key, role, won, played_at)
            VALUES ($c, $o, $r, $w, $t);
            """;
        cmd.Parameters.AddWithValue("$c", championKey);
        cmd.Parameters.AddWithValue("$o", opponentKey);
        cmd.Parameters.AddWithValue("$r", role);
        cmd.Parameters.AddWithValue("$w", won ? 1 : 0);
        cmd.Parameters.AddWithValue("$t", (playedAt ?? DateTimeOffset.UtcNow).ToString("O"));
        cmd.ExecuteNonQuery();
    }

    public List<GameResult> AllGames()
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            SELECT champion_key, opponent_key, role, won, played_at FROM games
            ORDER BY played_at;
            """;
        var list = new List<GameResult>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(new GameResult(r.GetString(0), r.GetString(1), r.GetString(2),
                                    r.GetInt32(3) != 0, DateTimeOffset.Parse(r.GetString(4))));
        return list;
    }

    public MatchupRecord GetRecord(string championKey, string opponentKey, string role)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            SELECT COALESCE(SUM(won), 0), COUNT(*) - COALESCE(SUM(won), 0) FROM games
            WHERE champion_key = $c AND opponent_key = $o AND role = $r;
            """;
        cmd.Parameters.AddWithValue("$c", championKey);
        cmd.Parameters.AddWithValue("$o", opponentKey);
        cmd.Parameters.AddWithValue("$r", role);

        using var r = cmd.ExecuteReader();
        return r.Read() ? new MatchupRecord(r.GetInt32(0), r.GetInt32(1)) : new MatchupRecord(0, 0);
    }

    // ── backup support ───────────────────────────────────────────────────

    public DataCounts Counts()
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            SELECT (SELECT COUNT(*) FROM notes),
                   (SELECT COUNT(*) FROM games),
                   (SELECT COUNT(*) FROM pool);
            """;
        using var r = cmd.ExecuteReader();
        return r.Read() ? new DataCounts(r.GetInt32(0), r.GetInt32(1), r.GetInt32(2))
                        : new DataCounts(0, 0, 0);
    }

    public bool IsEmpty()
    {
        var c = Counts();
        return c.Notes == 0 && c.Games == 0 && c.Pool == 0;
    }

    /// <summary>
    /// Cheap content signature, used to skip a snapshot when nothing has changed since
    /// the last one.
    ///
    /// Counts catch deletes and highest ids catch inserts, but an edit changes neither -
    /// hence the newest updated_at, without which a rewritten note could go unsnapshotted.
    /// </summary>
    public string Fingerprint()
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            SELECT (SELECT COUNT(*) FROM notes)
                || '.' || (SELECT COALESCE(MAX(id), 0) FROM notes)
                || '.' || (SELECT COALESCE(MAX(COALESCE(updated_at, created_at)), '') FROM notes)
                || '-' || (SELECT COUNT(*) FROM games)
                || '.' || (SELECT COALESCE(MAX(id), 0) FROM games)
                || '-' || (SELECT COUNT(*) FROM pool)
                || '.' || (SELECT COALESCE(MAX(added_at), '') FROM pool);
            """;
        return Convert.ToString(cmd.ExecuteScalar()) ?? "0";
    }

    /// <summary>
    /// Write a consistent, compacted copy to <paramref name="destination"/>.
    /// VACUUM INTO folds in anything sitting in the -wal file, which a plain file copy
    /// would silently miss. The destination must not already exist.
    /// </summary>
    public void SnapshotTo(string destination)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "VACUUM INTO $dest;";
        cmd.Parameters.AddWithValue("$dest", destination);
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Merge notes, games and pool entries from another database file. Unique indexes on
    /// the natural identity of each row make this idempotent - importing the same backup
    /// twice adds nothing the second time.
    /// </summary>
    public DataCounts ImportFrom(string sourceDatabaseFile)
    {
        var before = Counts();

        using var c = Open();
        using (var attach = c.CreateCommand())
        {
            attach.CommandText = "ATTACH DATABASE $src AS incoming;";
            attach.Parameters.AddWithValue("$src", sourceDatabaseFile);
            attach.ExecuteNonQuery();
        }

        try
        {
            using var tx = c.BeginTransaction();
            using var cmd = c.CreateCommand();
            cmd.CommandText = """
                INSERT OR IGNORE INTO main.notes (champion_key, opponent_key, role, body, created_at, updated_at)
                    SELECT champion_key, opponent_key, role, body, created_at,
                           COALESCE(updated_at, created_at) FROM incoming.notes;
                INSERT OR IGNORE INTO main.games (champion_key, opponent_key, role, won, played_at)
                    SELECT champion_key, opponent_key, role, won, played_at FROM incoming.games;
                INSERT OR IGNORE INTO main.pool (champion_key, role, added_at)
                    SELECT champion_key, role, added_at FROM incoming.pool;
                """;
            cmd.ExecuteNonQuery();
            tx.Commit();
        }
        finally
        {
            using var detach = c.CreateCommand();
            detach.CommandText = "DETACH DATABASE incoming;";
            detach.ExecuteNonQuery();
        }

        var after = Counts();
        return new DataCounts(after.Notes - before.Notes,
                              after.Games - before.Games,
                              after.Pool - before.Pool);
    }
}

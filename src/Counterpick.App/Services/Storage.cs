using Microsoft.Data.Sqlite;

namespace Counterpick.App.Services;

public sealed record PoolEntry(string ChampionKey, string Role);
public sealed record Note(long Id, string Body, DateTimeOffset CreatedAt);
public sealed record MatchupRecord(int Wins, int Losses);

/// <summary>
/// Local SQLite store for the champion pool, matchup notes, results, and cached briefs.
/// One file under %APPDATA%\Counterpick. No migrations framework - the schema is created
/// if absent and versioned with user_version so it can be stepped forward later.
/// </summary>
public sealed class Storage
{
    private const int SchemaVersion = 1;
    private readonly string _connectionString;

    public Storage(string? databaseFile = null)
    {
        AppPaths.EnsureCreated();
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databaseFile ?? AppPaths.DatabaseFile,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared
        }.ToString();
    }

    private SqliteConnection Open()
    {
        var c = new SqliteConnection(_connectionString);
        c.Open();
        using var pragma = c.CreateCommand();
        // WAL keeps reads from blocking while a brief is being written.
        pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA foreign_keys=ON;";
        pragma.ExecuteNonQuery();
        return c;
    }

    public void EnsureSchema()
    {
        using var c = Open();
        using var cmd = c.CreateCommand();

        cmd.CommandText = "PRAGMA user_version;";
        var current = Convert.ToInt32(cmd.ExecuteScalar() ?? 0);
        if (current >= SchemaVersion) return;

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
                created_at   TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS idx_notes_matchup
                ON notes (champion_key, opponent_key, role);

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

            -- Cached briefs. notes_fingerprint is the newest note id for the matchup, so
            -- writing a note automatically invalidates the brief that predates it.
            CREATE TABLE IF NOT EXISTS briefs (
                champion_key      TEXT NOT NULL,
                opponent_key      TEXT NOT NULL,
                role              TEXT NOT NULL,
                notes_fingerprint TEXT NOT NULL,
                body              TEXT NOT NULL,
                created_at        TEXT NOT NULL,
                PRIMARY KEY (champion_key, opponent_key, role)
            );
            """;
        cmd.ExecuteNonQuery();

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
            SELECT id, body, created_at FROM notes
            WHERE champion_key = $c AND opponent_key = $o AND role = $r
            ORDER BY created_at DESC;
            """;
        cmd.Parameters.AddWithValue("$c", championKey);
        cmd.Parameters.AddWithValue("$o", opponentKey);
        cmd.Parameters.AddWithValue("$r", role);

        var list = new List<Note>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            list.Add(new Note(reader.GetInt64(0), reader.GetString(1),
                              DateTimeOffset.Parse(reader.GetString(2))));
        return list;
    }

    public long AddNote(string championKey, string opponentKey, string role, string body)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            INSERT INTO notes (champion_key, opponent_key, role, body, created_at)
            VALUES ($c, $o, $r, $b, $t);
            SELECT last_insert_rowid();
            """;
        cmd.Parameters.AddWithValue("$c", championKey);
        cmd.Parameters.AddWithValue("$o", opponentKey);
        cmd.Parameters.AddWithValue("$r", role);
        cmd.Parameters.AddWithValue("$b", body);
        cmd.Parameters.AddWithValue("$t", DateTimeOffset.UtcNow.ToString("O"));
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    // ── results ──────────────────────────────────────────────────────────

    public void RecordGame(string championKey, string opponentKey, string role, bool won)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            INSERT INTO games (champion_key, opponent_key, role, won, played_at)
            VALUES ($c, $o, $r, $w, $t);
            """;
        cmd.Parameters.AddWithValue("$c", championKey);
        cmd.Parameters.AddWithValue("$o", opponentKey);
        cmd.Parameters.AddWithValue("$r", role);
        cmd.Parameters.AddWithValue("$w", won ? 1 : 0);
        cmd.Parameters.AddWithValue("$t", DateTimeOffset.UtcNow.ToString("O"));
        cmd.ExecuteNonQuery();
    }

    public MatchupRecord GetRecord(string championKey, string opponentKey, string role)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            SELECT SUM(won), COUNT(*) - SUM(won) FROM games
            WHERE champion_key = $c AND opponent_key = $o AND role = $r;
            """;
        cmd.Parameters.AddWithValue("$c", championKey);
        cmd.Parameters.AddWithValue("$o", opponentKey);
        cmd.Parameters.AddWithValue("$r", role);

        using var r = cmd.ExecuteReader();
        if (!r.Read() || r.IsDBNull(0)) return new MatchupRecord(0, 0);
        return new MatchupRecord(r.GetInt32(0), r.GetInt32(1));
    }

    // ── brief cache ──────────────────────────────────────────────────────

    /// <summary>
    /// A cached brief is only valid while no newer note exists for the matchup, so the
    /// newest note id doubles as the cache key. No notes yet means the fingerprint "0".
    /// </summary>
    public string NotesFingerprint(string championKey, string opponentKey, string role)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            SELECT COALESCE(MAX(id), 0) FROM notes
            WHERE champion_key = $c AND opponent_key = $o AND role = $r;
            """;
        cmd.Parameters.AddWithValue("$c", championKey);
        cmd.Parameters.AddWithValue("$o", opponentKey);
        cmd.Parameters.AddWithValue("$r", role);
        return Convert.ToInt64(cmd.ExecuteScalar()).ToString();
    }

    public string? GetCachedBrief(string championKey, string opponentKey, string role)
    {
        var fingerprint = NotesFingerprint(championKey, opponentKey, role);
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            SELECT body FROM briefs
            WHERE champion_key = $c AND opponent_key = $o AND role = $r
              AND notes_fingerprint = $f;
            """;
        cmd.Parameters.AddWithValue("$c", championKey);
        cmd.Parameters.AddWithValue("$o", opponentKey);
        cmd.Parameters.AddWithValue("$r", role);
        cmd.Parameters.AddWithValue("$f", fingerprint);
        return cmd.ExecuteScalar() as string;
    }

    public void CacheBrief(string championKey, string opponentKey, string role, string body)
    {
        var fingerprint = NotesFingerprint(championKey, opponentKey, role);
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            INSERT INTO briefs (champion_key, opponent_key, role, notes_fingerprint, body, created_at)
            VALUES ($c, $o, $r, $f, $b, $t)
            ON CONFLICT (champion_key, opponent_key, role) DO UPDATE SET
                notes_fingerprint = excluded.notes_fingerprint,
                body              = excluded.body,
                created_at        = excluded.created_at;
            """;
        cmd.Parameters.AddWithValue("$c", championKey);
        cmd.Parameters.AddWithValue("$o", opponentKey);
        cmd.Parameters.AddWithValue("$r", role);
        cmd.Parameters.AddWithValue("$f", fingerprint);
        cmd.Parameters.AddWithValue("$b", body);
        cmd.Parameters.AddWithValue("$t", DateTimeOffset.UtcNow.ToString("O"));
        cmd.ExecuteNonQuery();
    }
}

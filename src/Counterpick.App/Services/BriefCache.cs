using Microsoft.Data.Sqlite;

namespace Counterpick.App.Services;

/// <summary>
/// Claude's generated briefs, in their own database.
///
/// Nothing here is backed up and nothing here is precious - the whole file can be
/// deleted at any time and the app just pays for the briefs again. Keeping it out of
/// counterpick.db is what lets "clear the cache" be a safe instruction.
/// </summary>
public sealed class BriefCache
{
    private const int SchemaVersion = 1;
    private readonly string _connectionString;

    public string DatabaseFile { get; }

    public BriefCache(string? databaseFile = null)
    {
        AppPaths.EnsureCreated();
        DatabaseFile = databaseFile ?? AppPaths.CacheDatabaseFile;
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = DatabaseFile,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false
        }.ToString();
    }

    private SqliteConnection Open()
    {
        var c = new SqliteConnection(_connectionString);
        c.Open();
        using var pragma = c.CreateCommand();
        pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL;";
        pragma.ExecuteNonQuery();
        return c;
    }

    public void EnsureSchema()
    {
        using var c = Open();
        using var cmd = c.CreateCommand();

        cmd.CommandText = "PRAGMA user_version;";
        if (Convert.ToInt32(cmd.ExecuteScalar() ?? 0) >= SchemaVersion) return;

        cmd.CommandText = """
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

    /// <param name="notesFingerprint">
    /// Newest note id for this matchup, from <see cref="Storage"/>. A cached brief is only
    /// valid while it is not older than what you have since learned, so writing a note
    /// invalidates the brief that predates it without any explicit cache busting.
    /// </param>
    public string? Get(string championKey, string opponentKey, string role, string notesFingerprint)
    {
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
        cmd.Parameters.AddWithValue("$f", notesFingerprint);
        return cmd.ExecuteScalar() as string;
    }

    public void Put(string championKey, string opponentKey, string role,
                    string notesFingerprint, string body)
    {
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
        cmd.Parameters.AddWithValue("$f", notesFingerprint);
        cmd.Parameters.AddWithValue("$b", body);
        cmd.Parameters.AddWithValue("$t", DateTimeOffset.UtcNow.ToString("O"));
        cmd.ExecuteNonQuery();
    }

    public int Count()
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM briefs;";
        return Convert.ToInt32(cmd.ExecuteScalar() ?? 0);
    }

    public void Clear()
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "DELETE FROM briefs; VACUUM;";
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Notes fingerprint for one matchup. Lives here rather than on Storage because it
    /// exists only to key this cache.
    /// </summary>
    public static string FingerprintFor(Storage storage, string championKey, string opponentKey, string role)
    {
        var notes = storage.GetNotes(championKey, opponentKey, role);
        return notes.Count == 0 ? "0" : notes.Max(n => n.Id).ToString();
    }
}

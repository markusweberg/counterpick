using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Counterpick.App.Services;

public sealed record ExportedNote(string ChampionKey, string OpponentKey, string Role, string Body, DateTimeOffset CreatedAt);
public sealed record ExportedGame(string ChampionKey, string OpponentKey, string Role, bool Won, DateTimeOffset PlayedAt);
public sealed record ExportedPool(string ChampionKey, string Role);

public sealed record ExportBundle(
    string App,
    int Version,
    DateTimeOffset ExportedAt,
    IReadOnlyList<ExportedNote> Notes,
    IReadOnlyList<ExportedGame> Games,
    IReadOnlyList<ExportedPool> Pool);

/// <summary>
/// Export and import in formats that outlive the app.
///
/// A SQLite snapshot is the right thing for restoring into Counterpick, but it is a
/// binary blob that needs tooling to read. These two formats mean the notes survive the
/// app being abandoned: JSON round-trips back in, and Markdown is readable in Notepad
/// with no software at all.
/// </summary>
public static class DataTransfer
{
    /// <summary>No BOM: some JSON tooling chokes on it, and nothing here needs one.</summary>
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static ExportBundle Bundle(Storage storage) => new(
        App: "Counterpick",
        Version: 1,
        ExportedAt: DateTimeOffset.Now,
        Notes: storage.AllNotes()
                      .Select(n => new ExportedNote(n.ChampionKey, n.OpponentKey, n.Role, n.Body, n.CreatedAt))
                      .ToList(),
        Games: storage.AllGames()
                      .Select(g => new ExportedGame(g.ChampionKey, g.OpponentKey, g.Role, g.Won, g.PlayedAt))
                      .ToList(),
        Pool: storage.GetPool()
                     .Select(p => new ExportedPool(p.ChampionKey, p.Role))
                     .ToList());

    public static void WriteJson(Storage storage, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(Bundle(storage), Json), Utf8);
    }

    /// <summary>
    /// Notes grouped by role, then by matchup, newest first, with your record on each
    /// heading. Written for a person to read, not for the app to parse back.
    /// </summary>
    public static void WriteMarkdown(Storage storage, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        var notes = storage.AllNotes();
        var sb = new StringBuilder();
        sb.AppendLine("# Counterpick notes");
        sb.AppendLine();
        sb.AppendLine($"Exported {DateTimeOffset.Now:yyyy-MM-dd HH:mm}. "
                    + $"{notes.Count} note{(notes.Count == 1 ? "" : "s")} across "
                    + $"{notes.Select(n => (n.ChampionKey, n.OpponentKey, n.Role)).Distinct().Count()} matchups.");
        sb.AppendLine();

        if (notes.Count == 0)
        {
            sb.AppendLine("_No notes yet._");
            File.WriteAllText(path, sb.ToString(), Utf8);
            return;
        }

        foreach (var byRole in notes.GroupBy(n => n.Role).OrderBy(g => g.Key))
        {
            sb.AppendLine($"## {byRole.Key}");
            sb.AppendLine();

            foreach (var matchup in byRole.GroupBy(n => (n.ChampionKey, n.OpponentKey))
                                          .OrderBy(g => g.Key.ChampionKey)
                                          .ThenBy(g => g.Key.OpponentKey))
            {
                var record = storage.GetRecord(matchup.Key.ChampionKey, matchup.Key.OpponentKey, byRole.Key);
                var played = record.Wins + record.Losses > 0
                    ? $" — {record.Wins}–{record.Losses}"
                    : "";
                sb.AppendLine($"### {matchup.Key.ChampionKey} into {matchup.Key.OpponentKey}{played}");
                sb.AppendLine();

                foreach (var note in matchup.OrderByDescending(n => n.CreatedAt))
                    sb.AppendLine($"- **{note.CreatedAt:yyyy-MM-dd}** — {note.Body}");

                sb.AppendLine();
            }
        }

        File.WriteAllText(path, sb.ToString(), Utf8);
    }

    /// <summary>Write both formats to the exports folder. Returns the paths written.</summary>
    public static (string Json, string Markdown) ExportAll(Storage storage)
    {
        AppPaths.EnsureCreated();
        var json = Path.Combine(AppPaths.ExportsDir, "notes.json");
        var markdown = Path.Combine(AppPaths.ExportsDir, "notes.md");
        WriteJson(storage, json);
        WriteMarkdown(storage, markdown);
        return (json, markdown);
    }

    /// <summary>
    /// Merge a JSON export back in. Idempotent - the unique indexes in Storage mean
    /// importing the same file twice adds nothing the second time.
    /// </summary>
    public static DataCounts ImportJson(Storage storage, string path)
    {
        var bundle = JsonSerializer.Deserialize<ExportBundle>(File.ReadAllText(path), Json)
                     ?? throw new InvalidDataException("Not a Counterpick export.");

        if (!string.Equals(bundle.App, "Counterpick", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Not a Counterpick export (found '{bundle.App}').");

        var before = storage.Counts();

        foreach (var n in bundle.Notes)
            storage.AddNote(n.ChampionKey, n.OpponentKey, n.Role, n.Body, n.CreatedAt);

        foreach (var g in bundle.Games)
            storage.RecordGame(g.ChampionKey, g.OpponentKey, g.Role, g.Won, g.PlayedAt);

        foreach (var byRole in bundle.Pool.GroupBy(p => p.Role))
        {
            var existing = storage.GetPool(byRole.Key).Select(p => p.ChampionKey);
            storage.SetPool(byRole.Key, existing.Union(byRole.Select(p => p.ChampionKey)));
        }

        var after = storage.Counts();
        return new DataCounts(after.Notes - before.Notes,
                              after.Games - before.Games,
                              after.Pool - before.Pool);
    }
}

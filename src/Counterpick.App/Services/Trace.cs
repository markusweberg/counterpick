using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Counterpick.App.Services;

/// <summary>
/// Plain-text trace of what the app saw and did, under %APPDATA%\Counterpick\logs.
///
/// Two files per session: a readable .log with one line per event, and a .jsonl with
/// the raw League client payloads so a draft can be replayed against the mapper later.
/// Both are diagnostics, never backed up, pruned after two weeks.
/// </summary>
public static class Trace
{
    private static readonly object Gate = new();
    private static readonly JsonSerializerOptions Compact = new() { WriteIndented = false };
    private static string? _log;
    private static string? _payloads;

    public static string Dir => Path.Combine(AppPaths.Root, "logs");

    public static void Start()
    {
        Directory.CreateDirectory(Dir);
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        _log = Path.Combine(Dir, $"counterpick-{stamp}.log");
        _payloads = Path.Combine(Dir, $"lcu-{stamp}.jsonl");
        Prune();
        Write("app", $"started, data dir {AppPaths.Root}");
    }

    /// <summary>One readable line. Safe from any thread; silently drops on I/O errors.</summary>
    public static void Write(string area, string message)
    {
        if (_log is null) return;
        var line = $"{DateTime.Now:HH:mm:ss.fff} [{area}] {message}{Environment.NewLine}";
        lock (Gate)
        {
            try { File.AppendAllText(_log, line); } catch (IOException) { }
        }
    }

    /// <summary>A raw payload, one JSON object per line, for replay.</summary>
    public static void Payload(string area, string label, JsonNode? data)
    {
        if (_payloads is null) return;
        var record = new JsonObject
        {
            ["t"] = DateTime.Now.ToString("O"),
            ["area"] = area,
            ["label"] = label,
            ["data"] = data?.DeepClone()
        };
        var line = record.ToJsonString(Compact) + Environment.NewLine;
        lock (Gate)
        {
            try { File.AppendAllText(_payloads, line); } catch (IOException) { }
        }
    }

    private static void Prune()
    {
        try
        {
            var cutoff = DateTime.Now.AddDays(-14);
            foreach (var f in Directory.EnumerateFiles(Dir))
                if (File.GetLastWriteTime(f) < cutoff) File.Delete(f);
        }
        catch (IOException) { }
    }
}

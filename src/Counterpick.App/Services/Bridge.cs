using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Web.WebView2.Wpf;

namespace Counterpick.App.Services;

/// <summary>
/// The single seam between the web UI and C#.
///
/// The UI sends {"id","method","payload"} and gets back {"id","ok","result"} or
/// {"id","ok":false,"error"}. C# can also push unsolicited {"event","payload"} messages,
/// which is how live champ select updates will arrive once the LCU listener lands.
///
/// Keeping every call through one switch means the TypeScript side has exactly one
/// typed client to maintain (see Counterpick.Web/src/bridge.ts).
/// </summary>
public sealed class Bridge
{
    private readonly WebView2 _web;
    private readonly Storage _storage;
    private readonly AppConfig _config;
    private readonly BackupService _backups;

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public Bridge(WebView2 web, Storage storage, AppConfig config, BackupService backups)
    {
        _web = web;
        _storage = storage;
        _config = config;
        _backups = backups;
        _web.WebMessageReceived += OnMessage;
    }

    /// <summary>Push an unsolicited event to the UI. Safe to call from any thread.</summary>
    public void Emit(string name, object? payload = null)
    {
        var envelope = new JsonObject
        {
            ["event"] = name,
            ["payload"] = payload is null ? null : JsonSerializer.SerializeToNode(payload, Json)
        };
        _web.Dispatcher.Invoke(() => _web.CoreWebView2?.PostWebMessageAsJson(envelope.ToJsonString()));
    }

    private void OnMessage(object? sender, Microsoft.Web.WebView2.Core.CoreWebView2WebMessageReceivedEventArgs e)
    {
        JsonNode? request = null;
        try
        {
            request = JsonNode.Parse(e.WebMessageAsJson);
            var id = request?["id"]?.GetValue<string>();
            var method = request?["method"]?.GetValue<string>();
            if (id is null || method is null) return;

            try
            {
                Reply(id, true, Dispatch(method, request?["payload"]));
            }
            catch (Exception ex)
            {
                // A failing call must not take the UI down with it. The web side turns
                // this into a visible message rather than a silent dead control.
                Reply(id, false, null, $"{ex.GetType().Name}: {ex.Message}");
            }
        }
        catch (JsonException)
        {
            // Not our envelope. Ignore rather than crash on stray page messages.
        }
    }

    private object? Dispatch(string method, JsonNode? p) => method switch
    {
        "config.get" => new
        {
            hasApiKey = !string.IsNullOrWhiteSpace(_config.ApiKey),
            model = _config.Model,
            primaryRole = _config.PrimaryRole,
            dataDragonVersion = _config.DataDragonVersion
        },

        "config.setApiKey" => Run(() =>
        {
            _config.ApiKey = Str(p, "apiKey");
            _config.Save();
        }),

        "config.setRole" => Run(() =>
        {
            _config.PrimaryRole = Str(p, "role") ?? "Top";
            _config.Save();
        }),

        "pool.get" => _storage.GetPool(Str(p, "role"))
                              .Select(x => new { championKey = x.ChampionKey, role = x.Role }),

        "pool.set" => Run(() => _storage.SetPool(
            Str(p, "role") ?? _config.PrimaryRole,
            p?["championKeys"]?.AsArray().Select(n => n!.GetValue<string>()) ?? [])),

        "notes.get" => _storage.GetNotes(Req(p, "championKey"), Req(p, "opponentKey"), Req(p, "role"))
                               .Select(n => new { id = n.Id, body = n.Body, createdAt = n.CreatedAt }),

        "notes.add" => _storage.AddNote(Req(p, "championKey"), Req(p, "opponentKey"),
                                        Req(p, "role"), Req(p, "body")),

        "game.record" => Run(() => _storage.RecordGame(
            Req(p, "championKey"), Req(p, "opponentKey"), Req(p, "role"),
            p?["won"]?.GetValue<bool>() ?? false)),

        "record.get" => Wins(_storage.GetRecord(Req(p, "championKey"), Req(p, "opponentKey"), Req(p, "role"))),

        // ── data safety ──────────────────────────────────────────────────
        "backup.status" => _backups.Status(),

        "backup.now" => _backups.Snapshot("manual"),

        "backup.list" => _backups.List(),

        "backup.restore" => Counts(_backups.Restore(Req(p, "path"))),

        "data.export" => ExportPaths(DataTransfer.ExportAll(_storage)),

        "data.import" => Counts(DataTransfer.ImportJson(_storage, Req(p, "path"))),

        // Not built yet - the UI runs on mock data until these land.
        "draft.subscribe" or "brief.request" =>
            throw new NotImplementedException($"'{method}' arrives with the LCU and Claude clients."),

        _ => throw new ArgumentException($"Unknown method '{method}'.")
    };

    private static object Wins(MatchupRecord r) => new { wins = r.Wins, losses = r.Losses };

    private static object Counts(DataCounts c) => new { notes = c.Notes, games = c.Games, pool = c.Pool };

    private static object ExportPaths((string Json, string Markdown) paths) =>
        new { json = paths.Json, markdown = paths.Markdown };

    private static object? Run(Action a) { a(); return null; }

    private static string? Str(JsonNode? p, string key) => p?[key]?.GetValue<string>();

    private static string Req(JsonNode? p, string key) =>
        Str(p, key) ?? throw new ArgumentException($"Missing required field '{key}'.");

    private void Reply(string id, bool ok, object? result, string? error = null)
    {
        var envelope = new JsonObject
        {
            ["id"] = id,
            ["ok"] = ok,
            ["result"] = result is null ? null : JsonSerializer.SerializeToNode(result, Json),
            ["error"] = error
        };
        _web.CoreWebView2?.PostWebMessageAsJson(envelope.ToJsonString());
    }
}

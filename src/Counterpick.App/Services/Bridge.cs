using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using Counterpick.App.Services.Lcu;
using Microsoft.Web.WebView2.Wpf;

namespace Counterpick.App.Services;

/// <summary>
/// The single seam between the web UI and C#.
///
/// The UI sends {"id","method","payload"} and gets back {"id","ok","result"} or
/// {"id","ok":false,"error"}. C# also pushes unsolicited {"event","payload"} messages -
/// that is how live champ select updates arrive from <see cref="LcuWatcher"/>.
///
/// Keeping every call through one switch means the TypeScript side has exactly one
/// typed client to maintain (see Counterpick.Web/src/bridge.ts). Methods that talk to
/// the network return a Task and are awaited on the UI thread, so a slow Claude call
/// never blocks a fast storage call.
/// </summary>
public sealed class Bridge
{
    private readonly WebView2 _web;
    private readonly Storage _storage;
    private readonly AppConfig _config;
    private readonly BackupService _backups;
    private readonly BriefCache _briefs;
    private readonly ChampionCatalog _catalog;
    private readonly LcuWatcher _watcher;
    private readonly ClaudeClient _claude;
    private readonly RoleRates _rates;
    private readonly UpdateService _updates;

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DictionaryKeyPolicy = null
    };

    public Bridge(WebView2 web, Storage storage, AppConfig config, BackupService backups,
                  BriefCache briefs, ChampionCatalog catalog, LcuWatcher watcher, ClaudeClient claude,
                  RoleRates rates, UpdateService updates)
    {
        _web = web;
        _storage = storage;
        _config = config;
        _backups = backups;
        _briefs = briefs;
        _catalog = catalog;
        _watcher = watcher;
        _claude = claude;
        _rates = rates;
        _updates = updates;
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
        var text = envelope.ToJsonString();
        _web.Dispatcher.BeginInvoke(() => _web.CoreWebView2?.PostWebMessageAsJson(text));
    }

    private async void OnMessage(object? sender, Microsoft.Web.WebView2.Core.CoreWebView2WebMessageReceivedEventArgs e)
    {
        string? id = null;
        try
        {
            var request = JsonNode.Parse(e.WebMessageAsJson);
            id = request?["id"]?.GetValue<string>();
            var method = request?["method"]?.GetValue<string>();
            if (id is null || method is null) return;

            var result = Dispatch(method, request?["payload"]);
            if (result is Task<object?> pending) result = await pending;
            Reply(id, true, result);
        }
        catch (JsonException)
        {
            // Not our envelope. Ignore rather than crash on stray page messages.
        }
        catch (Exception ex) when (id is not null)
        {
            // A failing call must not take the UI down with it. The web side turns
            // this into a visible message rather than a silent dead control.
            Trace.Write("bridge", $"call failed: {Describe(ex)}");
            Reply(id, false, null, Describe(ex));
        }
    }

    private object? Dispatch(string method, JsonNode? p) => method switch
    {
        // ── settings ────────────────────────────────────────────────────
        "config.get" => ConfigView(),

        "config.setApiKey" => Run(() =>
        {
            var key = Str(p, "apiKey")?.Trim();
            _config.ApiKey = string.IsNullOrEmpty(key) ? null : key;
            _config.Save();
        }),

        "config.set" => Run(() =>
        {
            if (Str(p, "primaryRole") is { } role) _config.PrimaryRole = role;
            if (Str(p, "model") is { } model) _config.Model = model;
            if (Str(p, "shortlistModel") is { } fast) _config.ShortlistModel = fast;
            if (p?["workspaceId"] is not null)
            {
                var ws = Str(p, "workspaceId")?.Trim();
                _config.WorkspaceId = string.IsNullOrEmpty(ws) ? null : ws;
            }
            _config.Save();
        }),

        "config.setRole" => Run(() =>
        {
            _config.PrimaryRole = Str(p, "role") ?? "Top";
            _config.Save();
        }),

        // ── champions and pool ──────────────────────────────────────────
        "champions.list" => ChampionsAsync(),

        "pool.get" => _storage.GetPool(Str(p, "role"))
                              .Select(x => new { championKey = x.ChampionKey, role = x.Role }),

        "pool.set" => Run(() => _storage.SetPool(
            Str(p, "role") ?? _config.PrimaryRole,
            p?["championKeys"]?.AsArray().Select(n => n!.GetValue<string>()) ?? [])),

        // ── live draft ──────────────────────────────────────────────────
        "draft.subscribe" => new { status = _watcher.Status, draft = _watcher.CurrentDraft },

        "draft.lockIn" => _watcher.LockInAsync(Req(p, "championKey")),

        // ── enemy roles ─────────────────────────────────────────────────
        "roles.infer" => InferRolesAsync(p),

        // ── Claude ──────────────────────────────────────────────────────
        "recs.request" => RecommendAsync(p),

        "brief.request" => BriefAsync(p),

        "brief.clearCache" => Run(_briefs.Clear),

        // ── notes and results ───────────────────────────────────────────
        "notes.get" => _storage.GetNotes(Req(p, "championKey"), Req(p, "opponentKey"), Req(p, "role"))
                               .Select(n => new { id = n.Id, body = n.Body, createdAt = n.CreatedAt }),

        "notes.add" => _storage.AddNote(Req(p, "championKey"), Req(p, "opponentKey"),
                                        Req(p, "role"), Req(p, "body")),

        "notes.all" => _storage.AllNotes().Select(n => new
        {
            id = n.Id,
            championKey = n.ChampionKey,
            opponentKey = n.OpponentKey,
            role = n.Role,
            body = n.Body,
            createdAt = n.CreatedAt,
            updatedAt = n.UpdatedAt,
            edited = n.UpdatedAt - n.CreatedAt > TimeSpan.FromSeconds(1)
        }),

        "notes.update" => _storage.UpdateNote(Num(p, "id"), Req(p, "body"))
            ? null
            : throw new InvalidOperationException(
                "That edit would duplicate another note in the same matchup."),

        "notes.delete" => _storage.DeleteNote(Num(p, "id"))
            ? null
            : throw new InvalidOperationException("That note no longer exists."),

        "game.record" => Run(() => _storage.RecordGame(
            Req(p, "championKey"), Req(p, "opponentKey"), Req(p, "role"),
            p?["won"]?.GetValue<bool>() ?? false)),

        "record.get" => Wins(_storage.GetRecord(Req(p, "championKey"), Req(p, "opponentKey"), Req(p, "role"))),

        // ── data safety ─────────────────────────────────────────────────
        "backup.status" => _backups.Status(),

        "backup.now" => _backups.Snapshot("manual"),

        "backup.list" => _backups.List(),

        "backup.restore" => Counts(_backups.Restore(Req(p, "path"))),

        "data.export" => ExportPaths(DataTransfer.ExportAll(_storage)),

        "data.import" => Counts(DataTransfer.ImportJson(_storage, Req(p, "path"))),

        // The web layer cannot see real file paths, so the picker lives here.
        "data.importDialog" => PickAndImport(),

        "shell.reveal" => Run(() => Reveal(Req(p, "path"))),

        // ── updates ─────────────────────────────────────────────────────
        "update.status" => _updates.View(),

        "update.check" => _updates.CheckAsync(),

        // ── window ──────────────────────────────────────────────────────
        // The window is frameless, so the page draws the caption buttons and these three
        // are what they do. Dragging and edge-resizing are not here: WindowChrome and the
        // page's app-region give those straight to Windows.
        "window.minimize" => Run(() => Host().WindowState = WindowState.Minimized),

        "window.maximize" => Run(() =>
        {
            var w = Host();
            w.WindowState = w.WindowState == WindowState.Maximized
                ? WindowState.Normal
                : WindowState.Maximized;
        }),

        "window.close" => Run(() => Host().Close()),

        // How tall the page's own title bar is. WindowChrome needs the number to know
        // which strip of the window is caption; the page is the only thing that knows it.
        "window.setCaptionHeight" => Run(() =>
        {
            if (Host() is MainWindow w) w.SetCaptionHeight(Num(p, "height"));
        }),

        _ => throw new ArgumentException($"Unknown method '{method}'.")
    };

    // ── settings ─────────────────────────────────────────────────────────

    private object ConfigView() => new
    {
        hasApiKey = _claude.HasApiKey,
        workspaceId = _config.WorkspaceId ?? "",
        model = _config.Model,
        shortlistModel = _config.ShortlistModel,
        primaryRole = _config.PrimaryRole,
        dataDragonVersion = _catalog.Version ?? _config.DataDragonVersion,
        roleRatesPatch = _rates.Patch,
        roleRatesSource = _rates.Source
    };

    // ── champions ────────────────────────────────────────────────────────

    private async Task<object?> ChampionsAsync()
    {
        await _catalog.EnsureLoadedAsync();
        if (_catalog.Version is { } v && v != _config.DataDragonVersion)
        {
            _config.DataDragonVersion = v;
            _config.Save();
        }
        return new
        {
            version = _catalog.Version,
            champions = _catalog.Champions.Select(c => new
            {
                key = c.Key, name = c.Name, numericId = c.NumericId, tags = c.Tags
            })
        };
    }

    // ── enemy roles ──────────────────────────────────────────────────────

    /// <summary>
    /// Payload: { enemies: [{ championKey, role? }] } - the locked enemy champions, with a
    /// role on the ones the client, the player or the game already fixed. Returns the
    /// most likely role for each and how sure that is. Local and instant: play rates plus
    /// one-of-each-role, no model call.
    /// </summary>
    private async Task<object?> InferRolesAsync(JsonNode? p)
    {
        await _catalog.EnsureLoadedAsync();
        await _rates.EnsureLoadedAsync();

        var picks = new List<RolePick>();
        foreach (var n in p?["enemies"]?.AsArray() ?? [])
        {
            var key = Req(n, "championKey");
            var info = _catalog.ByKey(key);
            var weights = RoleInference.WeightsFor(info is null ? null : _rates.RatesFor(info.NumericId), info?.Tags ?? [],
                                                   info?.Ranged ?? true);
            picks.Add(new RolePick(info?.Key ?? key, weights, Str(n, "role")));
        }

        var guesses = RoleInference.Infer(picks);
        Trace.Write("roles", $"inferred [{string.Join(" ", guesses.Select(g => $"{g.ChampionKey}:{g.Role}@{g.Confidence:0.00}"))}] " +
                             $"from {_rates.Source} rates, patch {_rates.Patch ?? "?"}");
        return new
        {
            roles = guesses.ToDictionary(g => g.ChampionKey, g => g.Role),
            confidence = guesses.ToDictionary(g => g.ChampionKey, g => g.Confidence)
        };
    }

    // ── Claude ───────────────────────────────────────────────────────────

    /// <summary>
    /// Payload: { role, championKeys[], draft }. Names, classes, notes and records are
    /// filled in here rather than trusted from the page - the page only knows keys.
    ///
    /// Answers two questions in one call: how the player's pool ranks, and what the best
    /// pick in the role would be if the pool were no object (see <see cref="OpenPool"/>).
    /// </summary>
    private async Task<object?> RecommendAsync(JsonNode? p)
    {
        await _catalog.EnsureLoadedAsync();
        var role = Str(p, "role") ?? _config.PrimaryRole;
        var draft = ParseDraft(p?["draft"], role);
        var keys = p?["championKeys"]?.AsArray().Select(n => n!.GetValue<string>()).ToList() ?? [];

        var pool = keys.Select(k => CandidateFor(k, draft.LaneOpponent?.ChampionKey, role)).ToList();
        var open = await OpenFieldFor(draft, role, keys);
        Trace.Write("claude", $"shortlist requested: {role} vs {draft.LaneOpponent?.ChampionKey ?? "?"}, pool [{string.Join(",", keys)}], " +
                              $"enemy [{string.Join(",", draft.EnemyPicks.Select(p => p.ChampionKey))}], ally [{string.Join(",", draft.AllyPicks.Select(p => p.ChampionKey))}], " +
                              $"open field {open.Count}");
        var started = System.Diagnostics.Stopwatch.StartNew();
        var result = await _claude.ShortlistAsync(draft, pool, open);
        Trace.Write("claude", $"shortlist ready in {started.ElapsedMilliseconds}ms: " +
                              $"{string.Join(" ", result.Recommendations.Select(r => $"{r.ChampionKey}={r.Score}"))} | " +
                              $"open {string.Join(" ", result.OpenPicks.Select(r => $"{r.ChampionKey}={r.Score}"))}");

        return new
        {
            recommendations = result.Recommendations,
            openPicks = result.OpenPicks,
            records = pool.ToDictionary(c => c.ChampionKey, c => new { wins = c.Wins, losses = c.Losses })
        };
    }

    /// <summary>
    /// Every champion played in the role this patch that is still available, for the
    /// pool-blind half of the shortlist. An unreachable play-rate feed means no open
    /// field rather than a guessed one; the pool shortlist is unaffected either way.
    /// </summary>
    private async Task<List<OpenCandidate>> OpenFieldFor(DraftContext draft, string role, IReadOnlyList<string> pool)
    {
        await _rates.EnsureLoadedAsync();
        if (!_rates.IsLoaded) return [];

        var taken = draft.Bans
            .Concat(draft.EnemyPicks.Select(x => x.ChampionKey))
            .Concat(draft.AllyPicks.Select(x => x.ChampionKey));
        return OpenPool.For(_catalog, _rates, role, taken, pool);
    }

    /// <summary>
    /// Payload: { championKey, opponentKey, role, draft, force? }. Cached per matchup and
    /// per notes fingerprint: writing a note invalidates the brief that predates it.
    /// </summary>
    private async Task<object?> BriefAsync(JsonNode? p)
    {
        await _catalog.EnsureLoadedAsync();
        var championKey = Req(p, "championKey");
        var opponentKey = Req(p, "opponentKey");
        var role = Str(p, "role") ?? _config.PrimaryRole;
        var force = p?["force"]?.GetValue<bool>() ?? false;

        var fingerprint = BriefCache.FingerprintFor(_storage, championKey, opponentKey, role);
        if (!force && _briefs.Get(championKey, opponentKey, role, fingerprint) is { } cached)
        {
            Trace.Write("claude", $"brief {championKey} vs {opponentKey} ({role}) served from cache");
            return new { brief = JsonNode.Parse(cached), cached = true };
        }

        var draft = ParseDraft(p?["draft"], role);
        var me = CandidateFor(championKey, opponentKey, role);
        var opponent = new PickInfo(opponentKey, NameOf(opponentKey), role);

        Trace.Write("claude", $"brief requested: {championKey} vs {opponentKey} ({role}), {me.Notes.Count} note(s), force={force}");
        var started = System.Diagnostics.Stopwatch.StartNew();
        var brief = await _claude.BriefAsync(draft, me, opponent);
        Trace.Write("claude", $"brief ready in {started.ElapsedMilliseconds}ms");
        var body = JsonSerializer.Serialize(brief, Json);
        _briefs.Put(championKey, opponentKey, role, fingerprint, body);
        return new { brief = JsonNode.Parse(body), cached = false };
    }

    private Candidate CandidateFor(string championKey, string? opponentKey, string role)
    {
        var info = _catalog.ByKey(championKey);
        var notes = opponentKey is null
            ? []
            : _storage.GetNotes(championKey, opponentKey, role).Select(n => n.Body).ToList();
        var record = opponentKey is null ? new MatchupRecord(0, 0) : _storage.GetRecord(championKey, opponentKey, role);
        return new Candidate(championKey, info?.Name ?? championKey, info?.Tags ?? [], notes, record.Wins, record.Losses);
    }

    private DraftContext ParseDraft(JsonNode? d, string role)
    {
        PickInfo Pick(JsonNode? n, string? roleOverride = null) =>
            new(Req(n, "championKey"), NameOf(Req(n, "championKey")), roleOverride ?? Str(n, "role") ?? "?",
                Guessed: n?["guessed"]?.GetValue<bool>() ?? false);
        List<PickInfo> Picks(string field) => d?[field]?.AsArray().Select(n => Pick(n)).ToList() ?? [];

        var lane = d?["laneOpponent"];
        return new DraftContext(
            Role: role,
            LaneOpponent: lane is null ? null : Pick(lane, role),
            EnemyPicks: Picks("enemyPicks"),
            AllyPicks: Picks("allyPicks"),
            EnemyPicksRemaining: d?["enemyPicksRemaining"]?.GetValue<int>() ?? 0,
            Bans: d?["bans"]?.AsArray().Select(n => n!.GetValue<string>()).ToList() ?? []);
    }

    private string NameOf(string key) => _catalog.ByKey(key)?.Name ?? key;

    // ── helpers ──────────────────────────────────────────────────────────

    private static object Wins(MatchupRecord r) => new { wins = r.Wins, losses = r.Losses };

    private static object Counts(DataCounts c) => new { notes = c.Notes, games = c.Games, pool = c.Pool };

    /// <summary>
    /// Native open dialog, then import. Returns null when the user cancels, which the UI
    /// treats as "nothing happened" rather than an error.
    /// </summary>
    private object? PickAndImport()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Import Counterpick notes",
            Filter = "Counterpick export (*.json)|*.json|All files (*.*)|*.*",
            InitialDirectory = Directory.Exists(AppPaths.ExportsDir) ? AppPaths.ExportsDir : null,
            CheckFileExists = true
        };

        if (dialog.ShowDialog() != true) return null;
        return Counts(DataTransfer.ImportJson(_storage, dialog.FileName));
    }

    /// <summary>Open a folder in Explorer, selecting the file when given one.</summary>
    private static void Reveal(string path)
    {
        if (Directory.Exists(path))
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        else if (File.Exists(path))
            Process.Start("explorer.exe", $"/select,\"{path}\"");
        else
            throw new FileNotFoundException("Nothing to open there.", path);
    }

    private static object ExportPaths((string Json, string Markdown) paths) =>
        new { json = paths.Json, markdown = paths.Markdown };

    private static object? Run(Action a) { a(); return null; }

    /// <summary>
    /// The window the web view lives in. Dispatch already runs on the UI thread, so this
    /// is safe to touch directly.
    /// </summary>
    private Window Host() =>
        Window.GetWindow(_web) ?? throw new InvalidOperationException("The window is gone.");

    private static string? Str(JsonNode? p, string key) => p?[key]?.GetValue<string>();

    private static string Req(JsonNode? p, string key) =>
        Str(p, key) ?? throw new ArgumentException($"Missing required field '{key}'.");

    private static long Num(JsonNode? p, string key) =>
        p?[key]?.GetValue<long>() ?? throw new ArgumentException($"Missing required field '{key}'.");

    /// <summary>The innermost message is the one worth showing; wrappers just add noise.</summary>
    private static string Describe(Exception ex)
    {
        while (ex is AggregateException { InnerException: { } inner }) ex = inner;
        return ex is InvalidOperationException or ArgumentException
            ? ex.Message
            : $"{ex.GetType().Name}: {ex.Message}";
    }

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

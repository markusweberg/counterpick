using System.IO;
using System.Net.Http;
using System.Text.Json.Nodes;

namespace Counterpick.App.Services;

/// <summary>
/// How often each champion is played in each role, as a share of games in that role.
///
/// The League client never says which enemy plays where, so the app works it out from
/// popularity. The numbers come from Meraki Analytics' <c>championrates.json</c>, which
/// is derived from Riot's own match data and refreshed every patch. It is fetched once a
/// day at most and cached under <see cref="AppPaths.CacheDir"/>; offline, the cached
/// copy is used, and with nothing cached yet the snapshot bundled into the exe stands in.
/// A stale table costs a little accuracy on the newest reworks, never a blank board.
///
/// The feed zeroes anything under roughly 0.35%, so a champion's off-roles read as
/// impossible here. <see cref="RoleInference"/> puts a floor under them.
/// </summary>
public sealed class RoleRates
{
    private const string FeedUrl = "https://cdn.merakianalytics.com/riot/lol/resources/latest/en-US/championrates.json";
    private static readonly TimeSpan Freshness = TimeSpan.FromHours(24);
    /// <summary>How long a fallback (stale cache, bundled snapshot) is used before the feed is tried again.</summary>
    private static readonly TimeSpan RetryAfter = TimeSpan.FromMinutes(10);
    private DateTime _nextRefresh = DateTime.MinValue;

    /// <summary>The feed's position names and the app's role names.</summary>
    private static readonly (string Feed, string Role)[] Positions =
    [
        ("TOP", "Top"), ("JUNGLE", "Jungle"), ("MIDDLE", "Mid"), ("BOTTOM", "Bot"), ("UTILITY", "Support")
    ];

    private readonly HttpClient _http;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Dictionary<int, IReadOnlyDictionary<string, double>> _byId = new();

    /// <summary>The patch the table describes, as the feed labels it ("16.3").</summary>
    public string? Patch { get; private set; }

    /// <summary>"feed", "cache" or "bundled": where the current table came from.</summary>
    public string Source { get; private set; } = "none";

    public bool IsLoaded => _byId.Count > 0;
    public int Count => _byId.Count;

    public RoleRates(HttpClient? http = null)
    {
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
    }

    private static string CacheFile => Path.Combine(AppPaths.CacheDir, "roles", "championrates.json");

    /// <summary>
    /// Rates for one champion by Riot numeric id, keyed by the app's role names. Null when
    /// the feed has never heard of the champion (a brand-new release) - the caller falls
    /// back to Data Dragon's classes. Roles the feed left out read as zero.
    /// </summary>
    public IReadOnlyDictionary<string, double>? RatesFor(int numericId) =>
        _byId.TryGetValue(numericId, out var r) && r.Values.Any(v => v > 0) ? r : null;

    /// <summary>
    /// Load the table: a cached copy under a day old is used as is; otherwise the feed is
    /// fetched and cached; failing that, the stale cache; failing that, the bundled
    /// snapshot. Never throws for network reasons - the bundled copy always exists.
    /// </summary>
    public async Task EnsureLoadedAsync(CancellationToken ct = default)
    {
        if (IsLoaded && DateTime.UtcNow < _nextRefresh) return;
        await _gate.WaitAsync(ct);
        try
        {
            if (IsLoaded && DateTime.UtcNow < _nextRefresh) return;

            if (File.Exists(CacheFile))
            {
                var age = DateTime.UtcNow - File.GetLastWriteTimeUtc(CacheFile);
                if (age < Freshness && TryApply(File.ReadAllText(CacheFile), "cache"))
                {
                    _nextRefresh = DateTime.UtcNow + (Freshness - age);
                    return;
                }
            }

            try
            {
                var json = await _http.GetStringAsync(FeedUrl, ct);
                if (TryApply(json, "feed"))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(CacheFile)!);
                    File.WriteAllText(CacheFile, json);
                    _nextRefresh = DateTime.UtcNow + Freshness;
                    return;
                }
            }
            catch (Exception) when (!ct.IsCancellationRequested)
            {
                // Offline or the feed moved. Fall through to whatever is on disk.
            }

            // A fallback is trusted for a while rather than hammering the network from
            // every inference; the next attempt after that goes back to the feed.
            _nextRefresh = DateTime.UtcNow + RetryAfter;
            if (IsLoaded) return;
            if (File.Exists(CacheFile) && TryApply(File.ReadAllText(CacheFile), "cache")) return;

            var bundled = Bundled();
            if (bundled is not null) TryApply(bundled, "bundled");
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Build a table from a championrates.json body. Used by tests.</summary>
    public static RoleRates FromJson(string json, string source = "test")
    {
        var r = new RoleRates();
        if (!r.TryApply(json, source)) throw new InvalidOperationException("championrates.json has no 'data' object.");
        return r;
    }

    /// <summary>The snapshot compiled into the exe, or null when this code runs without it (the tests).</summary>
    private static string? Bundled()
    {
        var assembly = typeof(RoleRates).Assembly;
        var name = assembly.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith("championrates.json", StringComparison.OrdinalIgnoreCase));
        if (name is null) return null;
        using var stream = assembly.GetManifestResourceStream(name);
        if (stream is null) return null;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private bool TryApply(string json, string source)
    {
        try
        {
            var root = JsonNode.Parse(json);
            var data = root?["data"]?.AsObject();
            if (data is null) return false;

            var table = new Dictionary<int, IReadOnlyDictionary<string, double>>();
            foreach (var (idText, node) in data)
            {
                if (!int.TryParse(idText, out var id) || node is null) continue;
                var rates = new Dictionary<string, double>();
                foreach (var (feed, role) in Positions)
                    rates[role] = node[feed]?["playRate"]?.GetValue<double>() ?? 0;
                table[id] = rates;
            }
            if (table.Count == 0) return false;

            _byId = table;
            Patch = root?["patch"]?.GetValue<string>();
            Source = source;
            return true;
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException or FormatException)
        {
            return false;
        }
    }
}

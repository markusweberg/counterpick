using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json.Nodes;

namespace Counterpick.App.Services;

/// <summary>One champion as Data Dragon describes it.</summary>
/// <param name="Key">Data Dragon id, e.g. "MonkeyKing". The key used everywhere in this app.</param>
/// <param name="NumericId">Riot's numeric champion id, which is what the League client speaks.</param>
/// <param name="Tags">Data Dragon classes, e.g. ["Fighter", "Tank"].</param>
/// <param name="Ranged">Basic attack range of 300 or more. Melee champions all but never play bot.</param>
public sealed record ChampionInfo(string Key, string Name, int NumericId, IReadOnlyList<string> Tags, bool Ranged = false);

/// <summary>
/// The champion list from Data Dragon, cached per patch under <see cref="AppPaths.CacheDir"/>.
///
/// The League client identifies champions by numeric id; the UI, the notes and the briefs
/// all use the Data Dragon key. This is the one place that translates between the two.
/// </summary>
public sealed class ChampionCatalog
{
    private const string Base = "https://ddragon.leagueoflegends.com";

    private readonly HttpClient _http;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Dictionary<int, ChampionInfo> _byId = new();
    private Dictionary<string, ChampionInfo> _byKey = new(StringComparer.OrdinalIgnoreCase);

    public string? Version { get; private set; }
    public IReadOnlyList<ChampionInfo> Champions { get; private set; } = [];
    public bool IsLoaded => Champions.Count > 0;

    public ChampionCatalog(HttpClient? http = null)
    {
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
    }

    private static string CacheRoot => Path.Combine(AppPaths.CacheDir, "ddragon");

    /// <summary>
    /// Load the newest patch, from disk when it is already cached and from the CDN
    /// otherwise. Offline, the newest cached patch is used; with nothing cached at all
    /// this throws and the caller retries later.
    /// </summary>
    public async Task EnsureLoadedAsync(CancellationToken ct = default)
    {
        if (IsLoaded) return;
        await _gate.WaitAsync(ct);
        try
        {
            if (IsLoaded) return;

            string? version = null;
            try
            {
                version = await LatestVersionAsync(ct);
            }
            catch (Exception) when (!ct.IsCancellationRequested)
            {
                // Offline. Fall through to the cache below.
            }

            if (version is not null)
            {
                var file = CacheFile(version);
                if (!File.Exists(file))
                {
                    var json = await _http.GetStringAsync($"{Base}/cdn/{version}/data/en_US/champion.json", ct);
                    Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                    File.WriteAllText(file, json);
                }
                Apply(version, File.ReadAllText(file));
                return;
            }

            var cached = NewestCachedVersion()
                ?? throw new InvalidOperationException(
                    "Champion list is not cached yet and Data Dragon is unreachable.");
            Apply(cached, File.ReadAllText(CacheFile(cached)));
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Build a catalog from a champion.json body. Used by tests.</summary>
    public static ChampionCatalog FromJson(string version, string championJson)
    {
        var c = new ChampionCatalog();
        c.Apply(version, championJson);
        return c;
    }

    public ChampionInfo? ById(int numericId) =>
        numericId > 0 && _byId.TryGetValue(numericId, out var c) ? c : null;

    public ChampionInfo? ByKey(string key) =>
        _byKey.TryGetValue(key, out var c) ? c : null;

    private async Task<string> LatestVersionAsync(CancellationToken ct)
    {
        var versions = await _http.GetFromJsonAsync<string[]>($"{Base}/api/versions.json", ct);
        return versions is { Length: > 0 }
            ? versions[0]
            : throw new InvalidOperationException("versions.json was empty.");
    }

    private static string CacheFile(string version) => Path.Combine(CacheRoot, version, "champion.json");

    private static string? NewestCachedVersion()
    {
        if (!Directory.Exists(CacheRoot)) return null;
        return Directory.GetDirectories(CacheRoot)
            .Select(Path.GetFileName)
            .Where(v => v is not null && File.Exists(CacheFile(v)))
            .OrderByDescending(v => v, VersionComparer.Instance)
            .FirstOrDefault();
    }

    private void Apply(string version, string championJson)
    {
        var root = JsonNode.Parse(championJson)?["data"]?.AsObject()
                   ?? throw new InvalidOperationException("champion.json has no 'data' object.");

        var list = new List<ChampionInfo>();
        foreach (var (_, node) in root)
        {
            if (node is null) continue;
            var key = node["id"]?.GetValue<string>();
            var name = node["name"]?.GetValue<string>();
            var numeric = node["key"]?.GetValue<string>();
            if (key is null || name is null || !int.TryParse(numeric, out var id)) continue;

            var tags = node["tags"]?.AsArray().Select(t => t!.GetValue<string>()).ToList() ?? [];
            var range = node["stats"]?["attackrange"]?.GetValue<double>() ?? 0;
            list.Add(new ChampionInfo(key, name, id, tags, Ranged: range >= 300));
        }

        list.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        Champions = list;
        _byId = list.ToDictionary(c => c.NumericId);
        _byKey = list.ToDictionary(c => c.Key, StringComparer.OrdinalIgnoreCase);
        Version = version;
    }

    /// <summary>Orders "14.10.1" after "14.9.1", which plain string ordering gets wrong.</summary>
    private sealed class VersionComparer : IComparer<string?>
    {
        public static readonly VersionComparer Instance = new();

        public int Compare(string? x, string? y)
        {
            var a = Parts(x);
            var b = Parts(y);
            for (var i = 0; i < Math.Max(a.Length, b.Length); i++)
            {
                var ai = i < a.Length ? a[i] : 0;
                var bi = i < b.Length ? b[i] : 0;
                if (ai != bi) return ai.CompareTo(bi);
            }
            return 0;
        }

        private static int[] Parts(string? v) =>
            (v ?? "").Split('.').Select(p => int.TryParse(p, out var n) ? n : 0).ToArray();
    }
}

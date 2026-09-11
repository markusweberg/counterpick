using System.Net.Http;
using System.Text.Json.Nodes;

namespace Counterpick.App.Services.Lcu;

/// <summary>
/// Who plays where, as the running game reports it. This is the truth the draft-time
/// guess is checked against.
/// </summary>
/// <param name="EnemyRoles">Enemy champion key to role, all five.</param>
/// <param name="AllyRoles">Your side, same shape.</param>
/// <param name="YourRole">Your own position.</param>
public sealed record GameRoles(IReadOnlyDictionary<string, string> EnemyRoles,
                               IReadOnlyDictionary<string, string> AllyRoles,
                               string? YourRole);

/// <summary>
/// Turns a <c>/liveclientdata/allgamedata</c> payload into <see cref="GameRoles"/>.
///
/// The game client (not the League client) serves this on port 2999 once a game is
/// loading. Unlike champ select, it carries a <c>position</c> for every player on both
/// teams, so it settles the enemy laner for certain. Pure and tested; the polling lives
/// in <see cref="LiveGameClient"/>.
/// </summary>
public static class LiveGameMapper
{
    private const string RawNamePrefix = "game_character_displayname_";

    /// <summary>
    /// Null when the payload cannot settle the roles yet: no player list, you cannot be
    /// found in it, or a team is missing positions (a mode without them, or the game is
    /// still spinning up). The caller keeps polling.
    /// </summary>
    public static GameRoles? Map(JsonNode? data, ChampionCatalog catalog)
    {
        var players = data?["allPlayers"]?.AsArray();
        if (players is null || players.Count == 0) return null;

        var active = data?["activePlayer"];
        var myRiotId = active?["riotId"]?.GetValue<string>();
        var myName = active?["summonerName"]?.GetValue<string>();

        string? myTeam = null;
        foreach (var p in players)
        {
            if (p is null) continue;
            var riotId = p["riotId"]?.GetValue<string>();
            var name = p["summonerName"]?.GetValue<string>();
            if ((myRiotId is not null && riotId == myRiotId) || (myName is not null && name == myName))
            {
                myTeam = p["team"]?.GetValue<string>();
                break;
            }
        }
        if (myTeam is null) return null;

        var enemy = new Dictionary<string, string>();
        var ally = new Dictionary<string, string>();
        string? yourRole = null;
        foreach (var p in players)
        {
            if (p is null) continue;
            var key = ChampionKey(p, catalog);
            var role = DraftMapper.MapPosition(p["position"]?.GetValue<string>());
            if (key is null || role is null) return null;

            var mine = p["team"]?.GetValue<string>() == myTeam;
            (mine ? ally : enemy)[key] = role;
            var riotId = p["riotId"]?.GetValue<string>();
            var name = p["summonerName"]?.GetValue<string>();
            if (mine && ((myRiotId is not null && riotId == myRiotId) || (myName is not null && name == myName)))
                yourRole = role;
        }

        if (enemy.Count == 0 || enemy.Values.Distinct().Count() != enemy.Count) return null;
        return new GameRoles(enemy, ally, yourRole);
    }

    /// <summary>
    /// <c>rawChampionName</c> is "game_character_displayname_MonkeyKing": the internal
    /// name, which is the Data Dragon key. <c>championName</c> is the display name and
    /// the fallback.
    /// </summary>
    private static string? ChampionKey(JsonNode player, ChampionCatalog catalog)
    {
        var raw = player["rawChampionName"]?.GetValue<string>();
        if (raw is not null && raw.StartsWith(RawNamePrefix, StringComparison.OrdinalIgnoreCase))
        {
            var key = raw[RawNamePrefix.Length..];
            if (catalog.ByKey(key) is { } info) return info.Key;
        }
        var name = player["championName"]?.GetValue<string>();
        return name is null
            ? null
            : catalog.Champions.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase))?.Key;
    }
}

/// <summary>
/// Reads the running game's player list from the Live Client Data API on 127.0.0.1:2999.
///
/// The endpoint exists only while a game process is up, answers with a self-signed
/// certificate, and needs no auth. It is polled from the loading screen until it
/// yields positions or the game is over.
/// </summary>
public sealed class LiveGameClient : IDisposable
{
    private const string AllGameData = "https://127.0.0.1:2999/liveclientdata/allgamedata";

    private readonly HttpClient _http;

    public LiveGameClient()
    {
        var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (_, _, _, _) => true
        };
        _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(3) };
    }

    /// <summary>Null while the game process is not answering. Throws for nothing routine.</summary>
    public async Task<JsonNode?> GetAllGameDataAsync(CancellationToken ct = default)
    {
        try
        {
            using var response = await _http.GetAsync(AllGameData, ct);
            if (!response.IsSuccessStatusCode) return null;
            var body = await response.Content.ReadAsStringAsync(ct);
            return body.Length == 0 ? null : JsonNode.Parse(body);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            if (ct.IsCancellationRequested) throw new OperationCanceledException(ct);
            return null;
        }
    }

    public void Dispose() => _http.Dispose();
}

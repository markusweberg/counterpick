using System.Text.Json.Nodes;

namespace Counterpick.App.Services.Lcu;

/// <summary>One seat on the draft board. Mirrors DraftSlot in the web's types.ts.</summary>
/// <param name="Hovering">The champion is shown but not locked. Hovers are noisy, so the
/// UI shows them on the board without re-scoring.</param>
/// <param name="RoleKnown">The role came from the client's assignedPosition. When false
/// it is seat order - a placeholder the UI must not treat as the truth. In a normal
/// draft the enemy side never has positions; only your own team does.</param>
public sealed record DraftSlot(string? ChampionKey, string Role, bool IsYou, bool OnTheClock, bool Hovering,
                               bool RoleKnown);

/// <summary>
/// The live draft as the UI consumes it. Everything the web side needs to render the
/// board, run the clock, and decide when to re-score.
/// </summary>
public sealed record DraftPayload(
    IReadOnlyList<DraftSlot> Ally,
    IReadOnlyList<DraftSlot> Enemy,
    /// <summary>Locked enemy champions mapped to the role the client assigned them.</summary>
    IReadOnlyDictionary<string, string> EnemyRoles,
    IReadOnlyList<string> Bans,
    long TimerMs,
    /// <summary>Practice tool runs with no clock at all.</summary>
    bool TimerInfinite,
    string TimerPhase,
    bool YourTurn,
    string? LockedKey,
    string? HoverKey,
    /// <summary>How many enemy picks are still to come. Drives "their last pick is still hidden".</summary>
    int EnemyPicksRemaining,
    /// <summary>
    /// The role the client assigned you, or null when it did not (blind pick, custom games).
    /// The UI scores this role for the draft instead of the one under Settings.
    /// </summary>
    string? YourRole,
    /// <summary>The client put you somewhere other than the positions you queued for.</summary>
    bool Autofilled);

/// <summary>
/// Turns a <c>/lol-champ-select/v1/session</c> payload into a <see cref="DraftPayload"/>.
///
/// Pure: no I/O, no client. The payload assumptions here were checked against captures
/// from a live client (tests/Counterpick.DataTests/fixtures) and are exercised by the tests.
/// </summary>
public static class DraftMapper
{
    private static readonly string[] RoleOrder = ["Top", "Jungle", "Mid", "Bot", "Support"];

    /// <summary>
    /// Null when the session is not a draft this app understands - ARAM's bench, or a
    /// team that is not five players. The caller should show "unsupported queue" rather
    /// than a half-empty board.
    /// </summary>
    public static DraftPayload? Map(JsonNode session, ChampionCatalog catalog)
    {
        if (session["benchEnabled"]?.GetValue<bool>() == true) return null;

        var myTeam = session["myTeam"]?.AsArray() ?? [];
        var theirTeam = session["theirTeam"]?.AsArray() ?? [];
        if (myTeam.Count != 5) return null;

        var localCell = session["localPlayerCellId"]?.GetValue<int>() ?? -1;

        // Actions are a list of rounds, each a list of pick/ban actions. In a custom or
        // practice game only your own pick appears; the enemy side has no actions at all.
        var inProgress = new HashSet<int>();
        var pending = new HashSet<int>();
        var lockedByCell = new Dictionary<int, int>();
        var banIds = new List<int>();
        foreach (var round in session["actions"]?.AsArray() ?? [])
        foreach (var action in round?.AsArray() ?? [])
        {
            if (action is null) continue;
            var type = action["type"]?.GetValue<string>();
            var cell = action["actorCellId"]?.GetValue<int>() ?? -1;
            var championId = action["championId"]?.GetValue<int>() ?? 0;
            var completed = action["completed"]?.GetValue<bool>() ?? false;
            var active = action["isInProgress"]?.GetValue<bool>() ?? false;

            if (type == "ban")
            {
                if (completed && championId != 0) banIds.Add(championId);
                continue;
            }
            if (type != "pick") continue;

            if (completed) lockedByCell[cell] = championId;
            else
            {
                pending.Add(cell);
                if (active) inProgress.Add(cell);
            }
        }

        // The session also carries the bans as a summary; take both, they agree.
        var bans = session["bans"];
        foreach (var side in new[] { "myTeamBans", "theirTeamBans" })
        foreach (var id in bans?[side]?.AsArray() ?? [])
            if (id?.GetValue<int>() is > 0 and var banned) banIds.Add(banned);

        var ally = MapTeam(myTeam, catalog, localCell, inProgress, lockedByCell);
        var enemy = MapTeam(theirTeam, catalog, localCell, inProgress, lockedByCell);

        // Only roles the client actually assigned. Seat-order placeholders stay off this
        // map so the UI knows the lane opponent is still to be worked out.
        var enemyRoles = new Dictionary<string, string>();
        foreach (var slot in enemy)
            if (slot.ChampionKey is not null && !slot.Hovering && slot.RoleKnown) enemyRoles[slot.ChampionKey] = slot.Role;

        var me = ally.FirstOrDefault(s => s.IsYou);
        var meMember = myTeam.FirstOrDefault(m => (m?["cellId"]?.GetValue<int>() ?? -1) == localCell);
        var timer = session["timer"];

        // Enemy picks remaining: count their pending actions when they have actions, and
        // fall back to their empty seats when they do not (custom games, bots).
        var enemyCells = theirTeam.Select(m => m?["cellId"]?.GetValue<int>() ?? -1).ToHashSet();
        var enemyHasActions = enemyCells.Any(c => pending.Contains(c) || lockedByCell.ContainsKey(c));
        var enemyRemaining = enemyHasActions
            ? pending.Count(enemyCells.Contains)
            : enemy.Count(s => s.ChampionKey is null || s.Hovering);

        return new DraftPayload(
            Ally: ally,
            Enemy: enemy,
            EnemyRoles: enemyRoles,
            Bans: banIds.Distinct().Select(catalog.ById).Where(c => c is not null).Select(c => c!.Key).ToList(),
            TimerMs: timer?["adjustedTimeLeftInPhase"]?.GetValue<long>() ?? 0,
            TimerInfinite: timer?["isInfinite"]?.GetValue<bool>() ?? false,
            TimerPhase: timer?["phase"]?.GetValue<string>() ?? "",
            YourTurn: inProgress.Contains(localCell),
            LockedKey: me is { Hovering: false } ? me.ChampionKey : null,
            HoverKey: me is { Hovering: true } ? me.ChampionKey : null,
            EnemyPicksRemaining: enemyRemaining,
            YourRole: me is { RoleKnown: true } ? me.Role : null,
            Autofilled: meMember?["isAutofilled"]?.GetValue<bool>() ?? false);
    }

    private static List<DraftSlot> MapTeam(JsonArray team, ChampionCatalog catalog, int localCell,
                                           HashSet<int> inProgress, Dictionary<int, int> lockedByCell)
    {
        // Roles in two passes: honour every explicit assignment first, then hand the
        // unassigned seats whatever is left in seat order. Otherwise an unassigned seat
        // early in the list (you, in a custom game) steals a role a later seat really has.
        var roles = new string?[team.Count];
        var known = new bool[team.Count];
        var used = new HashSet<string>();
        for (var i = 0; i < team.Count; i++)
        {
            var role = MapPosition(team[i]?["assignedPosition"]?.GetValue<string>());
            if (role is not null && used.Add(role))
            {
                roles[i] = role;
                known[i] = true;
            }
        }
        for (var i = 0; i < team.Count; i++)
        {
            if (roles[i] is not null) continue;
            roles[i] = RoleOrder.FirstOrDefault(r => !used.Contains(r)) ?? RoleOrder[i % 5];
            used.Add(roles[i]!);
        }

        var slots = new List<DraftSlot>();
        for (var i = 0; i < team.Count; i++)
        {
            var member = team[i];
            var cell = member?["cellId"]?.GetValue<int>() ?? -1;

            // championId is set once locked; before that championPickIntent carries the hover.
            var lockedId = member?["championId"]?.GetValue<int>() ?? 0;
            if (lockedId == 0 && lockedByCell.TryGetValue(cell, out var fromAction)) lockedId = fromAction;
            var hoverId = member?["championPickIntent"]?.GetValue<int>() ?? 0;

            var locked = catalog.ById(lockedId);
            var hover = locked is null ? catalog.ById(hoverId) : null;

            slots.Add(new DraftSlot(
                ChampionKey: locked?.Key ?? hover?.Key,
                Role: roles[i]!,
                IsYou: cell == localCell,
                OnTheClock: inProgress.Contains(cell),
                Hovering: locked is null && hover is not null,
                RoleKnown: known[i]));
        }

        return slots;
    }

    /// <summary>The client's position names to the app's role names.</summary>
    public static string? MapPosition(string? assignedPosition) => assignedPosition?.ToLowerInvariant() switch
    {
        "top" => "Top",
        "jungle" => "Jungle",
        "middle" => "Mid",
        "bottom" => "Bot",
        "utility" => "Support",
        _ => null
    };
}

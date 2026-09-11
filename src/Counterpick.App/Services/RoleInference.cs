namespace Counterpick.App.Services;

/// <summary>One enemy champion to place: its per-role weights and, if somebody already fixed it, its role.</summary>
public sealed record RolePick(string ChampionKey, IReadOnlyDictionary<string, double> Weights, string? FixedRole);

/// <summary>Where a champion most likely plays, and how sure that is.</summary>
/// <param name="Confidence">
/// The share of every consistent arrangement of the enemy side in which this champion
/// really is in <paramref name="Role"/>. 1 for a role somebody fixed.
/// </param>
public sealed record RoleGuess(string ChampionKey, string Role, double Confidence);

/// <summary>
/// Works out who on the enemy side plays where, from how often each champion is played
/// in each role and the constraint that a team has one of each.
///
/// Pure and instant: no model call, so the board has an answer the moment a pick lands.
/// Each enemy champion is treated as an independent draw from its play rates; an
/// arrangement of the locked champions into distinct roles is weighted by the product of
/// those rates; the arrangement with the highest weight is what the board shows, and the
/// share of total weight in which a champion sits in its shown role is the confidence.
/// With one champion locked that is just its own rates (Diana: jungle more often than
/// mid). With more, the others pin it down (Diana next to Lee Sin is mid). At most five
/// champions into five roles, so the whole space is 120 arrangements at the very worst.
/// </summary>
public static class RoleInference
{
    public static readonly string[] Roles = ["Top", "Jungle", "Mid", "Bot", "Support"];

    /// <summary>
    /// The weight a role gets when the feed reports none. Well under the feed's own
    /// reporting cutoff (about 0.35), so an unreported off-role is unlikely but never
    /// impossible: a Veigar next to four champions that cannot go bot still goes bot.
    /// Four floored roles together should stay a small fraction of a real rate, or a
    /// one-role champion like Kha'Zix reads as unsure of its own lane.
    /// </summary>
    public const double Floor = 0.05;

    /// <summary>
    /// Bot is a ranged lane: a melee champion's unreported bot rate gets a twentieth of
    /// the floor. This is what separates "Nunu jungle, Diana mid, Veigar bot" from "Diana
    /// jungle, Veigar mid, Nunu bot" when the feed reports neither bot rate - a ranged
    /// mage under the cutoff is still played bot now and then; a melee tank is not.
    /// </summary>
    public const double MeleeBotFactor = 0.05;

    /// <summary>Roles a Data Dragon class points at, for champions the feed does not know yet.</summary>
    private static readonly Dictionary<string, (string Role, double Weight)[]> TagPriors = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Marksman"] = [("Bot", 3.0), ("Mid", 0.8), ("Top", 0.4)],
        ["Support"] = [("Support", 3.0), ("Mid", 0.4)],
        ["Mage"] = [("Mid", 2.5), ("Support", 1.0), ("Bot", 0.6), ("Top", 0.4)],
        ["Assassin"] = [("Mid", 2.5), ("Jungle", 1.5), ("Top", 0.8)],
        ["Fighter"] = [("Top", 2.5), ("Jungle", 1.8), ("Mid", 0.6)],
        ["Tank"] = [("Top", 2.0), ("Support", 1.5), ("Jungle", 1.5)],
    };

    /// <summary>
    /// Per-role weights for a champion: its play rates when the feed has them, its
    /// classes otherwise, and an even spread when it has neither. Zeros become the
    /// floor, a smaller one for bot when the champion is melee.
    /// </summary>
    public static IReadOnlyDictionary<string, double> WeightsFor(IReadOnlyDictionary<string, double>? rates,
                                                                 IReadOnlyList<string> tags, bool ranged)
    {
        var weights = Roles.ToDictionary(r => r, _ => 0.0);
        if (rates is not null)
        {
            foreach (var role in Roles) weights[role] = rates.TryGetValue(role, out var v) ? v : 0;
        }
        else
        {
            foreach (var tag in tags)
                if (TagPriors.TryGetValue(tag, out var prior))
                    foreach (var (role, w) in prior) weights[role] += w;
        }
        if (weights.Values.All(v => v <= 0)) return Roles.ToDictionary(r => r, _ => 1.0);
        foreach (var role in Roles)
        {
            var floor = role == "Bot" && !ranged ? Floor * MeleeBotFactor : Floor;
            weights[role] = Math.Max(weights[role], floor);
        }
        return weights;
    }

    /// <summary>
    /// Place every pick. Fixed roles are honoured as given. Two picks fixed to the same
    /// role, or more picks than roles, is nonsense from the caller and gets a best-effort
    /// answer rather than an exception.
    /// </summary>
    public static IReadOnlyList<RoleGuess> Infer(IReadOnlyList<RolePick> picks)
    {
        if (picks.Count == 0) return [];
        var n = Math.Min(picks.Count, Roles.Length);
        var items = picks.Take(n).ToList();

        // Every injective assignment of picks to roles that respects the fixed ones,
        // accumulating the total weight, the best arrangement and per-pick marginals.
        var marginal = new double[n, Roles.Length];
        var total = 0.0;
        var best = -1.0;
        var bestRoles = new int[n];
        var current = new int[n];
        var used = new bool[Roles.Length];

        void Walk(int i, double weight)
        {
            if (i == n)
            {
                total += weight;
                for (var k = 0; k < n; k++) marginal[k, current[k]] += weight;
                if (weight > best)
                {
                    best = weight;
                    Array.Copy(current, bestRoles, n);
                }
                return;
            }
            var pick = items[i];
            for (var r = 0; r < Roles.Length; r++)
            {
                if (used[r]) continue;
                if (pick.FixedRole is not null && pick.FixedRole != Roles[r]) continue;
                used[r] = true;
                current[i] = r;
                var w = pick.Weights.TryGetValue(Roles[r], out var rate) ? Math.Max(rate, Floor * MeleeBotFactor) : Floor;
                Walk(i + 1, weight * w);
                used[r] = false;
            }
        }
        Walk(0, 1.0);

        if (total <= 0)
        {
            // Contradictory fixed roles. Drop the fixes and let the rates decide.
            return Infer(items.Select(p => p with { FixedRole = null }).ToList());
        }

        var result = new List<RoleGuess>(n);
        for (var k = 0; k < n; k++)
        {
            var role = Roles[bestRoles[k]];
            var confidence = items[k].FixedRole is not null ? 1.0 : marginal[k, bestRoles[k]] / total;
            result.Add(new RoleGuess(items[k].ChampionKey, role, Math.Round(confidence, 3)));
        }
        return result;
    }
}

namespace Counterpick.App.Services;

/// <summary>
/// One champion the player could take in this role, whether or not they play it.
/// </summary>
/// <param name="PlayRate">
/// Share of games in that role this patch, as the play-rate feed reports it (percent, so
/// 3.2 is a common pick and 0.4 is off-meta). It is what keeps the open list to champions
/// that are actually played in the role rather than every legal pick.
/// </param>
/// <param name="InPool">The player already plays this one, so it is on the shortlist too.</param>
public sealed record OpenCandidate(string ChampionKey, string Name, double PlayRate, bool InPool);

/// <summary>
/// The champions on the board for a role, ignoring the player's pool.
///
/// The shortlist answers "which of mine is best here". That is the question during a
/// pick timer, but it hides the other one: whether the draft wanted something the player
/// does not play at all. So the same call also ranks the open field, and the open field
/// is built here: everything the play-rate table sees in that role this patch, minus
/// what is banned or already taken, richest rate first.
///
/// Play rate is a filter, not a judgement - it keeps Soraka out of the top-lane list
/// without pretending the most-played champion is the best pick. Claude scores them.
/// </summary>
public static class OpenPool
{
    /// <summary>
    /// How many champions the model is offered. Every role has roughly fifty with a
    /// reported rate, so this is a ceiling that normally does not bite; it exists so a
    /// broken feed cannot put the whole roster in a prompt read under a pick timer.
    /// </summary>
    public const int Max = 60;

    /// <summary>
    /// Build the open field for one role. Empty when the play-rate table has not loaded,
    /// which the caller passes on as an empty list rather than guessing.
    /// </summary>
    /// <param name="unavailable">Banned or already picked, on either side.</param>
    /// <param name="pool">The player's own pool, used only to mark the overlap.</param>
    public static List<OpenCandidate> For(ChampionCatalog catalog, RoleRates rates, string role,
                                          IEnumerable<string> unavailable, IEnumerable<string> pool,
                                          int max = Max)
    {
        var gone = new HashSet<string>(unavailable, StringComparer.OrdinalIgnoreCase);
        var mine = new HashSet<string>(pool, StringComparer.OrdinalIgnoreCase);

        return catalog.Champions
            .Where(c => !gone.Contains(c.Key))
            .Select(c => (Champion: c, Rate: rates.RatesFor(c.NumericId)?.GetValueOrDefault(role) ?? 0))
            .Where(x => x.Rate > 0)
            .OrderByDescending(x => x.Rate)
            .ThenBy(x => x.Champion.Name, StringComparer.OrdinalIgnoreCase)
            .Take(Math.Max(0, max))
            .Select(x => new OpenCandidate(x.Champion.Key, x.Champion.Name, Math.Round(x.Rate, 2),
                                           mine.Contains(x.Champion.Key)))
            .ToList();
    }
}

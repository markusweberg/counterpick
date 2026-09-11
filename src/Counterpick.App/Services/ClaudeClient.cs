using System.Text.Json;
using Anthropic;
using Anthropic.Models.Messages;

namespace Counterpick.App.Services;

/// <summary>A champion already on the board, with the role it is believed to play.</summary>
/// <param name="Guessed">
/// The role is the app's inference from play rates and not yet settled. False when the
/// client, the player or the running game fixed it.
/// </param>
public sealed record PickInfo(string ChampionKey, string Name, string Role, bool Guessed = false);

/// <summary>The draft as the model should see it.</summary>
public sealed record DraftContext(
    string Role,
    PickInfo? LaneOpponent,
    IReadOnlyList<PickInfo> EnemyPicks,
    IReadOnlyList<PickInfo> AllyPicks,
    int EnemyPicksRemaining,
    IReadOnlyList<string> Bans);

/// <summary>One champion from your pool, with everything you have learned about this matchup.</summary>
public sealed record Candidate(
    string ChampionKey,
    string Name,
    IReadOnlyList<string> Tags,
    IReadOnlyList<string> Notes,
    int Wins,
    int Losses);

/// <summary>Mirrors Recommendation in types.ts, minus the brief, which is a separate call.</summary>
public sealed record ShortlistEntry(string ChampionKey, int Score, string Verdict, string Why, List<string> Hints);

/// <summary>The ranked pool. Enemy roles are the app's business (see RoleInference), not the model's.</summary>
public sealed record ShortlistResult(List<ShortlistEntry> Recommendations);

public sealed record LaneBeat(string Mark, string Text);
public sealed record SetupDto(string Keystone, string Secondary, string Summoners, string First);

/// <summary>Mirrors Brief in types.ts exactly, so the response drops straight into the view.</summary>
public sealed record BriefDto(
    string Headline,
    List<LaneBeat> Lane,
    List<string> YouKill,
    List<string> TheyKill,
    string Jungle,
    string Comp,
    SetupDto Setup,
    string SetupNote);

/// <summary>
/// The two calls to Claude, kept separate because they have different latency budgets.
///
/// The shortlist has to come back inside a pick timer, so it runs on the faster model at
/// low effort and says one line per champion. The brief is read on the loading screen
/// and can take its time on the stronger model.
///
/// What makes either call yours rather than a generic site is the notes: every saved
/// lesson for a matchup goes into the prompt, and the model is told to treat them as
/// the player's own words.
/// </summary>
public sealed class ClaudeClient
{
    private readonly AppConfig _config;

    private static readonly JsonSerializerOptions Wire = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = false
    };

    public ClaudeClient(AppConfig config)
    {
        _config = config;
    }

    public bool HasApiKey => !string.IsNullOrWhiteSpace(_config.ApiKey);

    private IAnthropicClient Client()
    {
        if (!HasApiKey) throw new InvalidOperationException("No API key set. Add one under Settings.");
        var headers = new Dictionary<string, string>();
        if (!string.IsNullOrWhiteSpace(_config.WorkspaceId))
            headers["anthropic-workspace-id"] = _config.WorkspaceId.Trim();
        var client = new AnthropicClient { ApiKey = _config.ApiKey };
        return headers.Count == 0
            ? client
            : client.WithOptions(o => { o.ExtraHeaders = headers; return o; });
    }

    /// <summary>Rank the whole pool against the current draft. Fast path.</summary>
    public async Task<ShortlistResult> ShortlistAsync(DraftContext draft, IReadOnlyList<Candidate> pool,
                                                      CancellationToken ct = default)
    {
        if (pool.Count == 0) throw new InvalidOperationException("Your pool is empty. Add champions under Settings.");

        var context = JsonSerializer.Serialize(new { draft, pool }, Wire);
        var text = await Complete(_config.ShortlistModel, Effort.Low, 4096, ShortlistSystem, ShortlistSchema,
            $"Rank my pool for this draft. Score and explain every champion in `pool`.\n\n```json\n{context}\n```", ct);

        var parsed = JsonSerializer.Deserialize<ShortlistResponse>(text, Wire)
                     ?? throw new InvalidOperationException("Claude returned an empty shortlist.");

        // Keep the contract honest even if the model drops or invents a champion.
        var allowed = pool.Select(p => p.ChampionKey).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var list = parsed.Recommendations
            .Where(r => allowed.Contains(r.ChampionKey))
            .Select(r => r with
            {
                Score = Math.Clamp(r.Score, 0, 100),
                Verdict = NormaliseVerdict(r.Verdict),
                Hints = r.Hints.Take(3).ToList()
            })
            .OrderByDescending(r => r.Score)
            .ToList();

        if (list.Count == 0) throw new InvalidOperationException("Claude's shortlist matched none of your pool.");

        return new ShortlistResult(list);
    }

    /// <summary>The full matchup brief for one champion into one opponent. Slow path.</summary>
    public async Task<BriefDto> BriefAsync(DraftContext draft, Candidate me, PickInfo opponent,
                                           CancellationToken ct = default)
    {
        var context = JsonSerializer.Serialize(new { draft, me, opponent }, Wire);
        var text = await Complete(_config.Model, Effort.Medium, 8192, BriefSystem, BriefSchema,
            $"Write the brief for {me.Name} into {opponent.Name} in {draft.Role}.\n\n```json\n{context}\n```", ct);

        return JsonSerializer.Deserialize<BriefDto>(text, Wire)
               ?? throw new InvalidOperationException("Claude returned an empty brief.");
    }

    private async Task<string> Complete(string model, Effort effort, long maxTokens, string system,
                                        string schemaJson, string user, CancellationToken ct)
    {
        var client = Client();
        var response = await client.Messages.Create(new MessageCreateParams
        {
            Model = model,
            MaxTokens = maxTokens,
            // Stable prompt first so it caches; the volatile draft goes in the user turn.
            System = new List<TextBlockParam>
            {
                new() { Text = system, CacheControl = new CacheControlEphemeral() }
            },
            Messages = [new() { Role = Role.User, Content = user }],
            OutputConfig = new OutputConfig
            {
                Effort = effort,
                Format = new JsonOutputFormat { Schema = SchemaDictionary(schemaJson) }
            }
        }, ct);

        Trace.Write("claude", $"{model} stop={response.StopReason} in={response.Usage.InputTokens} out={response.Usage.OutputTokens} " +
                              $"cacheRead={response.Usage.CacheReadInputTokens} cacheWrite={response.Usage.CacheCreationInputTokens}");

        if (response.StopReason?.ToString() == "refusal")
            throw new InvalidOperationException("Claude declined that request.");
        if (response.StopReason?.ToString() == "max_tokens")
            throw new InvalidOperationException("Claude's answer was cut off. Try again.");

        var text = string.Concat(response.Content.Select(b => b.Value).OfType<TextBlock>().Select(t => t.Text));
        if (string.IsNullOrWhiteSpace(text)) throw new InvalidOperationException("Claude returned no text.");
        return text;
    }

    private static Dictionary<string, JsonElement> SchemaDictionary(string schemaJson)
    {
        using var doc = JsonDocument.Parse(schemaJson);
        return doc.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone());
    }

    private static string NormaliseVerdict(string? v) => v?.Trim().ToLowerInvariant() switch
    {
        "favorable" or "favourable" => "Favorable",
        "even" => "Even",
        "difficult" => "Difficult",
        "losing" => "Losing",
        _ => "Even"
    };

    private sealed record ShortlistResponse(List<ShortlistEntry> Recommendations);

    // ── prompts ──────────────────────────────────────────────────────────

    private const string Persona = """
        You are a League of Legends draft coach working for one specific player on the
        current live patch of Summoner's Rift. You know every champion's kit, cooldowns,
        power spikes, and the standard runes and item paths. You speak plainly, name
        abilities by letter (Q, W, E, R), and never pad.

        The player has saved notes on matchups. Those notes are their own hard-won lessons
        and outrank your general knowledge: build on them, quote the lesson back when it
        is relevant, and never contradict one without saying why. A matchup with notes is
        a matchup this player has actually played; weight the win-loss record accordingly.

        Roles are Top, Jungle, Mid, Bot, Support. `laneOpponent` is the enemy champion in
        the player's role, if known. `enemyPicksRemaining` is how many enemy champions are
        still hidden - when it is not zero, say what their remaining pick could change.

        The League client does not reveal which enemy plays where, so the app places each
        enemy pick from how often that champion is played in each role. An enemy pick with
        `guessed` true is such a placement and may be wrong: treat it as the working
        assumption, and if the rest of their picks make another arrangement clearly more
        likely, say so in one clause. A pick with `guessed` false was fixed by the client,
        the player or the running game. A role of "?" is not placed yet.
        """;

    private const string ShortlistSystem = Persona + """

        Task: rank the player's champion pool for the current draft. This is read during
        the pick timer, so every field is short.

        Score 0-100 for how well the champion fits this exact draft. Judge the whole
        enemy comp: what they want to do, what they struggle against, and what this
        champion brings into that. The lane matchup is the single heaviest factor but not
        the majority of the score; a good lane into a comp this champion cannot fight
        later should score below an even lane into a comp it beats. Then weigh the enemy
        jungler, the fit with allied picks, and what the hidden enemy picks could still do.
        Verdict is one of Favorable, Even, Difficult, Losing and must agree with the score.
        `why` is one sentence the player reads in two seconds: lead with the comp reason,
        then the lane. `hints` are two or three fragments, each under ten words.

        Include every champion in `pool` exactly once, using its `championKey` verbatim.

        Score against `laneOpponent` when it is given. When it is null nobody on the enemy
        side has been placed in the player's role yet: score on the team fit and on what
        the hidden picks could be, and say so in `why`.
        """;

    private const string BriefSystem = Persona + """

        Task: write the matchup brief the player reads on the loading screen after
        locking in. It has to be concrete enough to act on in the first ten minutes.

        Fields:
        - headline: two sentences. The single most important thing about this game.
        - lane: four to six beats in game order. `mark` is a point in the game ("Lv 1-2",
          "Lv 6", "Bleed", "1st back"), `text` is one or two sentences. Wrap the one
          phrase to remember in <strong></strong>; no other HTML.
        - youKill / theyKill: two to four short conditions each, the windows in which
          each side wins a fight.
        - jungle: how the enemy jungler changes this lane - clear timings, where to ward,
          whether to fight or back off when they show. If their jungler is unknown, say
          what to assume. May use <strong>.
        - comp: the player's job in the allied team comp and what not to do. May use <strong>.
        - setup: keystone, secondary tree summary, summoner spells, first major item.
        - setupNote: one sentence on the setup choice that could reasonably go the other way.

        If the player has notes on this matchup, at least one beat or kill window must
        fold in what they wrote.
        """;

    // ── schemas ──────────────────────────────────────────────────────────

    private const string ShortlistSchema = """
        {
          "type": "object",
          "additionalProperties": false,
          "required": ["recommendations"],
          "properties": {
            "recommendations": {
              "type": "array",
              "items": {
                "type": "object",
                "additionalProperties": false,
                "required": ["championKey", "score", "verdict", "why", "hints"],
                "properties": {
                  "championKey": { "type": "string" },
                  "score": { "type": "integer" },
                  "verdict": { "type": "string", "enum": ["Favorable", "Even", "Difficult", "Losing"] },
                  "why": { "type": "string" },
                  "hints": { "type": "array", "items": { "type": "string" } }
                }
              }
            }
          }
        }
        """;

    private const string BriefSchema = """
        {
          "type": "object",
          "additionalProperties": false,
          "required": ["headline", "lane", "youKill", "theyKill", "jungle", "comp", "setup", "setupNote"],
          "properties": {
            "headline": { "type": "string" },
            "lane": {
              "type": "array",
              "items": {
                "type": "object",
                "additionalProperties": false,
                "required": ["mark", "text"],
                "properties": { "mark": { "type": "string" }, "text": { "type": "string" } }
              }
            },
            "youKill": { "type": "array", "items": { "type": "string" } },
            "theyKill": { "type": "array", "items": { "type": "string" } },
            "jungle": { "type": "string" },
            "comp": { "type": "string" },
            "setup": {
              "type": "object",
              "additionalProperties": false,
              "required": ["keystone", "secondary", "summoners", "first"],
              "properties": {
                "keystone": { "type": "string" },
                "secondary": { "type": "string" },
                "summoners": { "type": "string" },
                "first": { "type": "string" }
              }
            },
            "setupNote": { "type": "string" }
          }
        }
        """;
}

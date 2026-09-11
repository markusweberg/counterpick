# Remaining work

Written 2026-09-11, at the end of the session that built the League client listener,
the Claude client and the settings screen. This replaces the 2026-09-10 handover: the
three pieces it listed as unbuilt are now built. What follows is what is left, and the
detail needed to pick each piece up cold.

---

## Where things stand

| Piece | State |
|---|---|
| WPF + WebView2 shell | Done |
| TypeScript frontend, three phases | Done |
| Storage, backups, mirror, export/import | Done |
| Data panel | Done |
| Champion catalog from Data Dragon | Done, cached per patch |
| **Live draft from the League client** | Done, validated in a custom game and a real normal draft |
| **Recommendations and briefs from Claude** | **Built; no successful call yet** (the key needs a workspace id, now a setting) |
| Settings: API key, workspace id, role, models, pool editor | Done, pool editor reported broken in the app |
| **Following the role the client assigns you** | Done 2026-09-11, tested against the fixtures; not yet seen in a live autofill |
| Trace log under `%APPDATA%\Counterpick\logs` | Done: events, mapped drafts, Claude timings, raw payloads |
| Tests | 115 checks, including real captured payloads |

The worked example in `src/Counterpick.Web/src/data/scenario.ts` now only appears in a
plain browser (`npm run dev` outside the app). Inside the app everything starts empty and
fills from the client and from Claude.

**Validated against a live client on 2026-09-11**, in a custom game against bots:
discovery (command line and lockfile), the HTTPS calls, the gameflow phase in the
topbar, and the champ select payload at three moments - before hovering, hovering,
locked. Those captures are in `tests/Counterpick.DataTests/fixtures/` with identifiers
scrubbed, and the mapper is tested against them. What the captures showed:

- Every command-line argument is quoted as a whole (`"--install-directory=C:\Riot
  Games\League of Legends"`), so the quote sits before the dashes.
- A hover sets `championPickIntent` on the team member and `championId` on the
  in-progress action; a lock moves it to `championId` on the member, clears the
  intent, and completes the action. Exactly as assumed.
- In a custom game your own `assignedPosition` is empty while the bots have theirs,
  and the enemy side has no actions at all. The mapper assigns explicit roles first
  and fills gaps second, and falls back to empty seats for "picks remaining".
- `timer.isInfinite` exists (practice tool). The clock shows ∞.
- `bans` is also a separate object (`myTeamBans`, `theirTeamBans`); both sources are read.

**A real normal draft (queue 400) followed the same evening**, through the WebSocket
stream with the app open. Forty-odd session events, all mapped: enemy hovers, ten bans,
position swaps on the allied side, the clock resetting per action, your turn, the lock.
Two captures are in the fixtures. What it showed that the custom game could not:

- **The enemy side never has `assignedPosition`** in a normal draft; only your own team
  does. Seat order is pick order, not lanes. So enemy roles are now inferred by the
  shortlist call - Claude returns `enemyRoles` alongside the ranking - and shown on the
  board as a guess you can overrule. The mapper marks each slot `RoleKnown`, and
  `enemyRoles` in the payload only carries roles the client actually set.
- `bans` in the session summary object can be empty while the ban actions are all
  there; both are read.
- There is a `ten_bans_reveal` action type; ignored.
- An enemy can carry both `championId` and `championPickIntent` for the same champion
  at the moment of locking; a set `championId` wins.

**Not yet seen:** a successful Claude call. The key used in that draft was an
organisation-level key, which the API refuses without a workspace header; that is now a
Settings field. Every trace line for the draft itself was clean.

---

## Priorities after the first real draft  ← start here

Noted by Markus on 2026-09-11 after using the app in a real draft. In order.

### 1. Detect the role you were assigned, and use it - done

Built 2026-09-11, the session after it was noted. How it works now:

- `DraftPayload` carries `YourRole` (your seat's `assignedPosition`, null when the
  client set none) and `Autofilled` (`isAutofilled` on your `myTeam` entry).
- `applyLiveDraft` in `session.ts` follows `yourRole` for the length of a draft: it
  switches `state.role`, loads that role's pool and re-scores. It acts on a fresh draft
  and on any later change (position swaps), never on a repeat of what the client last
  said, so a role you set by hand mid-draft is left alone. When the client says nothing
  the Settings role is the default, and a fresh draft always re-reads it - a blind pick
  after an autofilled game does not inherit the autofill. A role change after you have
  locked re-requests the brief.
- An assigned role with no pool of its own borrows the Settings role's pool
  (`state.poolRole` says which pool is on the board). The draft view says so in the
  eyebrow and the subnote, and the hero banner carries an "Autofilled · Support" flag.
  Only when both pools are empty does the "build your pool" notice show.
- The Settings role dropdown now edits the default only, and says which role the client
  currently has you on.

Not yet seen live: an actual autofill. The `Autofilled: true` path is only exercised by
a mutated fixture in the tests. First real autofill, check that the hero flag appears,
the right pool loads, and the shortlist lands.

### 2. The app finds the enemy laner - never the player

The client does not say who on the enemy side plays where (confirmed: their
`assignedPosition` is always empty in a normal draft). The role dropdowns on the enemy
board exist as a correction, not as the way to tell the app who you are laning
against. The player should never have to touch them for the app to work.

The current build infers roles through the shortlist call (Claude returns
`enemyRoles`, applied to the board as guesses), but this has not yet run with a
working key, so treat it as unverified. Things to check and improve:

- The first enemy lock, alone, may be ambiguous (Diana can be jungle or mid). The
  inference should get more confident with each pick, and a guess must be revised
  when a later pick makes it wrong - which is why Claude-sourced roles are re-asked
  on every re-score rather than frozen.
- The cost of being wrong is a brief about the wrong matchup. If confidence is low,
  the draft view should say "probably X in top" rather than state it as fact, and
  the brief prefetch should wait until the lane is settled.
- If Claude's guessing turns out weak or slow, the fallback is a small local table of
  champion-to-role likelihoods (e.g. from Data Dragon tags plus a hand list of the
  common flex picks) used before the call, with Claude only breaking ties.

### 3. UI overhaul

The web UI was ported from the prototype and then extended feature by feature; it
needs a proper pass. Reported:

- **The champion pool selector in Settings is broken.** Reproduce it first: it was
  only ever checked in the browser preview, where the catalog is empty, not inside the
  app with 173 champions. Suspects: the search re-renders the whole screen on every
  keystroke (focus is restored by hand in `render()`), the add/remove buttons save
  through `pool.set` and reload, and the grid is capped at 12 results without a
  search term.
- **The app is ugly.** The prototype in `prototype/counterpick.html` is the approved
  look; the live app has drifted from it as screens were added (Settings and Data were
  never designed). Do a design pass on those two screens and on the empty states.
- **White bar at the top.** That is the default WPF title bar. Options: a dark title
  bar via the DWM immersive dark mode attribute on the window handle, or
  `WindowChrome` with a custom title bar drawn in the page's colours. The window
  background is already the page ground, so only the chrome is wrong.
- **No icon.** Add `ApplicationIcon` to `Counterpick.App.csproj` and `Icon` on the
  window in `MainWindow.xaml`; the taskbar and title bar pick it up from there.

## 4. Watch a draft with a working key

Launch Counterpick, make sure Settings shows "Key set" (and the workspace id if the key
is organisation-level), queue a normal draft, and watch the trace log in
`%APPDATA%\Counterpick\logs`. The things to confirm:

- The shortlist lands inside the pick timer. The trace prints its duration and tokens.
- The inferred enemy roles are sensible, and the lane opponent in the hero banner is
  the right champion. Correct one on the board and the next re-score keeps it.
- Scoring fires once per settled change, not per event: there is a 1.2 s debounce and
  a failed call is not retried until the inputs change or you press "Try again".
- Locking starts the brief; the brief view fills in during the loading screen.
- Autofill: the client's assigned role is followed. Check the hero flag, the pool that
  loads (the assigned role's, or the Settings role's as a stand-in) and that the lane
  opponent is read for the assigned role.

Where to look if something is wrong:

1. **Discovery.** `Services/Lcu/LcuLocator.cs` reads `--app-port` and
   `--remoting-auth-token` off `LeagueClientUx.exe`'s command line through WMI, then
   falls back to the lockfile in `--install-directory`, then to the lockfile beside the
   exe. If the topbar never leaves "not running" with the client open, WMI is probably
   being denied - the lockfile fallback needs the install directory, which only comes
   from the command line or from `Process.MainModule`, and the latter fails if League
   runs elevated and Counterpick does not.
2. **Connection.** `LcuClient.cs` accepts the self-signed certificate on its own handler
   only. The WebSocket uses the `wamp` subprotocol and subscribes with
   `[5, "OnJsonApiEvent"]`. If the phase shows but nothing updates, the subscription
   is the suspect.
3. **Mapping.** `DraftMapper.cs` is where payload assumptions live. Capture a real
   session with `GET /lol-champ-select/v1/session` (any REST client with Basic auth
   `riot:<password>`, certificate checks off) and compare it with the fixture in
   `tests/Counterpick.DataTests/Program.cs`. The assumptions most likely to need
   adjusting:
   - `championId` on a `myTeam` member becomes non-zero on lock; `championPickIntent`
     carries the hover. The mapper also falls back to the completed pick action for
     the cell, in case `championId` lags.
   - `theirTeam` members carry `championPickIntent` for hovers in some queues and not
     others. Hovers are shown but never scored on, so a missing hover costs nothing.
   - `assignedPosition` is empty in blind pick; the mapper falls back to seat order.
   - `timer.adjustedTimeLeftInPhase` is in milliseconds.
4. **Gameflow.** `LcuWatcher.cs` moves the UI to the after-game view on `EndOfGame`
   (and `PreEndOfGame`). If the client uses a different phase name for the post-game
   lobby, add it to `PHASE_LABEL` in `ui/chrome.ts` and to `applyClientStatus` in
   `session.ts`.

Anything learned here belongs in the test fixture, so the next change to the mapper
is checked against a real shape rather than a guessed one.

## 5. Validate the Claude calls with a real key

Add the key under Settings, add a pool, and either sit in a champ select or set an
enemy role by hand on the board. The draft view then calls `recs.request`; locking
calls `brief.request`.

What to look at:

- **Latency.** The shortlist runs on the shortlist model at low effort with a 4 K
  token cap. If it does not land comfortably inside the pick timer, trim the pool sent
  (`availablePool()` in `session.ts` already drops banned and taken champions) or shorten
  the system prompt in `ClaudeClient.cs`.
- **Quality.** Both prompts are in `ClaudeClient.cs` as constants. The output schema is
  enforced by structured outputs, so tune the prose in the prompt, not the parsing.
- **Cache hits.** `BriefCache` keys on `(champion, opponent, role)` plus the newest note
  id. The Settings screen has a "clear cached briefs" button for when the prompt changes.
- **Prompt caching.** The system prompt carries a cache breakpoint. It is shorter than
  the minimum cacheable prefix on some models, so it may never hit; that costs nothing.

Refusal fallbacks are not configured. For draft advice a refusal is not a realistic
outcome, and a refusal surfaces as a readable error in the UI rather than a blank.

---

## Smaller gaps

- **The brief is cached per matchup, not per team comp.** The "your job in this comp"
  section therefore describes the comp from whichever game first generated it. That is
  the design from the original notes; if it grates, add the allied picks to the cache
  key or split the comp paragraph into its own uncached call.
- **Ally roles are not editable.** Enemy roles have a dropdown; allied roles come from
  the client or seat order and cannot be corrected. Rarely matters because ally roles
  only feed the prompt as context.
- **Win/loss recording is fire-and-forget.** `game.record` failures are swallowed by
  `callOr`. Fine while it is best-effort.
- **No delete for a game result**, only for notes.
- **The Data panel does not show win/loss per matchup**, though the Markdown export does.
- **Ally roles are read but a swap on your own seat after locking re-keys the brief.**
  Deliberate: the brief is for the lane you will actually play. The shortlist you
  locked from was for the old role, which is unavoidable.
- **Hover does not re-score.** Deliberate: hovers are noisy and each score is an API
  call. Your own hover is shown in the hero and the board.

## Decisions to revisit

- **The API key sits in `%APPDATA%\Counterpick\config.json` in plain text.** Correct for
  a single user on their own machine. **If this is ever shared with anyone, that has to
  change** - a distributed build cannot hold a key safely, and it would need a small
  backend instead. Flagged, accepted, and deliberately deferred.
- **Models default to Opus 5 for briefs and Sonnet 5 for the shortlist.** Both are
  changeable under Settings. A config file written by the previous build keeps its
  `model` value, so an existing install stays on whatever it had.
- **Backups keep the last 30 snapshots** and mirror to OneDrive.
- **Note identity is `(champion, opponent, role, created_at, body)`.**

---

## Picking it back up

```
dotnet build                                    # builds C# and the frontend
dotnet run --project src/Counterpick.App        # launch
dotnet run --project tests/Counterpick.DataTests # 115 checks
```

For UI work with hot reload, run `npm run dev` in `src/Counterpick.Web` and start a Debug
build - it prefers `localhost:5173` when something is listening there. In a plain browser
the UI runs on the worked example; inside the app it never does.

To work against throwaway data, set `COUNTERPICK_DATA_DIR` to a temp folder before
launching. Set `OneDrive` too, or the mirror writes into the real one.

The approved visual design is `prototype/counterpick.html` - open it in a browser. It is
the reference for anything the live app should look like.

### Map of the new code

| File | Does |
|---|---|
| `Services/ChampionCatalog.cs` | Data Dragon champion list, numeric id ↔ key, cached per patch |
| `Services/Lcu/LcuEndpoint.cs` | Port and password parsing from the command line or lockfile |
| `Services/Lcu/LcuLocator.cs` | Finds the running client (WMI, then lockfile fallbacks) |
| `Services/Lcu/LcuClient.cs` | HTTPS and WebSocket to one client instance |
| `Services/Lcu/DraftMapper.cs` | Session payload → `DraftPayload`, pure and tested |
| `Services/Lcu/LcuWatcher.cs` | Poll, connect, reconnect with backoff, emit events |
| `Services/ClaudeClient.cs` | Shortlist and brief calls, prompts, JSON schemas |
| `Services/Bridge.cs` | Async dispatch; `draft.subscribe`, `recs.request`, `brief.request`, `champions.list`, `config.set` |
| `Web/src/session.ts` | Live draft handling, when to score, brief prefetch, phase transitions |
| `Web/src/champions.ts` | Champion lookup backed by the catalog |
| `Web/src/ui/settings.ts` | The settings screen |

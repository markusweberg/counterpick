# Remaining work

Written 2026-09-11, at the end of the session that built the League client listener,
the Claude client and the settings screen; updated later the same day by the session
that built enemy-role inference. What follows is what is left, and the detail needed
to pick each piece up cold.

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
| Settings: API key, workspace id, role, models, pool editor | Done; pool editor rebuilt 2026-09-11 (full roster, click to toggle) |
| **UI pass: Settings and Data as panels, dark title bar, app icon** | Done 2026-09-11 |
| **Following the role the client assigns you** | Done 2026-09-11, tested against the fixtures; not yet seen in a live autofill |
| **Enemy roles from play rates, confirmed by the running game** | Done 2026-09-11, tested against the captured draft; not yet seen live |
| Trace log under `%APPDATA%\Counterpick\logs` | Done: events, mapped drafts, Claude timings, raw payloads |
| Tests | 143 checks, including real captured payloads and the play-rate snapshot |

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
  does. Seat order is pick order, not lanes. Enemy roles are now placed locally from
  play rates (see priority 2 below) and shown on the board as a guess you can overrule.
  The mapper marks each slot `RoleKnown`, and `enemyRoles` in the payload only carries
  roles the client actually set.
- `bans` in the session summary object can be empty while the ban actions are all
  there; both are read.
- There is a `ten_bans_reveal` action type; ignored.
- An enemy can carry both `championId` and `championPickIntent` for the same champion
  at the moment of locking; a set `championId` wins.

**Not yet seen:** a successful Claude call. The key used in that draft was an
organisation-level key, which the API refuses without a workspace header; that is now a
Settings field. Every trace line for the draft itself was clean.

---

## Priorities after the first real draft

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

### 2. The app finds the enemy laner - never the player - done

The client does not say who on the enemy side plays where (confirmed: their
`assignedPosition` is always empty in a normal draft). The role dropdowns on the enemy
board exist as a correction, not as the way to tell the app who you are laning
against. The player should never have to touch them for the app to work.

Built 2026-09-11. Decided with Markus: popularity data, not a Claude call, so there is
no delay - and the running game confirms the roles before the brief is trusted. How it
works now:

- **The table.** `Services/RoleRates.cs` loads Meraki Analytics' `championrates.json`
  (per champion, the share of games in each position; derived from Riot match data,
  refreshed every patch). Fetched at most once a day, cached under
  `cache\roles\`, and a snapshot is embedded in the exe
  (`Resources/championrates.json`) so a first run offline still works. Refresh the
  snapshot now and then by overwriting it with the feed. The feed zeroes anything
  under about 0.35%, so unreported off-roles get a floor.

  **The feed looks stale.** On 2026-09-11 it reported patch 16.3 with a Last-Modified
  of 2026-02-11, while Data Dragon was on 16.18.1. Role distributions move slowly, so
  it still places a draft correctly (the captured one included), but reworks and meta
  shifts since February are not in it and champions released after it fall back to
  their Data Dragon classes. Watch the Settings screen, which shows both patch labels.
  If the feed stays dead, two ways out, both behind the one loader class: hand-edit
  the snapshot (it is plain JSON, one object per champion), or add a once-per-patch
  Claude call that produces the same shape - that keeps the zero-delay draft-time
  placement and confines the model to a background refresh.
- **The placement.** `Services/RoleInference.cs`, pure. Every arrangement of the
  locked enemy champions into distinct roles is weighted by the product of their play
  rates; the best arrangement goes on the board; the share of total weight in which a
  champion sits in its shown role is its confidence. One pick alone is just its own
  rates (Diana: jungle 0.75); the next picks pin it down (Diana next to Kha'Zix: mid
  0.84). A guess an early pick got wrong is revised by a later one because the whole
  side is re-placed on every pick. Roles the client, the player or the game fixed are
  honoured; the rest are placed around them. A melee champion gets a twentieth of the
  floor for bot - that is what separates "Nunu jungle, Diana mid, Veigar bot" from
  "Diana jungle, Veigar mid, Nunu bot" when the feed reports neither bot rate; the
  ranged flag comes from Data Dragon's attack range. Champions the feed does not know
  yet (a new release) fall back to their Data Dragon classes.
- **The bridge.** `roles.infer` takes the locked enemies with any fixed roles and
  returns roles plus confidence. `session.ts` calls it on every draft update and every
  dropdown change (`inferRoles`), then re-scores or briefs as the new roles call for.
  The trace prints `[roles] inferred [Diana:Mid@0.65 ...]` each time.
- **Saying "probably".** `LANE_SETTLED` in `state.ts` is 0.85. Below it the hero
  banner says "Probably your lane opponent", the subnote says "probably the Diana
  lane", the dropdown shows a dashed border and a `?` after the role, and the tooltip
  carries the percentage. Above it the board states the role as fact, with the
  percentage in the tooltip.
- **The brief waits.** After you lock, the brief is requested only if the lane is
  settled (fixed by you or the client, or confidence at or above `LANE_SETTLED`).
  Otherwise the brief view says so and offers "Write it now". The shortlist prefetch
  of the top two briefs also waits for a settled lane.
- **The game confirms.** When the gameflow phase reaches `GameStart` or `InProgress`,
  `LcuWatcher` polls the Live Client Data API (`https://127.0.0.1:2999/liveclientdata/
  allgamedata`, served by the game process, self-signed, no auth) every two seconds for
  up to five minutes. The player list carries a `position` for all ten players.
  `Lcu/LiveGame.cs` maps it (`rawChampionName` is the Data Dragon key, "Nunu & Willump"
  falls back to the display name); the watcher emits `game.roles`; `applyGameRoles` in
  `session.ts` marks those roles as source "game" (which beats everything, your own
  corrections included) and requests the brief for the confirmed lane. Positions are
  empty in modes without them; then nothing is emitted and the guess stands.
- Claude no longer guesses roles. The shortlist prompt and schema lost `enemyRoles`;
  each enemy pick now carries `guessed` so the model knows which placements are firm.

Not yet seen live, in order of what to check in the trace log:

1. `[roles] play rates ready: feed, patch 16.3` at startup (or `cache`; `bundled`
   means the feed was unreachable - the URL is in `RoleRates.cs`). Seen once on
   2026-09-11 with the client open: catalog, feed and websocket all came up within
   two seconds of launch.
2. `[roles] inferred [...]` after each enemy lock, with sensible roles and a confidence
   that rises as their side fills in.
3. `[game] positions confirmed: enemy[...]` shortly after the loading screen starts.
   **Unverified assumption:** that the Live Client Data API answers during the loading
   screen rather than only once the game clock runs. If it only answers in game, the
   confirmed brief lands a minute later than hoped; the mitigation is already in place
   (a settled guess is briefed on at lock, and the confirmation only re-requests when
   it disagrees). If the poll never confirms, the first thing to check is whether the
   `allPlayers` entries carry `riotId` or only `summonerName`; the mapper matches
   either against `activePlayer`. Save a real `allgamedata` capture into the fixtures
   in place of `allgamedata-synthetic.json`, which is hand-written from the docs.
4. The dropdown correction: change a role by hand and the rest are re-placed around
   it on the next `[roles] inferred` line, and the shortlist re-scores.

Tuning knobs, all constants: `RoleInference.Floor` (0.05), `MeleeBotFactor` (0.05),
the class priors in `RoleInference.TagPriors`, and `LANE_SETTLED` (0.85). The test
section "roles. placement" in `Program.cs` pins the captured draft's answer
(Nunu jungle, Diana mid 0.65, Yone top 0.89, Thresh support 0.97, Veigar bot 0.79).

### 3. UI overhaul - done

Done 2026-09-11. What was reported, and what was done about it:

- **The champion pool selector only showed part of the roster.** The grid was capped at
  12 champions when nothing was typed, so most of the 173 could only be reached by
  searching for them by name. Rebuilt: `poolGrid()` in `ui/settings.ts` shows the whole
  roster as tiles, alphabetical; a tile in the pool is lit gold with a check, and a click
  toggles it in or out (`pool-toggle` in `main.ts`). The chips above stay as the ordered
  list of the pool. The filter box only redraws the grid and its count (`refreshPool()`),
  so it never loses focus, and it ignores apostrophes ("kaisa" finds Kai'Sa). A save that
  fails puts the tile back and says why in the flash line. Verified inside the app with
  real key and mouse events through WebView2's remote-debugging port (see below).
- **Settings and Data redesigned as panels.** Both screens now open with the same page
  header (mono eyebrow, Cinzel title, subnote, back button) and lay their content out as
  `.panel` surfaces with a mono heading and a right-aligned meta count. Settings has four:
  API key, Draft (role and both models), Champion pool, Housekeeping (cached briefs and
  the data versions). Data keeps the stat cards and actions and puts Snapshots and Notes
  in panels. The two topbar links became a second segmented control matching the phase
  steps. Empty states and notices are gold by default; only a failure is orange
  (`notice(..., "warn")`).
- **Dark title bar.** `TitleBar.cs` sets the DWM attributes on the window handle from
  `OnSourceInitialized`: immersive dark mode, and on Windows 11 the caption colour
  (`--sunk`), caption text (`--gold`) and border (`--line-hard`). The standard chrome
  stays, only its paint changes; an older Windows ignores the calls.
- **Icon.** `Resources/counterpick.ico`, the UI's beveled octagon in gold around a serif
  C, in nine sizes from 16 to 256. Wired as `ApplicationIcon` in the csproj and `Icon` on
  the window. Regenerate it with a script if the mark changes; the source of truth is
  the description here, not a design file.

The browser preview (`npm run dev`) now loads the real champion list straight from Data
Dragon (`fetchCatalog()` in `ddragon.ts`), so the pool editor can be styled against all
173 champions rather than the worked example's ten. The pool itself is not saved there.

**Driving the real app from a script.** WebView2 honours
`WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS=--remote-debugging-port=9222` in the environment
of the exe. With that set, `http://127.0.0.1:9222/json` lists the page and its WebSocket
URL, and any DevTools-protocol client can evaluate JavaScript in it, dispatch real key and
mouse events (`Input.dispatchKeyEvent`, `Input.dispatchMouseEvent`) and take screenshots
(`Page.captureScreenshot`). Node 22 has a global `WebSocket`, so a 40-line script does it.
That is how the pool editor was reproduced and verified inside the app; use it whenever a
bug only shows up hosted. Combine with `COUNTERPICK_DATA_DIR` for throwaway data.

## 4. Watch a draft with a working key

Launch Counterpick, make sure Settings shows "Key set" (and the workspace id if the key
is organisation-level), queue a normal draft, and watch the trace log in
`%APPDATA%\Counterpick\logs`. The things to confirm:

- The shortlist lands inside the pick timer. The trace prints its duration and tokens.
- The placed enemy roles are sensible (`[roles] inferred` in the trace), and the lane
  opponent in the hero banner is the right champion. Correct one on the board and the
  rest are re-placed around it. Once the loading screen starts, `[game] positions
  confirmed` should follow and the banner should say "confirmed".
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
dotnet run --project tests/Counterpick.DataTests # 143 checks
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
| `Services/ChampionCatalog.cs` | Data Dragon champion list, numeric id ↔ key, classes, ranged flag, cached per patch |
| `Services/RoleRates.cs` | Per-position play rates (Meraki feed), cached daily, bundled snapshot as fallback |
| `Services/RoleInference.cs` | Places the enemy side into roles with a confidence; pure and tested |
| `Services/Lcu/LiveGame.cs` | Live Client Data API: positions for all ten players once the game loads |
| `Resources/championrates.json` | The bundled play-rate snapshot, refreshed by hand |
| `Services/Lcu/LcuEndpoint.cs` | Port and password parsing from the command line or lockfile |
| `Services/Lcu/LcuLocator.cs` | Finds the running client (WMI, then lockfile fallbacks) |
| `Services/Lcu/LcuClient.cs` | HTTPS and WebSocket to one client instance |
| `Services/Lcu/DraftMapper.cs` | Session payload → `DraftPayload`, pure and tested |
| `Services/Lcu/LcuWatcher.cs` | Poll, connect, reconnect with backoff, emit events |
| `Services/ClaudeClient.cs` | Shortlist and brief calls, prompts, JSON schemas |
| `Services/Bridge.cs` | Async dispatch; `draft.subscribe`, `roles.infer`, `recs.request`, `brief.request`, `champions.list`, `config.set` |
| `Web/src/session.ts` | Live draft handling, enemy-role placement, when to score, when the brief is safe to write, phase transitions |
| `Web/src/champions.ts` | Champion lookup backed by the catalog |
| `Web/src/ui/settings.ts` | The settings screen: panels and the roster-grid pool editor |
| `TitleBar.cs` | Paints the native title bar in the page's colours through DWM |
| `Resources/counterpick.ico` | The app icon, multi-size |

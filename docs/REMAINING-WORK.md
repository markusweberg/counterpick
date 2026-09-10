# Remaining work

Written 2026-09-10, at the end of the session that built the shell and the data layer.
This is the handover: what works, what is next, and the detail needed to pick each piece
up cold.

---

## Where things stand

**Working end to end.** The app builds with `dotnet build`, opens a WPF window hosting
WebView2, renders all three phases from the approved design, and manages its own storage.
Champion art loads from Data Dragon at runtime. The Data panel reads the real database.

| Piece | State |
|---|---|
| WPF + WebView2 shell | Done |
| TypeScript frontend, three phases | Done, running on a worked example |
| Storage, backups, mirror, export/import | Done, 58 tests |
| Data panel: notes, snapshots, export | Done |
| **Live draft from the League client** | **Not started** |
| **Recommendations and briefs from Claude** | **Not started** |
| Pool configuration UI | Not started |

The two missing pieces are both stubbed in `Services/Bridge.cs` and throw
`NotImplementedException` today: `draft.subscribe` and `brief.request`.

Everything the UI shows during draft comes from `src/Counterpick.Web/src/data/scenario.ts` —
blue side, top lane, pick B3, into Darius and Nidalee. That file disappears once the two
pieces below land.

---

## 1. The LCU listener  ← start here

The piece with real unknowns in it. **Have League running while working on this**, and
sit in a practice-tool champ select to get a live payload to look at.

The LCU (League Client Update) API is the local API the client talks to itself with. It
is not the public Riot API and needs no developer key. Blitz and Porofessor use it.

### Getting the connection details

The client writes a **lockfile** when it starts:

```
<install>\Riot Games\League of Legends\lockfile
```

Single line, colon-separated:

```
LeagueClient:12345:52001:AbCdEf123456:https
   name    : pid : port :  password  : protocol
```

The install path varies, so do not hardcode it. Find it from the running process command
line instead — `LeagueClientUx.exe` is launched with `--install-directory=...`. Query it
with `Process.GetProcessesByName("LeagueClientUx")` plus WMI
(`Win32_Process.CommandLine`), or read `--app-port` and `--remoting-auth-token` straight
off that command line and skip the lockfile entirely. The command-line route is more
reliable; the lockfile is the documented fallback.

### Talking to it

- Base URL `https://127.0.0.1:<port>`
- HTTP Basic auth, username literally `riot`, password from the lockfile
- **The certificate is self-signed.** `HttpClientHandler.ServerCertificateCustomValidationCallback`
  has to accept it. Scope that to this one `HttpClient`, never globally.

Endpoints that matter:

| Endpoint | Gives |
|---|---|
| `GET /lol-champ-select/v1/session` | The whole champ select state |
| `GET /lol-summoner/v1/current-summoner` | Who you are |
| `GET /lol-gameflow/v1/gameflow-phase` | `Lobby`, `ChampSelect`, `InProgress`, `EndOfGame` |

For live updates, open a WebSocket to `wss://127.0.0.1:<port>/` with the same Basic auth
and the `wamp` subprotocol. Subscribe by sending `[5, "OnJsonApiEvent"]`. Events arrive as
`[8, "OnJsonApiEvent", { uri, eventType, data }]` — filter on
`uri == "/lol-champ-select/v1/session"`.

### The session payload

Shapes to map onto `DraftState` in `src/Counterpick.Web/src/types.ts`:

- `localPlayerCellId` — which cell is you
- `myTeam[]` / `theirTeam[]` — each with `cellId`, `championId`, `championPickIntent`,
  `assignedPosition` (`"top"`, `"jungle"`, `"middle"`, `"bottom"`, `"utility"`, or empty)
- `actions[][]` — nested arrays of pick/ban actions, each with `actorCellId`,
  `championId`, `completed`, `type`, `isInProgress`
- `timer` — `adjustedTimeLeftInPhase` in milliseconds, for the countdown

Notes:

- **Champion ids are numeric.** Map them to Data Dragon keys with
  `/cdn/<version>/data/en_US/champion.json`, which has both. Cache it per patch under
  `AppPaths.CacheDir`. Numeric id 0 means "nothing picked".
- **Hover versus lock.** `championPickIntent` (and an action with `completed: false`) is a
  hover. Only `completed: true` is locked. Deciding whether to re-score on hovers is a
  real design question — hovers are noisy, and re-scoring on each one burns API calls.
  Suggest: show hovers on the board, but only re-score on locks.
- **`assignedPosition` is often empty**, notably in blind pick and in some queues. That is
  exactly why the manual role dropdowns exist — keep them, and treat the client's value
  as a default rather than the truth.
- `theirTeam` entries stay at `championId: 0` until the enemy locks.

### Gotchas

- The client may not be running. Poll for it; do not crash or block startup.
- The port and password **change every client restart**. Re-read on reconnect.
- The websocket drops when the client closes. Reconnect with backoff.
- Other queues (ARAM, Arena) have different session shapes. Guard on
  `gameflow-phase` and on queue id before trusting the payload.

### Wiring it up

Implement `draft.subscribe` in `Bridge.cs`. Push updates with the existing
`Bridge.Emit("draft.changed", payload)` — the TypeScript side already has `on(event, fn)`
in `bridge.ts` waiting for it. Set `state.live = true` so the "worked example" banner
disappears on its own.

---

## 2. The Claude client

Implement `brief.request`. The plumbing around it already exists: `BriefCache` stores
results keyed by `(champion, opponent, role)` plus a notes fingerprint, so writing a note
invalidates the brief that predates it, with no explicit cache busting.

### The prompt

Send: your pool for the role, the enemy picks with assigned roles, your team's picks,
which pick number you are, and **your saved notes for each candidate matchup**. The notes
are what make this yours rather than a generic site — they are the whole point.

Ask for structured JSON matching the `Recommendation` and `Brief` interfaces in
`types.ts`, so the response drops straight into the existing views. Those types are
already the contract; do not invent a second shape.

Model: `claude-sonnet-5` for pick-phase latency, `claude-opus-5` if a brief is worth the
extra seconds. The key is in `AppConfig.ApiKey`, and there is **no UI to set it yet** —
that is a prerequisite (see below).

### The latency problem

Pick phase gives roughly 27 seconds and an API call takes 3–8. Three mitigations, all
designed for already:

1. **Prefetch.** The moment the lane opponent is known, fire the briefs for the top two
   or three candidates in the background.
2. **Cache hard.** A pool of six champions hits the same matchups constantly.
3. **Pre-warm offline.** Generate the whole pool against the 40 most common opponents
   ahead of time; most of champ select then becomes a local lookup with no wait.

Split the two calls: the **ranked shortlist** must be fast and short (that is all you read
during pick phase), while the **full brief** can arrive later, during the loading screen.
They do not have to be one request.

---

## 3. Pool configuration UI

Required before the app is usable by anyone but the worked example — right now the pool
is hardcoded in `scenario.ts`.

Needs a champion picker over the Data Dragon list, per role, writing through the existing
`pool.set` and `pool.get` bridge methods (both already implemented and tested). A settings
screen alongside the Data panel is the natural home, and it should also hold:

- **The API key field.** `config.setApiKey` exists; nothing calls it.
- Primary role, via `config.setRole`.
- Model choice.

---

## Smaller gaps

- **The champ select timer is hardcoded to `0:27`** in `ui/chrome.ts`. Wire it to the real
  `timer.adjustedTimeLeftInPhase` with the LCU work.
- **Win/loss recording is fire-and-forget.** `game.record` is called from the After-game
  view but failures are swallowed by `callOr`. Fine while it is best-effort; revisit if
  the record starts mattering for scoring.
- **Records shown on recommendation cards come from `SEED_RECORDS`**, not the database.
  Wire to `record.get` when the pool becomes real.
- **The Data panel does not show win/loss per matchup**, though the Markdown export does.
  Cheap to add if useful.
- **No delete for a game result**, only for notes.
- **Only one role is supported end to end.** The data model handles all five; the UI
  assumes you are the one in `state.role`.

---

## Decisions to revisit

- **The API key sits in `%APPDATA%\Counterpick\config.json` in plain text.** Correct for a
  single user on their own machine. **If this is ever shared with anyone, that has to
  change** — a distributed build cannot hold a key safely, and it would need a small
  backend instead. Flagged, accepted, and deliberately deferred.
- **Backups keep the last 30 snapshots** and mirror to OneDrive. If notes grow into the
  thousands, revisit retention — though at a few hundred KB it will be a long while.
- **Note identity is `(champion, opponent, role, created_at, body)`.** That makes re-import
  idempotent while letting you relearn the same lesson on a later day. If bulk editing
  ever arrives, check that assumption still holds.

---

## Picking it back up

```
dotnet build                                    # builds C# and the frontend
dotnet run --project src/Counterpick.App        # launch
dotnet run --project tests/Counterpick.DataTests # 58 data-safety checks
```

For UI work with hot reload, run `npm run dev` in `src/Counterpick.Web` and start a Debug
build — it prefers `localhost:5173` when something is listening there.

To work against throwaway data, set `COUNTERPICK_DATA_DIR` to a temp folder before
launching. Set `OneDrive` too, or the mirror writes into the real one.

The approved visual design is `prototype/counterpick.html` — open it in a browser. It is
the reference for anything the live app should look like.

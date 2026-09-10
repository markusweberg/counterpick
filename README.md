# Counterpick

A personal League of Legends champ select companion. It reads the draft live from the
League client, ranks your champion pool against what the enemy has picked, and — once
you lock in — writes a matchup brief you read on the loading screen. After the game you
add a note, and that note feeds into every future brief for the same matchup.

Single-user, local-first. There is no server and no account.

## The three moments

| Phase | You need | The app shows |
|---|---|---|
| **Draft** | "Who do I take?" | Your pool re-scored live against the enemy picks, with one-line reasoning |
| **Locked in** | "What do I need to know?" | Lane beats, kill windows, jungle tracking, your job in the team comp |
| **After the game** | "What did I learn?" | A note that gets folded into the next brief for this matchup |

## Stack

A WPF window hosting a **WebView2** control. All the logic is C#; the UI is TypeScript
rendered by Edge, which ships with Windows 11 so there is no runtime to install.

Coming from C#: this builds like any other .NET project. `dotnet build` is the only
command you need — an MSBuild target runs the frontend's `npm run build` for you and
copies the result next to the exe.

| Familiar | Here |
|---|---|
| `dotnet restore` / `build` / `run` | identical |
| `dotnet publish -r win-x64 --self-contained` | identical, produces a real `.exe` |
| `.csproj`, NuGet, `bin/`, `obj/` | identical |
| — | plus `package.json` for the UI half, driven from the `.csproj` |

Skip the frontend step with `dotnet build -p:SkipFrontend=true`.

### Working on the UI

For hot reload while styling, run the Vite dev server and start the app in Debug:

```
cd src/Counterpick.Web && npm run dev
```

A Debug build prefers `localhost:5173` when something is listening there, and otherwise
serves the built copy from `wwwroot/`. Release builds always use `wwwroot/`.

## Repository layout

```
Counterpick.sln
src/
  Counterpick.App/        WPF host - the window, storage, and every external call
    MainWindow.xaml(.cs)    Hosts WebView2; picks dev server vs packaged UI
    Services/
      AppPaths.cs           Where everything is written (%APPDATA%\Counterpick)
      AppConfig.cs          API key, model, primary role
      Storage.cs            SQLite: your pool, notes and results
      BriefCache.cs         SQLite: Claude's briefs, disposable, separate file
      BackupService.cs      Snapshots, pruning, OneDrive mirror, restore
      DataTransfer.cs       JSON and Markdown export/import
      Bridge.cs             The single JS <-> C# seam
  Counterpick.Web/        Vite + TypeScript frontend
    src/
      main.ts               Entry: render loop and event delegation
      state.ts              App state and the draft/role rules
      bridge.ts             Typed client for Bridge.cs
      ddragon.ts            Champion art URLs
      types.ts              Domain model, mirrors the C# records
      ui/                   chrome.ts (topbar, hero, board), views.ts, atoms.ts
      data/scenario.ts      The worked example, until live data lands
      styles.css            Ported from the prototype
tests/
  Counterpick.DataTests/  Data-safety checks over the real service sources
prototype/                Design reference - the approved UI as a standalone page
tools/                    Build and asset scripts
```

## Your data

Everything the app writes lives in `%APPDATA%\Counterpick`, never in the repo. The two
databases are deliberately separate, because only one of them matters:

| File | Holds | Replaceable? |
|---|---|---|
| `counterpick.db` | **Your** pool, matchup notes, game results | **No. Backed up.** |
| `cache.db` | Claude's generated briefs | Yes, for pennies. Never backed up |
| `config.json` | API key, model, primary role | Retype it |
| `backups/` | Timestamped snapshots of `counterpick.db` | — |
| `exports/` | `notes.json` and `notes.md` | Regenerated on demand |
| `cache/`, `webview/` | Data Dragon art, WebView2 profile | Safe to delete |

Set `COUNTERPICK_DATA_DIR` to move that whole folder elsewhere (portable install, or
test isolation).

### Backups

Three layers, each covering a failure the others do not:

1. **Local snapshots** — taken at startup and shutdown, but only when the data actually
   changed, so an evening of League produces one snapshot rather than thirty. The last 30
   are kept. Snapshots taken before a schema migration or a restore are never pruned.
2. **OneDrive mirror** — every snapshot is copied to `%OneDrive%\Counterpick`, along with
   `notes.json` and `notes.md`. This is the layer that survives losing the disk, and
   OneDrive adds its own file version history on top. If OneDrive is offline or paused
   the local snapshot still succeeds and the next one retries.
3. **Export** — `notes.json` round-trips back in; `notes.md` is readable in Notepad with
   no software at all. This is the layer that survives abandoning the app.

Two properties worth knowing:

- **Snapshots use `VACUUM INTO`, not a file copy.** The database runs in WAL mode, so the
  newest commits can sit in a `-wal` sidecar. Copying `counterpick.db` by hand can
  silently miss them; if you ever back it up manually, take all three files.
- **Restore merges, it never overwrites.** Restoring an old snapshot cannot destroy notes
  written since — unique indexes on the natural identity of each row make re-importing
  the same rows a no-op. A fresh install with an empty database pulls the OneDrive mirror
  back automatically.

A note's identity is `(champion, opponent, role, created_at, body)`. Re-importing an
export therefore adds nothing, while writing the same lesson again three weeks later is
kept as a genuinely new note.

### Tests

```
dotnet run --project tests/Counterpick.DataTests
```

42 checks over the data-safety path: snapshots, WAL correctness, the OneDrive mirror,
export, import idempotency, restore-without-loss, fresh-machine recovery, and the
separation between your notes and the disposable cache. They run against an isolated
data directory via `COUNTERPICK_DATA_DIR`.

### Why the prototype ships art inline

The published prototype runs under a content-security policy that blocks external
images, so Data Dragon art had to be embedded. **The real app should not do this.** It
should fetch champion art from Data Dragon at runtime and cache it locally, so new
champions and art updates arrive without a rebuild.

## Data sources

- **Champion metadata and art** — [Data Dragon](https://developer.riotgames.com/docs/lol#data-dragon),
  Riot's static CDN. No API key. Version pinned per patch (`versions.json`).
- **Live draft state** — the LCU (League Client Update) API on `127.0.0.1`, authenticated
  from the client's `lockfile`. This is the local client API, not the public Riot API,
  and needs no developer key.
- **Recommendations and briefs** — the Claude API, prompted with your pool, the enemy
  comp, and your own saved notes.

## Decisions so far

- Knowledge comes from the Claude API seasoned with the user's own notes, not from a
  hand-authored matchup table or scraped community stats.
- Desktop app with live LCU auto-detect, rather than typing the enemy picks in.
- Briefs are cached per `(your champ, enemy champ, role)` so repeat matchups are instant
  and free; the pool can be pre-warmed against common opponents offline.
- The API key sits in local user config. Fine while this is single-user. **Revisit before
  sharing the app with anyone** — a shared build needs a backend to hold the key.

## Status

Working: the app builds, opens, renders all three phases, and creates its own storage.
The UI runs on the worked example in `data/scenario.ts` — blue side, top lane, pick B3,
into Darius and Nidalee.

Not built yet, and stubbed in `Bridge.cs`:

- **`draft.subscribe`** — the LCU listener. Read the client's `lockfile` for the port and
  password, open the champ-select websocket, map its payload onto `DraftState`.
- **`brief.request`** — the Claude client. Prompt with the pool, the enemy comp and the
  saved notes; cache the result; prefetch as soon as the lane opponent is known.

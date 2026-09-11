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
      AppConfig.cs          API key, models, primary role
      Storage.cs            SQLite: your pool, notes and results
      BriefCache.cs         SQLite: Claude's briefs, disposable, separate file
      BackupService.cs      Snapshots, pruning, OneDrive mirror, restore
      DataTransfer.cs       JSON and Markdown export/import
      ChampionCatalog.cs    Data Dragon champion list, numeric id <-> key
      ClaudeClient.cs       The shortlist and brief calls, prompts and schemas
      Bridge.cs             The single JS <-> C# seam
      Lcu/                  League client listener: locator, client, mapper, watcher
  Counterpick.Web/        Vite + TypeScript frontend
    src/
      main.ts               Entry: render loop and event delegation
      state.ts              App state and the draft/role rules
      session.ts            Live draft, scoring, briefs, phase transitions
      bridge.ts             Typed client for Bridge.cs
      champions.ts          Champion lookup backed by the catalog
      ddragon.ts            Champion art URLs
      types.ts              Domain model, mirrors the C# records
      ui/                   chrome.ts (topbar, hero, board), views.ts, settings.ts, data.ts, atoms.ts
      data/scenario.ts      The worked example, shown only in a plain browser
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

### The Data panel

Everything above is reachable from **Data** in the top right: current counts, when the
last backup ran, whether the OneDrive mirror is healthy, and buttons for Back up now,
Export, Import and Open backups folder. Below that, every snapshot with a Restore
button, and every note you have written, grouped by matchup, with inline edit and
delete.

Unlike the session views, nothing in this panel is mocked - it reads the real database,
so it reports honestly rather than inventing figures about the safety of your notes.

Deleting a note is safe: the previous snapshot still has it, and Restore brings it back.
Destructive buttons confirm in place rather than opening a dialog.

### Tests

```
dotnet run --project tests/Counterpick.DataTests
```

85 checks: the data-safety path (snapshots, WAL correctness, the OneDrive mirror,
export, import idempotency, restore-without-loss, fresh-machine recovery, note editing
and deletion, the separation between your notes and the disposable cache) plus the pure
half of the League client listener (endpoint parsing, the champion catalog, and the
session-to-draft mapper against a captured-shape payload). They run
against an isolated data directory via `COUNTERPICK_DATA_DIR`, and abort rather than
touch the real profile if that redirection ever fails.

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

Everything is built: the app opens, finds the League client when it is running, reads
the draft live, ranks your pool through Claude, writes the brief when you lock, and
manages its own storage. Settings holds the API key, your role, the models, and the
champion pool per role.

The League client listener has been validated in a custom game and a real normal draft;
captured payloads from both are test fixtures. The Claude calls are wired up but have
not yet succeeded against a real key. The next priorities, in order, are in
**[docs/REMAINING-WORK.md](docs/REMAINING-WORK.md)**: follow the role the client assigns
you, have the app work out the enemy laner on its own, and a UI overhaul (the pool
editor, the window chrome, the icon, and a design pass on the newer screens).

### How a game flows through it

1. **Client comes up.** The watcher polls for `LeagueClientUx.exe`, reads the port and
   password off its command line, connects, and follows the gameflow phase in the topbar.
2. **Champ select.** Every session update is mapped onto the board. Hovers are shown;
   only locks change the lane opponent. When your lane opponent locks, the pool is sent
   to Claude for a ranked shortlist, and the briefs for the top two come down in the
   background.
3. **You lock.** The brief view opens. Cached briefs are instant; a new one takes a few
   seconds and lands on the loading screen.
4. **Game ends.** The client's post-game phase moves the app to the after-game view.
   Win or loss, one line of notes, and the next brief for that matchup includes it.

If the client has the roles wrong, the dropdowns on the enemy board override it for the
rest of that draft.

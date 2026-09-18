# Counterpick

A personal League of Legends champ select companion. It reads the draft live from the
League client, works out who on the enemy side plays where, ranks your champion pool
against what they have picked, and — once you lock in — writes a matchup brief you read
on the loading screen. After the game you add a note, and that note feeds into every
future brief for the same matchup.

The client never says which enemy plays which role, so the app places them from how
often each champion is played in each position (a public play-rate table, refreshed
per patch), says "probably" until it is sure, and lets the running game confirm before
the brief is trusted.

The shortlist answers "which of mine is best here", which is the question on the clock.
Under it the same call answers the one that hides: the three strongest picks in the role
from every champion played there this patch, scored on the same scale, so you can see
whether the draft wanted something you do not play - and whether the gap is worth it.

Single-user, local-first. There is no server and no account.

## The three moments

| Phase | You need | The app shows |
|---|---|---|
| **Draft** | "Who do I take?" | Your pool re-scored live against the enemy picks, with one-line reasoning, and under it the best picks in the role from the whole roster |
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

## Installing it

Download **`Counterpick-win-Setup.exe`** from the
[latest release](https://github.com/markusweberg/counterpick/releases/latest) and run it.
That is the whole install: no runtime, no account, nothing to configure but your own API
key under Settings.

The app is packaged with [Velopack](https://velopack.io): a per-user install under
`%LOCALAPPDATA%\Counterpick`, shortcuts on the desktop and in the Start Menu, an entry in
Apps & features, and in-place updates. Nothing needs administrator rights.

Windows SmartScreen will warn that the publisher is unknown, because the build is not
code-signed — More info → Run anyway. A signing certificate is the only thing that
removes that, and it is not worth it for an app shared with a handful of people.

**Updates take care of themselves.** The installed app checks the Releases page on
startup, downloads a newer version in the background, and shows **Update ready** in the
top bar; click it, or use Settings → Restart to update, and it relaunches on the new
version. Settings also has Check for updates, the current version, and where releases are
read from. A build started from `bin/` or Visual Studio is not an install, so it cannot
update itself; Settings says so.

### Cutting a release

```
.\tools\release.ps1 -Publish
```

It bumps the patch number in the `.csproj` (or takes `-Version 1.2.0`), publishes a
self-contained build, packs it into `artifacts\releases`, commits and pushes the version
bump, and uploads the result to GitHub as a release tagged `v1.2.0`. Needs the GitHub CLI
logged in (`gh auth login`) or `GITHUB_TOKEN` set.

Leave off `-Publish` to pack without publishing. That build updates from
`artifacts\releases` rather than from GitHub, which is how you install and try a release
before anyone else sees it; `-Out` points it somewhere else, and `updateSource` in
`config.json` overrides the source on one machine.

Updating never touches your data - not `%APPDATA%\Counterpick`, not
`Documents\Counterpick` - and neither does uninstalling.

### Sharing it

Send a friend the [releases page](https://github.com/markusweberg/counterpick/releases/latest)
and they take it from there; the Setup is the only file they need. Each person uses
**their own Anthropic API key** and pays for their own calls - about 10 cents a game (the
first real draft cost 9).

- They create a key at [console.anthropic.com](https://console.anthropic.com), add credit,
  and **set a monthly spend limit** on it there. Then paste it under Settings.
- **Never give anyone your key**, and never bake one into a build. Anything inside a desktop
  app can be pulled out, and whoever has the key spends on its account without limit. A
  build carries no key today - it is only ever typed into Settings - keep it that way.
- The key is stored in `%APPDATA%\Counterpick\config.json` encrypted with Windows DPAPI
  (`AppConfig.cs`): only the same Windows account on the same machine can read it. A
  `config.json` copied to another PC or leaked through a backup carries an unreadable blob;
  Settings then asks for the key again. A plain-text key left by an older build is
  encrypted on the next launch.
- The trace log under `%APPDATA%\Counterpick\logs` never contains the key, so a friend can
  send you one for debugging. It does contain their draft payloads.

If you ever want to pay for everyone instead, the key must move behind a small backend
(a proxy that holds it, identifies each caller, and caps their usage). That is a different
app; don't do it by shipping the key.

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
    Program.cs              Entry point; runs the installer hook before WPF starts
    MainWindow.xaml(.cs)    Hosts WebView2; picks dev server vs packaged UI
    Services/
      AppPaths.cs           Where everything is written (%APPDATA%, notes in Documents)
      AppConfig.cs          API key, models, primary role
      Storage.cs            SQLite: your pool, notes and results
      BriefCache.cs         SQLite: Claude's briefs, disposable, separate file
      BackupService.cs      Snapshots, pruning, OneDrive mirror, restore
      DataTransfer.cs       JSON and Markdown export/import
      ChampionCatalog.cs    Data Dragon champion list, numeric id <-> key
      RoleRates.cs          Per-position play rates, cached daily, with a bundled snapshot
      RoleInference.cs      Places the enemy side into roles, with a confidence
      OpenPool.cs           The champions played in a role this patch, for the pool-blind picks
      ClaudeClient.cs       The shortlist and brief calls, prompts and schemas
      Bridge.cs             The single JS <-> C# seam
      UpdateService.cs      Watches the release folder, downloads, offers a restart
      Lcu/                  League client listener: locator, client, mapper, watcher,
                            and the running game's player list for confirmed positions
    Resources/              The bundled play-rate snapshot
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
tools/                    release.ps1 builds and packs an installer; asset scripts
```

## Your data

Your notes database lives in `Documents\Counterpick`, because Windows syncs Documents to
OneDrive - so the live file is off-machine from the moment you write a note, not just at
snapshot time. Everything else lives in `%APPDATA%\Counterpick`: a WebView2 profile and
an art cache have no business in your cloud storage. Nothing is ever written to the repo.

The two databases are deliberately separate, because only one of them matters:

| File | Holds | Replaceable? |
|---|---|---|
| `Documents\Counterpick\counterpick.db` | **Your** pool, matchup notes, game results | **No. Synced and backed up.** |
| `cache.db` | Claude's generated briefs | Yes, for pennies. Never backed up |
| `config.json` | API key (encrypted to your Windows account), model, primary role | Retype it |
| `backups/` | Timestamped snapshots of `counterpick.db` | — |
| `exports/` | `notes.json` and `notes.md` | Regenerated on demand |
| `cache/`, `webview/` | Data Dragon art, WebView2 profile | Safe to delete |

Upgrading from a version that kept the database in `%APPDATA%` moves it on first launch,
`-wal` sidecar included, and only deletes the old copy once the new one is in place. If
Documents cannot be written to, the old location keeps working rather than the app
opening an empty database beside your real one.

Set `COUNTERPICK_DATA_DIR` to put everything, notes database included, in one folder of
your choosing (portable install on a stick, or test isolation).

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

Syncing the live database is not a fourth layer. OneDrive faithfully copies a deletion
too, within seconds; only the snapshots hold a note you deleted yesterday.

Two properties worth knowing:

- **Snapshots use `VACUUM INTO`, not a file copy.** The database runs in WAL mode, so the
  newest commits can sit in a `-wal` sidecar. Copying `counterpick.db` by hand can
  silently miss them; if you ever back it up manually, take all three files. Closing the
  app checkpoints the sidecar away, so what OneDrive then carries is a whole database.
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

168 checks: the data-safety path (snapshots, WAL correctness, the OneDrive mirror,
export, import idempotency, restore-without-loss, fresh-machine recovery, note editing
and deletion, the separation between your notes and the disposable cache, the API key
never written in the clear) plus the pure
half of the League client listener (endpoint parsing, the champion catalog, and the
session-to-draft mapper against a captured-shape payload, and the open field a role
offers). They run
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
  comp, the champions played in your role this patch, and your own saved notes.

## Decisions so far

- Knowledge comes from the Claude API seasoned with the user's own notes, not from a
  hand-authored matchup table or scraped community stats.
- Desktop app with live LCU auto-detect, rather than typing the enemy picks in.
- Briefs are cached per `(your champ, enemy champ, role)` so repeat matchups are instant
  and free; the pool can be pre-warmed against common opponents offline.
- **Everyone brings their own API key.** There is no shared key and no backend; see
  [Sharing it](#sharing-it). The key is encrypted with Windows DPAPI in `config.json`.

## Status

Everything is built: the app opens, finds the League client when it is running, reads
the draft live, ranks your pool through Claude, writes the brief when you lock, and
manages its own storage. Settings holds the API key, your role, the models, and the
champion pool per role.

The League client listener has been validated in a custom game and a real normal draft;
captured payloads from both are test fixtures. The Claude calls are wired up but have
not yet succeeded against a real key. The next priorities, in order, are in
**[docs/REMAINING-WORK.md](docs/REMAINING-WORK.md)**: have the app work out the enemy
laner on its own, and a UI overhaul (the pool editor, the window chrome, the icon, and a
design pass on the newer screens).

### How a game flows through it

1. **Client comes up.** The watcher polls for `LeagueClientUx.exe`, reads the port and
   password off its command line, connects, and follows the gameflow phase in the topbar.
2. **Champ select.** The role the client assigned you is read off your seat, and the
   draft is scored for that role: its pool, its lane opponent. Autofill is announced in
   the hero banner, and an autofilled role with no pool of its own borrows the pool of
   the role under Settings. Every session update is mapped onto the board. Hovers are
   shown; only locks change the lane opponent. When your lane opponent locks, the pool
   is sent to Claude for a ranked shortlist - along with the champions played in your
   role this patch, so the same answer includes the best picks outside your pool - and
   the briefs for the top two come down in the background.
3. **You lock.** The brief view opens. Cached briefs are instant; a new one takes a few
   seconds and lands on the loading screen.
4. **Game ends.** The client's post-game phase moves the app to the after-game view.
   Win or loss, one line of notes, and the next brief for that matchup includes it.

If the client has the enemy roles wrong, the dropdowns on the enemy board override it
for the rest of that draft. The role under Settings is only the default, for queues
where the client assigns nothing: blind pick and custom games.

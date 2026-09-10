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
      Storage.cs            SQLite: pool, notes, results, cached briefs
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
prototype/                Design reference - the approved UI as a standalone page
tools/                    Build and asset scripts
```

## Storage

Everything the app writes lives in `%APPDATA%\Counterpick`, never in the repo:

| File | Holds |
|---|---|
| `config.json` | API key, model, primary role |
| `counterpick.db` | Champion pool, matchup notes, results, cached briefs |
| `cache/` | Data Dragon art and metadata, per patch. Safe to delete |
| `webview/` | WebView2 profile |

Cached briefs are keyed by `(champion, opponent, role)` plus the newest note id for that
matchup, so **writing a note automatically invalidates the brief that predates it**.

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

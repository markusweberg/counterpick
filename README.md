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

## Repository layout

```
prototype/       Design reference — the approved UI, as a standalone HTML page
  counterpick.html   Self-contained build (champion art inlined as data URIs)
  src.html           Source, with an /*__ART__*/ placeholder where art.js is spliced in
  art.js             Generated: champion art as base64 data URIs
tools/           Build and asset scripts
```

The application itself is not scaffolded yet — see *Decisions* below.

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

## Open

- Application stack and project scaffolding.
- LCU listener: lockfile auth, champ-select websocket, mapping its payload to the UI state.

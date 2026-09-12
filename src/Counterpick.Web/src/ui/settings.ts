/**
 * Settings: the API key, your role, which models to use, and the champion pool per role.
 *
 * Pool edits write through immediately - there is no Save button to forget. Everything
 * here reads from the host; in a plain browser the pool editor still works against the
 * Data Dragon catalog so the layout can be styled.
 */

import { isHosted } from "../bridge";
import { allChampions, champion } from "../champions";
import { state } from "../state";
import { ROLES } from "../types";
import { esc, portrait } from "./atoms";

const MODELS: [string, string][] = [
  ["claude-sonnet-5", "Claude Sonnet 5 · fast"],
  ["claude-opus-5", "Claude Opus 5 · strongest"],
  ["claude-haiku-4-5", "Claude Haiku 4.5 · cheapest"],
];

function modelSelect(setting: "shortlistModel" | "model", current: string): string {
  const options = [...MODELS];
  if (!options.some(([id]) => id === current)) options.unshift([current, current]);
  return `<select class="field" data-setting="${setting}">
    ${options.map(([id, label]) => `<option value="${esc(id)}" ${id === current ? "selected" : ""}>${esc(label)}</option>`).join("")}
  </select>`;
}

/* ── pool ────────────────────────────────────────────────────────────── */

function matches(): ReturnType<typeof allChampions> {
  const q = state.settings.search.trim().toLowerCase().replace(/[^a-z0-9& ]/g, "");
  if (!q) return allChampions();
  // Kai'Sa, Cho'Gath, Rek'Sai: the apostrophe is dropped from both sides so "kaisa" finds her.
  return allChampions().filter((c) =>
    c.name.toLowerCase().replace(/[^a-z0-9& ]/g, "").includes(q) || c.key.toLowerCase().includes(q));
}

/** The whole roster as tiles; a click toggles one in or out of the pool. */
export function poolGrid(): string {
  const all = allChampions();
  if (all.length === 0) {
    return `<p class="empty-note">Champion list not loaded yet. It comes from Data Dragon on first run.</p>`;
  }
  const inPool = new Set(state.settings.poolDraft);
  const list = matches();
  if (list.length === 0) return `<p class="empty-note">No champion matches that.</p>`;
  return `<div class="tiles">${list.map((c) => {
    const on = inPool.has(c.key);
    return `<button class="tile" data-act="pool-toggle" data-key="${c.key}" aria-pressed="${on}"
        title="${on ? "Remove" : "Add"} ${esc(c.name)}">
      ${portrait(c.key)}<span class="nm">${esc(c.name)}</span></button>`;
  }).join("")}</div>`;
}

/** "173 champions" or "2 of 173 match". Sits beside the search box. */
export function poolGridCount(): string {
  const all = allChampions().length;
  if (all === 0) return "";
  const n = matches().length;
  return state.settings.search.trim() ? `${n} of ${all} match` : `${all} champions`;
}

/** Your pool for the role being edited, in the order you added them. */
export function poolChips(): string {
  const s = state.settings;
  if (s.poolDraft.length === 0) {
    return `<p class="empty-note">Nothing in your ${s.poolRole.toLowerCase()} pool yet. Click champions below to add them.</p>`;
  }
  return `<div class="chips">${s.poolDraft.map((k) => `<span class="chip-champ">${portrait(k)}
      <span class="nm">${esc(champion(k).name)}</span>
      <button class="x" data-act="pool-toggle" data-key="${k}" title="Remove ${esc(champion(k).name)}" aria-label="Remove ${esc(champion(k).name)}">×</button>
    </span>`).join("")}</div>`;
}

function poolPanel(): string {
  const s = state.settings;
  const tabs = ROLES.map(
    (r) => `<button class="step" data-act="pool-role" data-role="${r}" aria-current="${s.poolRole === r}">${r}</button>`,
  ).join("");
  const count = s.poolDraft.length;

  return `<section class="panel span">
    <div class="panel-head">
      <h3>Champion pool</h3>
      <span class="panel-meta">${count === 0 ? "empty" : `${count} in ${s.poolRole.toLowerCase()}`}</span>
    </div>
    <p class="subnote">The champions you actually play in each role. Only these are scored during a draft;
      a role with an empty pool borrows your default role's.</p>
    <div class="steps pool-tabs">${tabs}</div>
    <div id="poolChips">${poolChips()}</div>
    <div class="pool-search">
      <input id="poolSearch" class="field" type="search" autocomplete="off" spellcheck="false"
        placeholder="Filter champions…" value="${esc(s.search)}" aria-label="Filter champions">
      <span id="poolCount" class="pool-count">${poolGridCount()}</span>
    </div>
    <div id="poolGrid">${poolGrid()}</div>
  </section>`;
}

/* ── updates ─────────────────────────────────────────────────────────── */

function updateLine(): string {
  const u = state.update;
  if (!u) return "Waiting for the host.";
  switch (u.state) {
    case "not-installed":
      return "This copy runs from a build folder, so it cannot swap itself. Install it with the Setup from tools/release.ps1 to get updates here.";
    case "checking":
      return "Looking for a newer version…";
    case "downloading":
      return `Downloading ${esc(u.available ?? "")} · ${u.progress}%`;
    case "ready":
      return `Version ${esc(u.available ?? "")} is downloaded. Restart to switch to it; your notes and settings stay where they are.`;
    case "up-to-date":
      return `Up to date${u.checkedAt ? `, checked ${new Date(u.checkedAt).toLocaleTimeString([], { hour: "2-digit", minute: "2-digit" })}` : ""}.`;
    case "error":
      return esc(u.error ?? "The update check failed.");
    default:
      return "Not checked yet.";
  }
}

function updatesPanel(): string {
  const u = state.update;
  const status = !isHosted
    ? `<span class="pill warn">Browser preview</span>`
    : !u
      ? ""
      : u.state === "ready"
        ? `<span class="pill ok">Update ready</span>`
        : u.installed
          ? `<span class="pill ok">Installed</span>`
          : `<span class="pill warn">Build folder</span>`;

  const busy = u?.state === "checking" || u?.state === "downloading";
  const buttons = u?.state === "ready"
    ? `<button class="mini go" data-act="update-restart">Restart to update</button>`
    : `<button class="mini" data-act="update-check" ${busy || !u?.installed ? "disabled" : ""}>Check for updates</button>`;

  return `<section class="panel">
    <div class="panel-head"><h3>Version ${esc(u?.version ?? "?")}</h3>${status}</div>
    <div class="actions">
      ${buttons}
      <span class="subnote">${updateLine()}</span>
    </div>
    <p class="subnote versions">Releases are read from
      ${u?.source ? `<code>${esc(u.source)}</code> <button class="mini" data-act="update-reveal">Open</button>` : "nowhere yet"}.
      A newer version there is fetched on startup and applied when you restart.</p>
  </section>`;
}

/* ── screen ──────────────────────────────────────────────────────────── */

export function settingsView(): string {
  const s = state.settings;
  const c = state.config;
  const flash = s.flash
    ? `<p class="flash ${s.flashError ? "bad" : "good"}">${esc(s.flash)}</p>`
    : "";

  const keyStatus = !isHosted
    ? `<span class="pill warn">Browser preview</span>`
    : c?.hasApiKey
      ? `<span class="pill ok">Key set</span>`
      : `<span class="pill warn">No key yet</span>`;

  const onRole = state.live && state.assignedRole
    ? ` Right now the client has you on ${state.assignedRole.toLowerCase()}${state.autofilled ? ", autofilled" : ""}.`
    : "";

  return `<div class="bar page-bar">
      <div>
        <p class="eyebrow">Counterpick · this machine</p>
        <h2 class="page-title">Settings</h2>
        <p class="subnote">Everything here lives in <code>%APPDATA%\\Counterpick</code>: the key in
          <code>config.json</code>, the pool in your notes database. Nothing is sent anywhere but the API.</p>
      </div>
      <div class="bar-cta"><button class="ghost" data-act="back">← Back to draft</button></div>
    </div>
    ${flash}

    <div class="panels">
      <section class="panel">
        <div class="panel-head"><h3>Anthropic API key</h3>${keyStatus}</div>
        <form class="fieldrow" id="apiKeyForm">
          <label for="apiKeyInput" hidden>API key</label>
          <input id="apiKeyInput" class="field grow" name="apiKey" type="password" autocomplete="off" spellcheck="false"
            placeholder="${c?.hasApiKey ? "•••••••• (paste a new key to replace it)" : "sk-ant-…"}">
          <button type="submit" class="ghost">Save</button>
          ${c?.hasApiKey ? `<button type="button" class="mini danger" data-act="key-clear">Remove</button>` : ""}
        </form>
        <p class="subnote">Pays for the shortlist and the briefs. A key made inside a workspace needs nothing else.</p>
        <form class="fieldrow" id="workspaceForm">
          <label for="workspaceInput" hidden>Workspace id</label>
          <input id="workspaceInput" class="field grow" name="workspaceId" type="text" autocomplete="off" spellcheck="false"
            placeholder="Workspace id (wrkspc_…)" value="${esc(c?.workspaceId ?? "")}">
          <button type="submit" class="ghost">Save</button>
        </form>
        <p class="subnote">Only for an organisation-level key. If the API answers "not scoped to a workspace",
          paste the workspace id from the Console.</p>
      </section>

      <section class="panel">
        <div class="panel-head"><h3>Draft</h3></div>
        <div class="setting-row"><span class="k">Your role</span>
          <select class="field" data-setting="primaryRole" aria-label="Default role">
            ${ROLES.map((r) => `<option ${(c?.primaryRole ?? state.role) === r ? "selected" : ""}>${r}</option>`).join("")}
          </select></div>
        <p class="subnote">The default. In a draft the app follows the role the client assigns you, autofill
          included, and falls back to this role's pool when that one is empty.${onRole}</p>
        <div class="setting-row"><span class="k">Shortlist</span>${modelSelect("shortlistModel", c?.shortlistModel ?? "claude-sonnet-5")}</div>
        <div class="setting-row"><span class="k">Brief</span>${modelSelect("model", c?.model ?? "claude-opus-5")}</div>
        <p class="subnote">The shortlist has to land inside the pick timer. The brief is read on the loading
          screen and can take its time.</p>
      </section>

      ${poolPanel()}

      ${updatesPanel()}

      <section class="panel foot">
        <div class="panel-head"><h3>Housekeeping</h3></div>
        <div class="actions">
          ${s.confirmClear
            ? `<button class="mini danger" data-act="clear-briefs-go">Yes, clear them</button>
               <button class="mini" data-act="cancel">Keep</button>`
            : `<button class="mini" data-act="clear-briefs">Clear cached briefs</button>`}
          <span class="subnote">Briefs are cached per matchup, so repeat games are instant and free.
            Clearing costs a few cents to rebuild; do it after changing the prompt or the model.</span>
        </div>
        <p class="subnote versions">Data Dragon ${esc(c?.dataDragonVersion ?? "not resolved yet")} ·
          enemy roles placed from play rates for patch ${esc(c?.roleRatesPatch ?? "?")}${
            c?.roleRatesSource === "bundled" ? " (bundled snapshot; the feed was unreachable)" : ""}</p>
      </section>
    </div>`;
}

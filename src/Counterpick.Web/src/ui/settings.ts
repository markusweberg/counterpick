/**
 * Settings: the API key, your role, which models to use, and the champion pool per role.
 *
 * Pool edits write through immediately - there is no Save button to forget. Everything
 * here reads from the host; in a plain browser the pool editor still works against the
 * worked example's champions so the layout can be styled.
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

function poolEditor(): string {
  const s = state.settings;
  const inPool = new Set(s.poolDraft);
  const q = s.search.trim().toLowerCase();

  const tabs = ROLES.map(
    (r) => `<button class="step" data-act="pool-role" data-role="${r}" aria-current="${s.poolRole === r}">${r}</button>`,
  ).join("");

  const chips = s.poolDraft.length === 0
    ? `<p class="empty-note">No champions yet. Search below and click to add.</p>`
    : s.poolDraft.map((k) => `<span class="chip-champ">${portrait(k)}
        <span class="nm">${esc(champion(k).name)}</span>
        <button class="x" data-act="pool-remove" data-key="${k}" title="Remove ${esc(champion(k).name)}">×</button>
      </span>`).join("");

  const all = allChampions();
  const matches = (q ? all.filter((c) => c.name.toLowerCase().includes(q) || c.key.toLowerCase().includes(q)) : all)
    .filter((c) => !inPool.has(c.key))
    .slice(0, q ? 24 : 12);
  const results = all.length === 0
    ? `<p class="empty-note">Champion list not loaded yet. It comes from Data Dragon on first run.</p>`
    : matches.length === 0
      ? `<p class="empty-note">${q ? "No champion matches that." : "Everyone is in your pool."}</p>`
      : `<div class="champ-grid">${matches.map((c) => `<button class="champ-pick" data-act="pool-add" data-key="${c.key}">
            ${portrait(c.key)}<span class="nm">${esc(c.name)}</span></button>`).join("")}</div>`;

  return `<section class="sect"><h3>Champion pool</h3>
    <p class="subnote" style="margin-bottom:12px">The champions you actually play in each role. Only these are scored during draft.</p>
    <div class="steps" style="width:max-content;margin-bottom:14px">${tabs}</div>
    <div class="chips">${chips}</div>
    <input id="poolSearch" class="field" type="search" autocomplete="off" placeholder="Search champions to add…"
      value="${esc(s.search)}" style="margin:14px 0 10px;max-width:360px">
    ${results}
  </section>`;
}

export function settingsView(): string {
  const s = state.settings;
  const c = state.config;
  const flash = s.flash
    ? `<p class="flash ${s.flashError ? "bad" : "good"}">${esc(s.flash)}</p>`
    : "";

  const keyStatus = !isHosted
    ? `<span class="pill warn">Browser preview · no host</span>`
    : c?.hasApiKey
      ? `<span class="pill ok">Key set</span>`
      : `<span class="pill warn">No key yet</span>`;

  return `<div class="bar">
      <div>
        <p class="eyebrow">Settings</p>
        <p class="subnote">Everything here lives in <code>%APPDATA%\\Counterpick\\config.json</code> and your
          notes database, on this machine only.</p>
      </div>
      <div class="bar-cta"><button class="ghost" data-act="back">← Back</button></div>
    </div>
    ${flash}

    <section class="sect"><h3>Anthropic API key</h3>
      <form class="noteform" id="apiKeyForm" style="margin-top:0">
        <label for="apiKeyInput" hidden>API key</label>
        <input id="apiKeyInput" name="apiKey" type="password" autocomplete="off" spellcheck="false"
          placeholder="${c?.hasApiKey ? "•••••••• (enter a new key to replace it)" : "sk-ant-…"}">
        <button type="submit" class="ghost">Save key</button>
        ${c?.hasApiKey ? `<button type="button" class="ghost" data-act="key-clear">Remove</button>` : ""}
      </form>
      <p class="subnote" style="margin-top:8px">${keyStatus} &nbsp; Used for recommendations and briefs. Never leaves this machine except to call the API.</p>
      <form class="noteform" id="workspaceForm" style="margin-top:14px">
        <label for="workspaceInput" hidden>Workspace id</label>
        <input id="workspaceInput" name="workspaceId" type="text" autocomplete="off" spellcheck="false"
          placeholder="Workspace id (wrkspc_…), only for an organisation-level key" value="${esc(c?.workspaceId ?? "")}">
        <button type="submit" class="ghost">Save workspace</button>
      </form>
      <p class="subnote" style="margin-top:8px">A key created inside a workspace needs nothing here. If the API answers
        "not scoped to a workspace", paste the workspace id from the Console.</p>
    </section>

    <div class="two">
      <section class="sect"><h3>Your role</h3>
        <select class="field" data-setting="primaryRole">
          ${ROLES.map((r) => `<option ${state.role === r ? "selected" : ""}>${r}</option>`).join("")}
        </select>
        <p class="subnote" style="margin-top:8px">Decides which enemy pick counts as your lane opponent, and which pool is scored.</p>
      </section>
      <section class="sect"><h3>Models</h3>
        <div class="setting-row"><span class="k">Pick-phase shortlist</span>${modelSelect("shortlistModel", c?.shortlistModel ?? "claude-sonnet-5")}</div>
        <div class="setting-row"><span class="k">Matchup brief</span>${modelSelect("model", c?.model ?? "claude-opus-5")}</div>
        <p class="subnote" style="margin-top:8px">The shortlist has to land inside the pick timer; the brief is read on the loading screen and can take its time.</p>
      </section>
    </div>

    ${poolEditor()}

    <section class="sect"><h3>Cached briefs</h3>
      <p class="subnote" style="margin-bottom:10px">Briefs are cached per matchup so repeat games are instant and free. Clearing only costs a few cents to rebuild.</p>
      ${s.confirmClear
        ? `<div class="actions"><button class="mini danger" data-act="clear-briefs-go">Yes, clear them</button>
             <button class="mini" data-act="cancel">Keep</button></div>`
        : `<div class="actions"><button class="mini" data-act="clear-briefs">Clear cached briefs</button></div>`}
    </section>

    <p class="subnote" style="margin-top:28px">Data Dragon ${esc(c?.dataDragonVersion ?? "version not resolved yet")}.</p>`;
}

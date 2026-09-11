/** Topbar, hero banner and draft board - the parts that persist across all three phases. */

import { ROLES, type Phase } from "../types";
import { champion } from "../champions";
import { isHosted } from "../bridge";
import { splashUrl } from "../ddragon";
import { heroChampion, laneOpponent, poolBorrowed, recommendationFor, state } from "../state";
import { timerRemaining } from "../session";
import { clock, esc, portrait } from "./atoms";

const STEPS: [Phase, string][] = [
  ["draft", "Draft"],
  ["locked", "Brief"],
  ["after", "After game"],
];

const PHASE_LABEL: Record<string, string> = {
  None: "in client",
  Lobby: "in lobby",
  Matchmaking: "in queue",
  ReadyCheck: "match found",
  ChampSelect: "reading draft",
  GameStart: "loading screen",
  InProgress: "in game",
  WaitingForStats: "post-game lobby",
  PreEndOfGame: "post-game lobby",
  EndOfGame: "post-game lobby",
};

function clientLabel(): string {
  if (!isHosted) return "League Client · browser preview";
  const c = state.client;
  if (!c.connected) return "League Client · not running";
  const label = PHASE_LABEL[c.phase] ?? c.phase;
  return c.message ? `League Client · ${label} · ${c.message}` : `League Client · ${label}`;
}

function clockLabel(): string {
  if (!state.live) return "Pick phase";
  if (state.yourTurn) return "Your pick";
  switch (state.timerPhase) {
    case "PLANNING": return "Planning";
    case "BAN_PICK": return "Waiting";
    case "FINALIZATION": return "Finalizing";
    case "GAME_STARTING": return "Starting";
    default: return "Pick phase";
  }
}

export function topbar(): string {
  const steps = STEPS.map(
    ([key, label]) =>
      `<button class="step" data-phase="${key}"
        aria-current="${state.screen === "session" && state.phase === key}"
        ${key !== "draft" && !state.picked ? "disabled" : ""}>${label}</button>`,
  ).join("");

  const foe = laneOpponent();
  const right =
    state.phase === "draft"
      ? `<div class="clock"><span class="t ${state.yourTurn ? "hot" : ""}">${
          !state.live ? "—:——" : state.timerInfinite ? "∞" : clock(timerRemaining())
        }</span><span class="l">${clockLabel()}</span></div>`
      : `<div class="clock"><span class="t" style="font-size:14px;color:var(--ink-2)">
           ${state.picked ? esc(champion(state.picked).name) : "—"}
           <span style="color:var(--ink-3)">vs</span>
           ${foe ? esc(champion(foe).name) : "—"}</span></div>`;

  const connected = !isHosted || state.client.connected;

  return `<header class="topbar">
    <div class="wordmark">Counter<span>pick</span></div>
    <nav class="steps" aria-label="Phase">${steps}</nav>
    <div class="topbar-right">
      <div class="lcu"><span class="dot ${connected ? "" : "off"}"></span> ${clientLabel()}</div>
      ${right}
      <button class="datalink" data-act="settings" title="API key, role, models and pool"
        aria-current="${state.screen === "settings"}">Settings</button>
      <button class="datalink" data-act="data" title="Notes, backups and export"
        aria-current="${state.screen === "data"}">Data</button>
    </div>
  </header>`;
}

export function hero(): string {
  const meKey = heroChampion();
  const me = meKey ? champion(meKey) : null;
  const foeKey = laneOpponent();
  const rec = meKey ? recommendationFor(meKey) : undefined;

  const flagText =
    state.phase === "draft"
      ? state.picked ? "Locked in" : state.hoverKey && !state.selected ? "Hovering" : "Considering"
      : state.phase === "locked" ? "Locked in" : "Game complete";
  const flagClass = state.phase === "draft" && !state.picked ? "" : "locked";

  const meSide = me
    ? `<div class="hero-side me" style="background-image:url(${splashUrl(me.key)});
         background-position:${me.splashPos}"></div>`
    : `<div class="hero-side me" style="background:linear-gradient(120deg,#101B2A,#0B1119)"></div>`;
  const foeSide = foeKey
    ? `<div class="hero-side foe" style="background-image:url(${splashUrl(foeKey)});
         background-position:${champion(foeKey).splashPos}"></div>`
    : `<div class="hero-side foe" style="background:linear-gradient(120deg,#1A1013,#0B1119)"></div>`;

  // Where the role came from is the one thing worth shouting about: an autofill means
  // the board is scoring a lane you did not queue for.
  const autofilled = state.autofilled && state.assignedRole === state.role;
  const roleText = autofilled
    ? `<span class="autofill">Autofilled to ${state.role}</span>`
    : state.assignedRole === state.role ? state.role : `${state.role} · default`;
  const autofillFlag = autofilled && state.phase === "draft"
    ? `<div class="h-flag autofill">Autofilled · ${state.role}${poolBorrowed() ? ` · ${state.poolRole} pool` : ""}</div>`
    : "";

  return `<div class="hero">
    ${meSide}
    ${foeSide}
    <div class="hero-scrim"></div>
    <div class="hero-seam"></div>
    <div class="h-flag ${flagClass}">${flagText}</div>
    ${autofillFlag}
    <div class="hero-txt">
      <div>
        <span class="h-cls">${me ? esc(me.klass) : "Your pool"} · ${roleText} · ${state.picked ? "Your champion" : "Your pick"}</span>
        <h2 class="h-name">${me ? esc(me.name) : "Undecided"}</h2>
      </div>
      <div class="h-mid">
        <span class="h-score">${rec?.score ?? "—"}</span><span class="h-scorel">Fit</span>
      </div>
      <div class="h-foe">
        <span class="h-cls">${foeKey ? esc(champion(foeKey).klass) : "Unassigned"} · Lane opponent</span>
        <h2 class="h-name">${foeKey ? esc(champion(foeKey).name) : "Unknown"}</h2>
      </div>
    </div>
  </div>`;
}

export function board(): string {
  const foe = laneOpponent();

  const ally = state.draft.ally
    .map((slot) => {
      if (slot.isYou) {
        const shown = state.picked ?? (slot.hovering ? slot.championKey : null) ?? state.hoverKey;
        const status = state.picked ? "Locked" : shown ? "Hovering" : slot.onTheClock ? "Picking" : slot.role;
        return `<div class="slot you ${!state.picked && shown ? "hover" : ""}">${portrait(shown)}
          <span class="nm">${shown ? esc(champion(shown).name) : "You"}</span>
          <span class="rl">${status}</span></div>`;
      }
      if (!slot.championKey) {
        return `<div class="slot pending">${portrait(null)}
          <span class="nm">${slot.onTheClock ? "Picking" : "Unpicked"}</span><span class="rl">${slot.role}</span></div>`;
      }
      return `<div class="slot ${slot.hovering ? "hover" : ""}">${portrait(slot.championKey)}
        <span class="nm">${esc(champion(slot.championKey).name)}</span>
        <span class="rl">${slot.hovering ? "Hovering" : slot.role}</span></div>`;
    })
    .join("");

  const enemy = state.draft.enemy
    .map((slot) => {
      if (!slot.championKey) {
        return `<div class="slot pending">${portrait(null)}
          <span class="nm">${slot.onTheClock ? "Picking" : "Unpicked"}</span>
          <span class="rl" ${slot.onTheClock ? 'style="color:var(--enemy)"' : ""}>
            ${slot.onTheClock ? "On the clock" : slot.role}</span></div>`;
      }
      const key = slot.championKey;
      if (slot.hovering) {
        return `<div class="slot hover">${portrait(key)}
          <span class="nm">${esc(champion(key).name)}</span>
          <span class="rl" style="color:var(--enemy)">Hovering</span></div>`;
      }
      const isLane = key === foe;
      const assigned = state.draft.enemyRoles[key];
      const guessed = state.roleSource[key] === "claude";
      const options = [
        assigned ? "" : `<option value="" selected>Role?</option>`,
        ...ROLES.map((r) => `<option ${assigned === r ? "selected" : ""}>${r}</option>`),
      ].join("");
      return `<div class="slot ${isLane ? "lane" : ""}">${portrait(key)}
        <span class="nm">${esc(champion(key).name)}</span>
        <select class="rolesel ${isLane ? "is-lane" : ""} ${assigned ? "" : "unknown"}" data-champ="${key}"
          title="${guessed ? "Claude's guess. Change it if you know better." : assigned ? "" : "The client does not say. Set it, or wait for the shortlist to work it out."}"
          aria-label="${esc(champion(key).name)} role">${options}</select></div>`;
    })
    .join("");

  return `<section class="board">
    <div class="side-ally">
      <div class="side-label"><span class="bar"></span> Your team</div>
      <div class="row">${ally}</div>
    </div>
    <div class="vs">VS</div>
    <div class="side-enemy">
      <div class="side-label"><span class="bar"></span> Enemy</div>
      <div class="row">${enemy}</div>
    </div>
  </section>`;
}

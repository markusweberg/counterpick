/** Topbar, hero banner and draft board - the parts that persist across all three phases. */

import { ROLES, type Phase } from "../types";
import { champion } from "../champions";
import { isHosted } from "../bridge";
import { splashUrl } from "../ddragon";
import {
  heroChampion, LANE_SETTLED, laneConfidence, laneOpponent, poolBorrowed, recommendationFor, roleFixed, state,
} from "../state";
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

/**
 * The window buttons. The app is frameless - there is no Windows caption above the page -
 * so the topbar is both the nav and the title bar, and these three are the only way to
 * minimise, maximise or close. Dragging is handled by the `app-region` in the stylesheet,
 * which WebView2 hands straight to Windows; only the buttons need the host.
 *
 * Absent in the browser preview, where the tab already has its own.
 */
function windowButtons(): string {
  if (!isHosted) return "";
  return `<div class="winbtns">
    <button class="winbtn" data-act="win-minimize" title="Minimise" aria-label="Minimise">&#xE921;</button>
    <button class="winbtn" data-act="win-maximize" title="Maximise" aria-label="Maximise">&#xE922;</button>
    <button class="winbtn close" data-act="win-close" title="Close" aria-label="Close">&#xE8BB;</button>
  </div>`;
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
      : `<div class="clock"><span class="t" style="font-size:15px;color:var(--ink-2)">
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
      <nav class="steps" aria-label="Screens">
        ${state.update?.state === "ready"
          ? `<button class="step upd" data-act="update-restart"
               title="Version ${esc(state.update.available ?? "")} is downloaded. Restart to switch to it.">Update ready</button>`
          : ""}
        <button class="step" data-act="settings" title="API key, role, models and pool"
          aria-current="${state.screen === "settings"}">Settings</button>
        <button class="step" data-act="data" title="Notes, backups and export"
          aria-current="${state.screen === "data"}">Data</button>
      </nav>
      ${windowButtons()}
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
    : `<div class="hero-side me" style="background:linear-gradient(120deg,#0E2338,#010A13)"></div>`;
  const foeSide = foeKey
    ? `<div class="hero-side foe" style="background-image:url(${splashUrl(foeKey)});
         background-position:${champion(foeKey).splashPos}"></div>`
    : `<div class="hero-side foe" style="background:linear-gradient(120deg,#2A1015,#010A13)"></div>`;

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
        <span class="h-cls">${foeKey ? `${esc(champion(foeKey).klass)} · ${foeLabel()}` : "Unassigned · Lane opponent"}</span>
        <h2 class="h-name">${foeKey ? esc(champion(foeKey).name) : "Unknown"}</h2>
      </div>
    </div>
  </div>`;
}

/**
 * How sure the app is that this is your lane opponent. A placement from play rates is
 * said to be a guess until it is sure enough, or until the game confirms it.
 */
function foeLabel(): string {
  const foe = laneOpponent();
  if (!foe) return "Lane opponent";
  if (state.roleSource[foe] === "game") return "Lane opponent · confirmed";
  const pct = Math.round(laneConfidence() * 100);
  if (roleFixed(foe) || laneConfidence() >= LANE_SETTLED) {
    return roleFixed(foe) ? "Lane opponent" : `<span title="${pct}% from play rates">Lane opponent</span>`;
  }
  return `<span class="probable" title="${pct}% from play rates. Confirmed when the game starts.">Probably your lane opponent</span>`;
}

/** A champion's name on one line. Long names ("Nunu & Willump") drop a size rather than wrap. */
function name(key: string): string {
  const n = champion(key).name;
  return `<span class="nm ${n.length > 8 ? "long" : ""}">${esc(n)}</span>`;
}

export function board(): string {
  const foe = laneOpponent();

  const ally = state.draft.ally
    .map((slot) => {
      if (slot.isYou) {
        const shown = state.picked ?? (slot.hovering ? slot.championKey : null) ?? state.hoverKey;
        const status = state.picked ? "Locked" : shown ? "Hovering" : slot.onTheClock ? "Picking" : slot.role;
        return `<div class="slot you ${!state.picked && shown ? "hover" : ""}">${portrait(shown)}
          ${shown ? name(shown) : `<span class="nm">You</span>`}
          <span class="rl">${status}</span></div>`;
      }
      if (!slot.championKey) {
        return `<div class="slot pending">${portrait(null)}
          <span class="nm">${slot.onTheClock ? "Picking" : "Unpicked"}</span><span class="rl">${slot.role}</span></div>`;
      }
      return `<div class="slot ${slot.hovering ? "hover" : ""}">${portrait(slot.championKey)}
        ${name(slot.championKey)}
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
          ${name(key)}
          <span class="rl" style="color:var(--enemy)">Hovering</span></div>`;
      }
      const isLane = key === foe;
      const assigned = state.draft.enemyRoles[key];
      const source = state.roleSource[key];
      const pct = Math.round((state.roleConfidence[key] ?? 0) * 100);
      const shaky = source === "inferred" && pct < LANE_SETTLED * 100;
      const title =
        source === "game" ? "Confirmed by the game."
        : source === "inferred" ? `Placed from play rates: ${pct}% likely. Change it if you know better.`
        : assigned ? ""
        : "The client does not say. Set it, or wait for the next pick to place it.";
      const options = [
        assigned ? "" : `<option value="" selected>Role?</option>`,
        ...ROLES.map((r) => `<option ${assigned === r ? "selected" : ""}>${r}${assigned === r && shaky ? "?" : ""}</option>`),
      ].join("");
      return `<div class="slot ${isLane ? "lane" : ""}">${portrait(key)}
        ${name(key)}
        <select class="rolesel ${isLane ? "is-lane" : ""} ${assigned ? "" : "unknown"} ${shaky ? "guess" : ""}" data-champ="${key}"
          title="${title}"
          aria-label="${esc(champion(key).name)} role">${options}</select></div>`;
    })
    .join("");

  return `<section class="board">
    <div class="side-ally">
      <div class="side-label"><span class="tick"></span> Your team</div>
      <div class="row">${ally}</div>
    </div>
    <div class="vs">VS</div>
    <div class="side-enemy">
      <div class="side-label"><span class="tick"></span> Enemy</div>
      <div class="row">${enemy}</div>
    </div>
  </section>`;
}

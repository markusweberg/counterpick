/** Topbar, hero banner and draft board - the parts that persist across all three phases. */

import { ROLES, type Phase } from "../types";
import { champion } from "../data/scenario";
import { splashUrl } from "../ddragon";
import { heroChampion, laneOpponent, recommendationFor, state } from "../state";
import { esc, portrait } from "./atoms";

const STEPS: [Phase, string][] = [
  ["draft", "Draft"],
  ["locked", "Brief"],
  ["after", "After game"],
];

const CLIENT_STATUS: Record<Phase, string> = {
  draft: "reading draft",
  locked: "in game — Summoner's Rift",
  after: "post-game lobby",
};

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
      ? `<div class="clock"><span class="t">0:27</span><span class="l">Your pick</span></div>`
      : `<div class="clock"><span class="t" style="font-size:14px;color:var(--ink-2)">
           ${state.picked ? esc(champion(state.picked).name) : "—"}
           <span style="color:var(--ink-3)">vs</span>
           ${foe ? esc(champion(foe).name) : "—"}</span></div>`;

  return `<header class="topbar">
    <div class="wordmark">Counter<span>pick</span></div>
    <nav class="steps" aria-label="Phase">${steps}</nav>
    <div class="topbar-right">
      <div class="lcu"><span class="dot"></span> League Client · ${CLIENT_STATUS[state.phase]}</div>
      ${right}
      <button class="datalink" data-act="data" title="Notes, backups and export">Data</button>
    </div>
  </header>`;
}

export function hero(): string {
  const meKey = heroChampion();
  const me = champion(meKey);
  const foeKey = laneOpponent();
  const rec = recommendationFor(meKey);

  const flagText =
    state.phase === "draft" ? "Considering" : state.phase === "locked" ? "Locked in" : "Game complete";
  const flagClass = state.phase === "draft" ? "" : "locked";

  const foeSide = foeKey
    ? `<div class="hero-side foe" style="background-image:url(${splashUrl(foeKey)});
         background-position:${champion(foeKey).splashPos}"></div>`
    : `<div class="hero-side foe" style="background:linear-gradient(120deg,#1A1013,#0B1119)"></div>`;

  return `<div class="hero">
    <div class="hero-side me" style="background-image:url(${splashUrl(me.key)});
      background-position:${me.splashPos}"></div>
    ${foeSide}
    <div class="hero-scrim"></div>
    <div class="hero-seam"></div>
    <div class="h-flag ${flagClass}">${flagText}</div>
    <div class="hero-txt">
      <div>
        <span class="h-cls">${esc(me.klass)} · ${state.picked ? "Your champion" : "Your pick"}</span>
        <h2 class="h-name">${esc(me.name)}</h2>
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
        return `<div class="slot you">${portrait(state.picked)}
          <span class="nm">${state.picked ? esc(champion(state.picked).name) : "You"}</span>
          <span class="rl">${state.picked ? "Locked" : "Picking"}</span></div>`;
      }
      if (!slot.championKey) {
        return `<div class="slot pending">${portrait(null)}
          <span class="nm">Unpicked</span><span class="rl">${slot.role}</span></div>`;
      }
      return `<div class="slot">${portrait(slot.championKey)}
        <span class="nm">${esc(champion(slot.championKey).name)}</span>
        <span class="rl">${slot.role}</span></div>`;
    })
    .join("");

  const enemy = state.draft.enemy
    .map((slot) => {
      if (!slot.championKey) {
        return `<div class="slot pending">${portrait(null)}
          <span class="nm">${slot.onTheClock ? "Last pick" : "Unpicked"}</span>
          <span class="rl" ${slot.onTheClock ? 'style="color:var(--enemy)"' : ""}>
            ${slot.onTheClock ? "On the clock" : slot.role}</span></div>`;
      }
      const key = slot.championKey;
      const isLane = key === foe;
      const options = ROLES.map(
        (r) => `<option ${state.draft.enemyRoles[key] === r ? "selected" : ""}>${r}</option>`,
      ).join("");
      return `<div class="slot ${isLane ? "lane" : ""}">${portrait(key)}
        <span class="nm">${esc(champion(key).name)}</span>
        <select class="rolesel ${isLane ? "is-lane" : ""}" data-champ="${key}"
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

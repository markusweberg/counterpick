/** The three phase views: draft, brief, after-game. */

import { isHosted } from "../bridge";
import { champion } from "../champions";
import {
  bestScores, briefFor, laneConfidence, laneOpponent, laneSettled, notesFor, poolBorrowed, state,
} from "../state";
import { availablePool, scoringBlocker } from "../session";
import type { NoteRecord, Recommendation } from "../types";
import { esc, portrait, recordLabel, rich, VERDICT_CLASS, VERDICT_COLOR } from "./atoms";

/* ── draft ───────────────────────────────────────────────────────────── */

function exampleBanner(): string {
  if (isHosted) return "";
  return `<p class="proto"><b>Browser preview.</b> This is the worked example from the design
     prototype. Inside the app the draft comes from the League client and the
     recommendations from Claude.</p>`;
}

function card(rec: Recommendation, i: number): string {
  const c = champion(rec.championKey);
  const hints = rec.hints.map((h) => `<li>${esc(h)}</li>`).join("");
  return `<div class="rec" role="button" tabindex="0"
      aria-pressed="${state.selected === rec.championKey}" data-pick="${rec.championKey}">
    <span class="rec-top">${portrait(rec.championKey)}
      <span class="rec-id">
        <span class="nm">${esc(c.name)}</span>
        <span class="cls">${esc(c.klass)} · ${recordLabel(state.records[rec.championKey])}</span>
      </span>
      <span class="score">${rec.score}</span>
    </span>
    <span class="meter"><i style="width:${rec.score}%;background:${VERDICT_COLOR[rec.verdict]}"></i></span>
    <span class="rec-chips">
      <span class="chip ${VERDICT_CLASS[rec.verdict]}">${rec.verdict}</span>
      ${i === 0 ? '<span class="badge1">Recommended</span>' : ""}
    </span>
    <p class="why">${esc(rec.why)}</p>
    <ul class="hints">${hints}</ul>
  </div>`;
}

/**
 * One open pick: the same card, marked for where it came from. Clickable like the rest,
 * so an out-of-pool pick can still be locked and briefed - the app records what you
 * actually played, not only what you practise.
 */
function openCard(rec: Recommendation, i: number): string {
  const c = champion(rec.championKey);
  const mine = state.pool.includes(rec.championKey);
  const hints = rec.hints.map((h) => `<li>${esc(h)}</li>`).join("");
  return `<div class="rec" role="button" tabindex="0"
      aria-pressed="${state.selected === rec.championKey}" data-pick="${rec.championKey}">
    <span class="rec-top">${portrait(rec.championKey)}
      <span class="rec-id">
        <span class="nm">${esc(c.name)}</span>
        <span class="cls">${esc(c.klass)}${mine ? ` · ${recordLabel(state.records[rec.championKey])}` : ""}</span>
      </span>
      <span class="score">${rec.score}</span>
    </span>
    <span class="meter"><i style="width:${rec.score}%;background:${VERDICT_COLOR[rec.verdict]}"></i></span>
    <span class="rec-chips">
      <span class="chip ${VERDICT_CLASS[rec.verdict]}">${rec.verdict}</span>
      ${mine ? '<span class="badge-pool">In your pool</span>' : i === 0 ? '<span class="badge-open">Best on the board</span>' : ""}
    </span>
    <p class="why">${esc(rec.why)}</p>
    <ul class="hints">${hints}</ul>
  </div>`;
}

/**
 * What the draft is asking for, pool or no pool. The shortlist answers "which of mine",
 * which is the question on the clock; this answers the one it hides - whether the right
 * pick here is a champion you do not play. Only shown once the pool itself is scored,
 * and never instead of it.
 */
function openSection(): string {
  if (state.openPicks.length === 0) return "";
  const { pool, open } = bestScores();
  const gap = open - pool;
  const top = state.openPicks[0]!;
  const name = esc(champion(top.championKey).name);

  const reading =
    state.pool.includes(top.championKey)
      ? `${name} is the best pick on the board and you already play it.`
      : gap >= 10
      ? `${name} is ${gap} points clear of anything you play. This draft wants something outside your pool.`
      : gap >= 4
      ? `${name} is ${gap} points above your best, which is worth knowing and not worth first-timing.`
      : `Your pool covers this draft: nothing on the board beats it by more than ${Math.max(gap, 0)}.`;

  return `<div class="bar open-bar"><div>
      <p class="eyebrow">Best in ${state.role.toLowerCase()} · any champion</p>
      <p class="subnote">${reading} Scored on the same scale as your pool, against the same
        draft, from the champions actually played in ${state.role.toLowerCase()} this patch.</p>
    </div></div>
    <div class="recs open">${state.openPicks.map(openCard).join("")}</div>`;
}

/** The pool before there is anything to score it against. */
function unscoredCard(key: string): string {
  const c = champion(key);
  return `<div class="rec" role="button" tabindex="0"
      aria-pressed="${state.selected === key}" data-pick="${key}">
    <span class="rec-top">${portrait(key)}
      <span class="rec-id">
        <span class="nm">${esc(c.name)}</span>
        <span class="cls">${esc(c.klass)}</span>
      </span>
      <span class="score" style="color:var(--ink-3)">—</span>
    </span>
    <span class="meter"><i style="width:0"></i></span>
  </div>`;
}

/** "Your top pool": the role matters once the client can move you. */
function poolLabel(): string {
  return `Your ${state.poolRole.toLowerCase()} pool`;
}

/** When the pool on the board is a stand-in, say so before anything else. */
function borrowedNote(): string {
  if (!poolBorrowed()) return "";
  return `No ${state.role.toLowerCase()} pool yet, so your ${state.poolRole.toLowerCase()} pool is ranked for ${state.role.toLowerCase()} instead. `;
}

function subnote(): string {
  const foe = laneOpponent();
  const d = state.draft;
  const jungler = Object.entries(d.enemyRoles).find(([k, r]) => r === "Jungle" && k !== foe)?.[0];
  const allies = d.ally.filter((s) => s.championKey && !s.isYou && !s.hovering).map((s) => champion(s.championKey!).name);
  const enemies = d.enemy.filter((s) => s.championKey && !s.hovering).map((s) => champion(s.championKey!).name);
  const parts = [
    foe
      ? `${laneSettled() ? "the" : "probably the"} ${champion(foe).name} lane`
      : `their picks so far (${enemies.join(", ")}), nobody placed in ${state.role.toLowerCase()} yet`,
  ];
  if (jungler) parts.push(`${champion(jungler).name}'s jungle pressure`);
  if (allies.length) parts.push(`how each fits alongside ${allies.join(", ")}`);
  const hidden = d.enemyPicksRemaining;
  const tail = hidden > 0
    ? ` ${hidden === 1 ? "Their last pick is" : `${hidden} enemy picks are`} still hidden.`
    : "";
  return `${borrowedNote()}Scored on ${parts.join(", ")}.${tail}`;
}

/** A boxed message in place of content. "warn" is for something that went wrong. */
function notice(title: string, body: string, action = "", tone: "quiet" | "warn" = "quiet"): string {
  return `<div class="notice ${tone}"><h3>${title}</h3>${body ? `<p>${body}</p>` : ""}${action}</div>`;
}

/**
 * Between drafts there is no assigned role, so there is no pool to show: which pool gets
 * ranked is decided by the role the client gives you when champ select opens, autofill
 * included. Listing the default role's champions here would invite a pick for a lane you
 * may not end up in, so the screen says what it is waiting for instead.
 */
function idleView(): string {
  const role = state.role.toLowerCase();
  const n = state.pool.length;
  const pool = `${n} champion${n === 1 ? "" : "s"} in your ${role} pool`;
  const edit = `<button class="ghost" data-act="settings">Edit your pool</button>`;

  return state.client.connected
    ? notice("Waiting for champ select",
        `${pool}, ready. Nothing is ranked yet: the role comes from the client when the draft
         opens, and the pool scored is that role's.`, edit)
    : notice("League client not running",
        `${pool}. Start the client and the draft appears here as it happens, scored for
         whichever role you are given.`, edit);
}

export function draftView(): string {
  const foe = laneOpponent();
  const banned = state.pool.filter((k) => state.draft.bans.includes(k));

  // Browser preview: the scenario only knows Darius, so say so rather than show stale advice.
  if (!isHosted && foe !== "Darius") {
    const name = foe ? esc(champion(foe).name) : "an unknown top laner";
    return notice(`Re-scoring for ${name}`,
      `You moved ${foe ? esc(champion(foe).name) : "your lane opponent"} into ${state.role.toLowerCase()} lane.
       Inside the app this re-runs the whole pool against the new matchup. The preview only
       carries a written scenario for Darius.`,
      `<button class="ghost" data-act="reset-roles">Put Darius back on top</button>`);
  }

  const blocker = isHosted ? scoringBlocker() : null;
  let body: string;

  if (blocker === "key") {
    body = notice("Add your Anthropic API key",
      "Recommendations and briefs come from Claude, and there is no key yet. It is stored on this machine only.",
      `<button class="ghost" data-act="settings">Open settings</button>`);
  } else if (blocker === "pool") {
    const lane = state.role.toLowerCase();
    body = state.poolLoading
      ? notice(`Loading your ${lane} pool<span class="ellipsis"></span>`, "")
      : state.pool.length === 0
      ? state.autofilled && state.config
        ? notice(`Autofilled to ${lane}, and there is no ${lane} pool`,
            `Neither your ${lane} pool nor your ${state.config.primaryRole.toLowerCase()} pool has anything in it.
             Add a couple of champions you can stand in with; they are ranked against every draft.`,
            `<button class="ghost" data-act="settings">Build your ${lane} pool</button>`)
        : notice(`Your ${lane} pool is empty`,
            "Add the champions you actually play and they will be ranked against every draft.",
            `<button class="ghost" data-act="settings">Build your pool</button>`)
      : notice("Nothing left to pick",
          `Every champion in your pool is banned or already taken. Your pool: ${state.pool.map((k) => esc(champion(k).name)).join(", ")}.`);
  } else if (blocker === "enemy" && !state.live) {
    body = idleView();
  } else if (blocker === "enemy") {
    const cards = availablePool().map(unscoredCard).join("");
    body = `<div class="bar"><div>
        <p class="eyebrow">${poolLabel()} · waiting for enemy picks</p>
        <p class="subnote">${borrowedNote()}Nothing locked on their side yet. The pool is scored the moment their first pick lands, and again as more come in. The client does not say who plays where; each pick is placed from how often it is played in each role, and you can correct it on the board.</p>
      </div></div>
      <div class="recs">${cards}</div>`;
  } else if (state.scoring === "error") {
    body = notice("Scoring failed", esc(state.scoringError ?? "Unknown error"),
      `<button class="ghost" data-act="rescore">Try again</button>`, "warn");
  } else if (state.recommendations.length === 0) {
    const cards = availablePool().map(unscoredCard).join("");
    body = `<div class="bar"><div>
        <p class="eyebrow">${poolLabel()} · scoring<span class="ellipsis"></span></p>
        <p class="subnote">${subnote()}</p>
      </div></div>
      <div class="recs">${cards}</div>`;
  } else {
    const cards = state.recommendations.map(card).join("");
    const busy = state.scoring === "loading" ? ` · re-scoring<span class="ellipsis"></span>` : "";
    const selected = state.selected ?? state.recommendations[0]!.championKey;
    body = `<div class="bar">
        <div>
          <p class="eyebrow">${poolLabel()} · ranked against this draft${busy}</p>
          <p class="subnote">${subnote()}</p>
        </div>
        <div class="bar-cta">
          <button class="lock" data-act="lock">Lock in ${esc(champion(selected).name)}</button>
        </div>
      </div>
      <div class="recs">${cards}</div>
      ${openSection()}`;
  }

  const bans = banned.length
    ? `<p class="subnote" style="margin-top:12px">Banned from your pool: ${banned.map((k) => esc(champion(k).name)).join(", ")}.</p>`
    : "";

  return `${body}${bans}${exampleBanner()}`;
}

/* ── brief ───────────────────────────────────────────────────────────── */

function noteList(notes: NoteRecord[], emptyText: string): string {
  if (notes.length === 0) return `<p class="empty-note">${esc(emptyText)}</p>`;
  return notes
    .map(
      (n) => `<div class="note"><span class="d">${esc(n.createdAt)}</span>
        <p>${esc(n.body)}</p></div>`,
    )
    .join("");
}

export function briefView(): string {
  const picked = state.picked;
  const foe = laneOpponent();
  if (!picked || !foe) {
    return notice("Nothing locked in yet",
      foe ? "Lock a champion and the brief starts writing." : "No lane opponent is placed yet, so there is no matchup to brief.");
  }

  const me = champion(picked);
  const opp = champion(foe);
  const slot = briefFor(picked, foe);

  const head = (cta: string) => `<div class="bar">
      <div>
        <p class="eyebrow">Matchup brief · ${esc(me.name)} into ${esc(opp.name)}</p>
        <p class="subnote">Read this on the loading screen. Nothing here needs a decision
          in the next thirty seconds.</p>
      </div>
      <div class="bar-cta">${cta}</div>
    </div>`;

  const notes = `<section class="sect"><h3>What you wrote last time</h3>
      ${noteList(notesFor(picked, foe), "Nothing saved for this matchup yet. Write a line after the game and it shows up here next time.")}
    </section>`;

  if (!slot) {
    // Not requested: the placement is not sure enough to spend a brief on. It is written
    // the moment the game confirms who is where.
    const pct = Math.round(laneConfidence() * 100);
    const body = laneSettled()
      ? "Not requested yet."
      : `The play rates put ${esc(opp.name)} in your lane at ${pct}%, which is not sure enough to write a
         brief about. It is written the moment the game confirms who is where, on the loading screen. If
         you can see it is right, write it now.`;
    return `${head(`<button class="ghost" data-act="after">Game over →</button>`)}
      <div class="notice quiet"><h3>${laneSettled() ? "Brief not written yet" : `Probably ${esc(opp.name)} in ${state.role.toLowerCase()}`}</h3>
        <p>${body}</p>
        <button class="ghost" data-act="brief-now">Write it now</button>
      </div>${notes}`;
  }
  if (slot.status === "loading") {
    return `${head(`<button class="ghost" data-act="after">Game over →</button>`)}
      <div class="notice quiet"><h3>Writing the brief<span class="ellipsis"></span></h3>
        <p>Claude is working through the matchup. This usually takes a few seconds.</p>
      </div>${notes}`;
  }
  if (slot.status === "error") {
    return `${head(`<button class="ghost" data-act="after">Game over →</button>`)}
      ${notice("The brief did not arrive", esc(slot.error), `<button class="ghost" data-act="brief-retry">Try again</button>`, "warn")}
      ${notes}`;
  }

  const b = slot.brief;
  const beats = b.lane
    .map((x) => `<li class="beat"><span class="mark">${esc(x.mark)}</span><p>${rich(x.text)}</p></li>`)
    .join("");
  const setup = Object.entries({
    Keystone: b.setup.keystone,
    Secondary: b.setup.secondary,
    Summoners: b.setup.summoners,
    First: b.setup.first,
  })
    .map(([k, v]) => `<div class="kv"><span class="k">${k}</span><span class="v">${esc(v)}</span></div>`)
    .join("");

  const cta = `${isHosted ? `<button class="ghost" data-act="brief-retry" title="Ask Claude again">Rewrite</button> ` : ""}
    <button class="ghost" data-act="after">Game over →</button>`;

  return `${head(cta)}
    <p class="headline">${rich(b.headline)}</p>
    <section class="sect"><h3>The lane</h3><ul class="beats">${beats}</ul></section>
    <section class="sect"><h3>Kill windows</h3>
      <div class="windows">
        <div class="win mine"><h4>You can kill ${esc(opp.name)}</h4>
          <ul>${b.youKill.map((x) => `<li>${esc(x)}</li>`).join("")}</ul></div>
        <div class="win theirs"><h4>${esc(opp.name)} can kill you</h4>
          <ul>${b.theyKill.map((x) => `<li>${esc(x)}</li>`).join("")}</ul></div>
      </div></section>
    <div class="two">
      <section class="sect"><h3>Their jungler</h3><p class="para">${rich(b.jungle)}</p></section>
      <section class="sect"><h3>Your job in this comp</h3><p class="para">${rich(b.comp)}</p></section>
    </div>
    <section class="sect"><h3>Setup</h3>
      <div class="setup">${setup}</div>
      <p class="para">${esc(b.setupNote)}</p></section>
    ${notes}`;
}

/* ── after game ──────────────────────────────────────────────────────── */

export function afterView(): string {
  const picked = state.picked;
  const foe = laneOpponent();
  if (!picked || !foe) return notice("No game to record", "Lock a champion into a known lane opponent first.");

  const me = champion(picked);
  const opp = champion(foe);

  return `<div class="bar">
      <div>
        <p class="eyebrow">After the game · ${esc(me.name)} into ${esc(opp.name)}</p>
        <p class="subnote">One line is enough. It goes into every future brief for this
          matchup and into how the pool gets scored next time.</p>
      </div>
      <div class="bar-cta"><button class="ghost" data-act="draft">← New draft</button></div>
    </div>
    <div class="result">
      <button class="res w" data-res="win" aria-pressed="${state.result === "win"}">Win</button>
      <button class="res l" data-res="loss" aria-pressed="${state.result === "loss"}">Loss</button>
    </div>
    <section class="sect"><h3>What did you learn?</h3>
      <form class="noteform" id="noteForm">
        <label for="noteInput" hidden>New note</label>
        <input id="noteInput" type="text" autocomplete="off"
          placeholder="e.g. Ward tri at 2:45, not 3:15 — she was there both games">
        <button type="submit" class="ghost">Save note</button>
      </form>
    </section>
    <section class="sect"><h3>Your notes on ${esc(me.name)} into ${esc(opp.name)}</h3>
      ${noteList(notesFor(picked, foe), "No notes on this matchup yet.")}
    </section>`;
}

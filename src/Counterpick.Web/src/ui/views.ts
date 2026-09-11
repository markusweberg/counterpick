/** The three phase views: draft, brief, after-game. */

import { isHosted } from "../bridge";
import { champion } from "../champions";
import { briefFor, laneOpponent, notesFor, state } from "../state";
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

function subnote(): string {
  const foe = laneOpponent();
  const d = state.draft;
  const jungler = Object.entries(d.enemyRoles).find(([k, r]) => r === "Jungle" && k !== foe)?.[0];
  const allies = d.ally.filter((s) => s.championKey && !s.isYou && !s.hovering).map((s) => champion(s.championKey!).name);
  const enemies = d.enemy.filter((s) => s.championKey && !s.hovering).map((s) => champion(s.championKey!).name);
  const parts = [foe ? `the ${champion(foe).name} lane` : `their picks so far (${enemies.join(", ")}), no ${state.role.toLowerCase()} laner identified yet`];
  if (jungler) parts.push(`${champion(jungler).name}'s jungle pressure`);
  if (allies.length) parts.push(`how each fits alongside ${allies.join(", ")}`);
  const hidden = d.enemyPicksRemaining;
  const tail = hidden > 0
    ? ` ${hidden === 1 ? "Their last pick is" : `${hidden} enemy picks are`} still hidden.`
    : "";
  return `Scored on ${parts.join(", ")}.${tail}`;
}

function notice(title: string, body: string, action = ""): string {
  return `<div class="notice"><h3>${title}</h3><p>${body}</p>${action}</div>`;
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
    body = state.pool.length === 0
      ? notice(`Your ${state.role.toLowerCase()} pool is empty`,
          "Add the champions you actually play and they will be ranked against every draft.",
          `<button class="ghost" data-act="settings">Build your pool</button>`)
      : notice("Nothing left to pick",
          `Every champion in your pool is banned or already taken. Your pool: ${state.pool.map((k) => esc(champion(k).name)).join(", ")}.`);
  } else if (blocker === "enemy") {
    const cards = availablePool().map(unscoredCard).join("");
    const waiting = state.live
      ? "Nothing locked on their side yet. The pool is scored the moment their first pick lands, and again as more come in."
      : state.client.connected
        ? "Open a champ select in the League client and the draft appears here."
        : "Start the League client and the draft appears here as it happens.";
    body = `<div class="bar"><div>
        <p class="eyebrow">Your pool · waiting for enemy picks</p>
        <p class="subnote">${waiting} The client does not say who plays where; the shortlist works that out, and you can correct it on the board.</p>
      </div></div>
      <div class="recs">${cards}</div>`;
  } else if (state.scoring === "error") {
    body = notice("Scoring failed", esc(state.scoringError ?? "Unknown error"),
      `<button class="ghost" data-act="rescore">Try again</button>`);
  } else if (state.recommendations.length === 0) {
    const cards = availablePool().map(unscoredCard).join("");
    body = `<div class="bar"><div>
        <p class="eyebrow">Your pool · scoring<span class="ellipsis"></span></p>
        <p class="subnote">${subnote()}</p>
      </div></div>
      <div class="recs">${cards}</div>`;
  } else {
    const cards = state.recommendations.map(card).join("");
    const busy = state.scoring === "loading" ? ` · re-scoring<span class="ellipsis"></span>` : "";
    const selected = state.selected ?? state.recommendations[0]!.championKey;
    body = `<div class="bar">
        <div>
          <p class="eyebrow">Your pool · ranked against this draft${busy}</p>
          <p class="subnote">${subnote()}</p>
        </div>
        <div class="bar-cta">
          <button class="lock" data-act="lock">Lock in ${esc(champion(selected).name)}</button>
        </div>
      </div>
      <div class="recs">${cards}</div>`;
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
      foe ? "Lock a champion and the brief starts writing." : "No lane opponent is assigned, so there is no matchup to brief.");
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

  if (!slot || slot.status === "loading") {
    return `${head(`<button class="ghost" data-act="after">Game over →</button>`)}
      <div class="notice quiet"><h3>Writing the brief<span class="ellipsis"></span></h3>
        <p>${slot ? "Claude is working through the matchup. This usually takes a few seconds." : "Not requested yet."}</p>
        ${slot ? "" : `<button class="ghost" data-act="brief-retry">Write it now</button>`}
      </div>${notes}`;
  }
  if (slot.status === "error") {
    return `${head(`<button class="ghost" data-act="after">Game over →</button>`)}
      ${notice("The brief did not arrive", esc(slot.error), `<button class="ghost" data-act="brief-retry">Try again</button>`)}
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

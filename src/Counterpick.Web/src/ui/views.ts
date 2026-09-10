/** The three phase views: draft, brief, after-game. */

import { champion } from "../data/scenario";
import { laneOpponent, notesFor, recommendationFor, state } from "../state";
import type { NoteRecord } from "../types";
import { esc, portrait, recordLabel, VERDICT_CLASS, VERDICT_COLOR } from "./atoms";

const MOCK_BANNER = state.live
  ? ""
  : `<p class="proto"><b>Worked example.</b> Draft state and recommendations are the
     scenario from the design prototype. Once the League client listener and the Claude
     client are wired up, both arrive live and this banner disappears.</p>`;

/* ── draft ───────────────────────────────────────────────────────────── */

export function draftView(): string {
  const foe = laneOpponent();

  // Role assignment is the one input that changes every downstream answer, so a
  // reassignment we have no scenario for has to say so rather than show stale advice.
  if (foe !== "Darius") {
    const name = foe ? esc(champion(foe).name) : "an unknown top laner";
    return `<div class="notice">
      <h3>Re-scoring for ${name}</h3>
      <p>You moved ${foe ? esc(champion(foe).name) : "your lane opponent"} into ${state.role.toLowerCase()} lane.
         In the finished app this re-runs the whole pool against the new matchup and
         rewrites every recommendation.</p>
      <p>This build only carries a written scenario for Darius.</p>
      <button class="ghost" data-act="reset-roles">Put Darius back on top</button>
    </div>`;
  }

  const cards = state.recommendations
    .map((rec, i) => {
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
    })
    .join("");

  return `<div class="bar">
      <div>
        <p class="eyebrow">Your pool · ranked against this draft</p>
        <p class="subnote">Scored on the ${esc(champion("Darius").name)} lane, Nidalee's jungle
          pressure, and how each fits alongside Sejuani and Orianna. Their last pick is still hidden.</p>
      </div>
      <div class="bar-cta">
        <button class="lock" data-act="lock">Lock in ${esc(champion(state.selected).name)}</button>
      </div>
    </div>
    <div class="recs">${cards}</div>
    ${MOCK_BANNER}`;
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
  const rec = picked ? recommendationFor(picked) : undefined;
  if (!picked || !foe || !rec) return `<div class="notice"><h3>Nothing locked in yet</h3></div>`;

  const b = rec.brief;
  const me = champion(picked);
  const opp = champion(foe);

  const beats = b.lane
    .map((x) => `<li class="beat"><span class="mark">${esc(x.mark)}</span><p>${x.text}</p></li>`)
    .join("");
  const setup = Object.entries({
    Keystone: b.setup.keystone,
    Secondary: b.setup.secondary,
    Summoners: b.setup.summoners,
    First: b.setup.first,
  })
    .map(([k, v]) => `<div class="kv"><span class="k">${k}</span><span class="v">${esc(v)}</span></div>`)
    .join("");

  return `<div class="bar">
      <div>
        <p class="eyebrow">Matchup brief · ${esc(me.name)} into ${esc(opp.name)}</p>
        <p class="subnote">Read this on the loading screen. Nothing here needs a decision
          in the next thirty seconds.</p>
      </div>
      <div class="bar-cta"><button class="ghost" data-act="after">Game over →</button></div>
    </div>
    <p class="headline">${b.headline}</p>
    <section class="sect"><h3>The lane</h3><ul class="beats">${beats}</ul></section>
    <section class="sect"><h3>Kill windows</h3>
      <div class="windows">
        <div class="win mine"><h4>You can kill him</h4>
          <ul>${b.youKill.map((x) => `<li>${esc(x)}</li>`).join("")}</ul></div>
        <div class="win theirs"><h4>He can kill you</h4>
          <ul>${b.theyKill.map((x) => `<li>${esc(x)}</li>`).join("")}</ul></div>
      </div></section>
    <div class="two">
      <section class="sect"><h3>Their jungler</h3><p class="para">${b.jungle}</p></section>
      <section class="sect"><h3>Your job in this comp</h3><p class="para">${b.comp}</p></section>
    </div>
    <section class="sect"><h3>Setup</h3>
      <div class="setup">${setup}</div>
      <p class="para">${esc(b.setupNote)}</p></section>
    <section class="sect"><h3>What you wrote last time</h3>
      ${noteList(notesFor(picked, foe), "Nothing saved for this matchup yet. Write a line after the game and it shows up here next time.")}
    </section>`;
}

/* ── after game ──────────────────────────────────────────────────────── */

export function afterView(): string {
  const picked = state.picked;
  const foe = laneOpponent();
  if (!picked || !foe) return `<div class="notice"><h3>No game to record</h3></div>`;

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

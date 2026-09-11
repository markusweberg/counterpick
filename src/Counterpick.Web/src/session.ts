/**
 * The live session: draft updates from the League client, scoring from Claude, and the
 * transitions between the three phases. main.ts owns rendering and events; this owns
 * the rules for when to call the host and what to do with the answer.
 */

import { call, callOr, isHosted, on } from "./bridge";
import { setCatalog } from "./champions";
import { fetchCatalog } from "./ddragon";
import {
  assignRole, emptyDraft, LANE_SETTLED, laneOpponent, laneSettled, noteKey, roleFixed, setRole, state,
} from "./state";
import type {
  AppConfigView, Brief, CatalogChampion, ClientStatus, DraftSummary, GameRoles, LiveDraft, NoteRecord,
  PickRef, Role, RoleInference, ShortlistResponse,
} from "./types";

let rerender: () => void = () => {};

export function bindRender(fn: () => void): void {
  rerender = fn;
}

/* ── boot ────────────────────────────────────────────────────────────── */

/** Load config, pool and catalog, then attach to whatever the client is doing. */
export async function boot(): Promise<void> {
  if (!isHosted) {
    // Browser preview: no host, but the real champion list is still worth having.
    await loadCatalog();
    rerender();
    return;
  }

  const config = await callOr<AppConfigView | null>("config.get", null);
  if (config) {
    state.config = config;
    setRole(config.primaryRole);
  }

  await Promise.all([loadPool(), loadCatalog()]);

  on("client.status", (p) => applyClientStatus(p as ClientStatus));
  on("draft.changed", (p) => applyLiveDraft(p as LiveDraft));
  on("draft.ended", () => endLiveDraft());
  on("game.roles", (p) => applyGameRoles(p as GameRoles));

  // The watcher may have been running for a while; catch up on its current view.
  const snapshot = await callOr<{ status: ClientStatus; draft: LiveDraft | null } | null>(
    "draft.subscribe", null,
  );
  if (snapshot) {
    applyClientStatus(snapshot.status);
    if (snapshot.draft) applyLiveDraft(snapshot.draft);
  }
  rerender();
}

/**
 * Load the pool for the role being scored. An autofilled role with no pool of its own
 * borrows the primary role's pool: a ranked list of champions you actually play beats
 * "your pool is empty" with 27 seconds on the clock.
 */
export async function loadPool(): Promise<void> {
  const role = state.role;
  state.poolLoading = true;
  const fetch = (r: Role) => callOr<{ championKey: string }[]>("pool.get", [], { role: r });

  let rows = await fetch(role);
  let from = role;
  const primary = state.config?.primaryRole;
  if (rows.length === 0 && primary && primary !== role) {
    rows = await fetch(primary);
    from = primary;
  }

  if (state.role !== role) return; // the role moved on while we waited; that load wins
  state.pool = rows.map((r) => r.championKey);
  state.poolRole = rows.length > 0 ? from : role;
  state.poolLoading = false;
}

export async function loadCatalog(): Promise<void> {
  if (!isHosted) {
    try {
      setCatalog(await fetchCatalog());
    } catch (e) {
      console.warn("Data Dragon catalog unavailable", e);
    }
    return;
  }
  const res = await callOr<{ version: string | null; champions: CatalogChampion[] } | null>(
    "champions.list", null,
  );
  if (res) setCatalog(res.champions);
}

export async function reloadConfig(): Promise<void> {
  state.config = await callOr<AppConfigView | null>("config.get", state.config);
}

/* ── client events ───────────────────────────────────────────────────── */

export function applyClientStatus(status: ClientStatus): void {
  state.client = status;

  // Game over: move to the after-game view so the note box is waiting when you tab back.
  if ((status.phase === "EndOfGame" || status.phase === "PreEndOfGame") &&
      state.picked && state.phase === "locked") {
    state.phase = "after";
  }
  rerender();
}

export function applyLiveDraft(d: LiveDraft): void {
  const fresh = !state.live;
  if (fresh) {
    // A fresh champ select. Whatever the last game left behind is done with.
    state.live = true;
    state.phase = "draft";
    state.picked = null;
    state.result = null;
    state.selected = null;
    state.recommendations = [];
    state.scoredFor = null;
    state.scoring = "idle";
    state.roleSource = {};
    state.roleConfidence = {};
    state.screen = "session";
    state.assignedRole = null;
    state.autofilled = false;
  }

  // Follow the role the client gave you. Positions can swap during the draft, so this
  // is checked on every update, but only a change from what the client last said acts,
  // which leaves a role you set by hand in the meantime alone. When the client says
  // nothing (blind pick, custom games) the Settings role is the default - including
  // right after a draft that autofilled you elsewhere.
  if (fresh || d.yourRole !== state.assignedRole) {
    state.assignedRole = d.yourRole;
    followRole(d.yourRole ?? state.config?.primaryRole ?? state.role);
  }
  state.autofilled = d.autofilled;

  // Enemy roles, by precedence: the game's, yours, the client's, then the last placement
  // for a champion still on the board - kept so the board does not blink while the new
  // placement is worked out below.
  const locked = new Set(d.enemy.filter((s) => s.championKey && !s.hovering).map((s) => s.championKey!));
  const roles: Record<string, Role> = {};
  const sources: typeof state.roleSource = {};
  const confidence: typeof state.roleConfidence = {};
  for (const [key, role] of Object.entries(state.draft.enemyRoles)) {
    const source = state.roleSource[key];
    if (locked.has(key) && source && source !== "client") {
      roles[key] = role;
      sources[key] = source;
      confidence[key] = state.roleConfidence[key] ?? 0;
    }
  }
  for (const [key, role] of Object.entries(d.enemyRoles)) {
    if (sources[key] === "user" || sources[key] === "game") continue;
    roles[key] = role;
    sources[key] = "client";
    confidence[key] = 1;
  }
  state.draft = {
    ally: d.ally,
    enemy: d.enemy,
    enemyRoles: roles,
    bans: d.bans,
    enemyPicksRemaining: d.enemyPicksRemaining,
  };
  state.roleSource = sources;
  state.roleConfidence = confidence;

  state.timerMs = d.timerMs;
  state.timerAt = Date.now();
  state.timerInfinite = d.timerInfinite;
  state.timerPhase = d.timerPhase;
  state.yourTurn = d.yourTurn;
  state.hoverKey = d.hoverKey;

  // The client's lock is the truth. Follow it into the brief, and re-lock when the app
  // was locked by hand on a different champion than the one that actually went through.
  if (d.lockedKey && (state.phase === "draft" || (state.phase === "locked" && state.picked !== d.lockedKey))) {
    lockIn(d.lockedKey);
  }
  // Place the enemy side afresh; that ends in a re-score, or in the brief once locked.
  void inferRoles();
  rerender();
}

/* ── enemy roles ─────────────────────────────────────────────────────── */

let inferSeq = 0;

/**
 * Place every locked enemy champion from play rates, honouring the roles the game, you
 * or the client already fixed. Local and instant, so it runs on every pick; the answer
 * gets surer as the enemy side fills in, and a guess an earlier pick got wrong is
 * revised by a later one. Ends in whatever the new roles call for: a re-score during
 * the draft, the brief once you have locked.
 */
async function inferRoles(rescoreDelayMs = 1200): Promise<void> {
  const seq = ++inferSeq;
  const enemies = state.draft.enemy
    .filter((s) => s.championKey && !s.hovering)
    .map((s) => {
      const key = s.championKey!;
      return roleFixed(key) ? { championKey: key, role: state.draft.enemyRoles[key] } : { championKey: key };
    });

  if (enemies.length > 0 && isHosted) {
    try {
      const res = await call<RoleInference>("roles.infer", { enemies });
      if (seq !== inferSeq) return; // a newer draft came in while we waited
      for (const [key, role] of Object.entries(res.roles)) {
        if (roleFixed(key)) continue;
        state.draft.enemyRoles[key] = role;
        state.roleSource[key] = "inferred";
        state.roleConfidence[key] = res.confidence[key] ?? 0;
      }
    } catch (e) {
      // No table at all. The board keeps "Role?" and the dropdown still works.
      console.warn("roles.infer failed", e);
      if (seq !== inferSeq) return;
    }
  }
  afterRolesChanged(rescoreDelayMs);
}

/** The roles moved; whatever depends on them is redone. */
function afterRolesChanged(rescoreDelayMs = 1200): void {
  if (state.phase === "locked" && state.picked) briefIfSettled(state.picked);
  else if (state.phase === "draft") scheduleRescore(rescoreDelayMs);
  rerender();
}

/** You set a role on the board. It is fixed from here; the rest are placed around it. */
export function userAssignedRole(championKey: string, role: Role): void {
  assignRole(championKey, role);
  void inferRoles(0);
}

/**
 * The running game says who plays where. This is the truth: it replaces every guess,
 * and if it moves the lane opponent, the brief is written for the right one.
 */
export function applyGameRoles(g: GameRoles): void {
  const onBoard = new Set(state.draft.enemy.filter((s) => s.championKey && !s.hovering).map((s) => s.championKey!));
  let applied = 0;
  for (const [key, role] of Object.entries(g.enemyRoles)) {
    if (!onBoard.has(key)) continue;
    state.draft.enemyRoles[key] = role;
    state.roleSource[key] = "game";
    state.roleConfidence[key] = 1;
    applied++;
  }
  // A game the board knows nothing about (the app opened mid-game) changes nothing.
  if (applied > 0) afterRolesChanged();
}

export function endLiveDraft(): void {
  // The session object is gone because the game is starting (or someone dodged). The
  // board stays: it is what the brief is about.
  state.live = false;
  state.yourTurn = false;
  rerender();
}

/* ── phases ──────────────────────────────────────────────────────────── */

export function lockIn(championKey: string): void {
  state.picked = championKey;
  state.selected = championKey;
  state.phase = "locked";
  if (isHosted) briefIfSettled(championKey);
  rerender();
}

/**
 * Load the notes for the lane, and write the brief once the lane is settled. A brief
 * about the wrong matchup is worse than a late one, so an unsure placement waits for
 * the game to confirm who is where; that arrives on the loading screen, which is when
 * the brief is read anyway. A sure one is written straight away.
 */
function briefIfSettled(championKey: string): void {
  const foe = laneOpponent();
  if (!foe) return;
  void loadNotes(championKey, foe);
  if (laneSettled()) void ensureBrief(championKey, foe);
}

/** Back to an empty draft, by hand. The client does the same thing on its own. */
export function newDraft(): void {
  state.phase = "draft";
  state.picked = null;
  state.result = null;
  if (!state.live) {
    // The last draft's assignment is over with; back to the Settings role.
    state.assignedRole = null;
    state.autofilled = false;
    followRole(state.config?.primaryRole ?? state.role);
    state.draft = emptyDraft(state.role);
    state.recommendations = [];
    state.scoredFor = null;
    state.selected = null;
    state.roleSource = {};
    state.roleConfidence = {};
  }
  rerender();
}

export async function loadNotes(championKey: string, opponentKey: string): Promise<void> {
  const notes = await callOr<NoteRecord[]>("notes.get", [], {
    championKey, opponentKey, role: state.role,
  });
  state.notes[noteKey(championKey, opponentKey, state.role)] = notes.map((n) => ({
    ...n,
    createdAt: n.createdAt.slice(0, 10),
  }));
  rerender();
}

/* ── scoring ─────────────────────────────────────────────────────────── */

/** A placement too unsure to state as fact; the model is told so. */
function guessed(championKey: string): boolean {
  return !roleFixed(championKey) && (state.roleConfidence[championKey] ?? 0) < LANE_SETTLED;
}

/** Locked picks on one side. Enemy roles are "?" where nobody has placed them yet. */
function picks(side: "ally" | "enemy"): PickRef[] {
  return state.draft[side]
    .filter((s) => s.championKey && !s.hovering && !s.isYou)
    .map((s) => {
      const key = s.championKey!;
      if (side === "ally") return { championKey: key, role: s.role };
      const role = state.draft.enemyRoles[key];
      return role
        ? { championKey: key, role, guessed: guessed(key) }
        : { championKey: key, role: "?" as Role };
    });
}

export function draftSummary(): DraftSummary {
  const foe = laneOpponent();
  return {
    laneOpponent: foe ? { championKey: foe, role: state.role, guessed: guessed(foe) } : null,
    enemyPicks: picks("enemy"),
    allyPicks: picks("ally"),
    enemyPicksRemaining: state.draft.enemyPicksRemaining,
    bans: state.draft.bans,
  };
}

/** Champions from your pool that can still be picked. Your own seat never counts against you. */
export function availablePool(): string[] {
  const gone = new Set<string | null>([
    ...state.draft.bans,
    ...state.draft.ally.filter((s) => !s.isYou && !s.hovering).map((s) => s.championKey),
    ...state.draft.enemy.filter((s) => !s.hovering).map((s) => s.championKey),
  ]);
  return state.pool.filter((k) => !gone.has(k) || k === state.picked);
}

/** Everything a score depends on. Hovers are deliberately not in it. */
function scoringFingerprint(): string {
  const d = draftSummary();
  const list = (p: PickRef[]) => p.map((x) => `${x.championKey}:${x.role}`).sort().join(",");
  return [
    state.role, list(d.enemyPicks), list(d.allyPicks), d.enemyPicksRemaining, availablePool().join(","),
  ].join("|");
}

/** Why scoring cannot run right now, or null if it can. */
export function scoringBlocker(): "key" | "pool" | "enemy" | null {
  if (!state.config?.hasApiKey) return "key";
  if (state.poolLoading || availablePool().length === 0) return "pool";
  if (picks("enemy").length === 0) return "enemy";
  return null;
}

let rescoreTimer: ReturnType<typeof setTimeout> | null = null;

/**
 * Draft updates arrive in bursts - a lock, then the next seat going on the clock a
 * moment later. Wait for the burst to settle before spending a call on it.
 */
export function scheduleRescore(delayMs = 1200): void {
  if (rescoreTimer) clearTimeout(rescoreTimer);
  rescoreTimer = setTimeout(() => {
    rescoreTimer = null;
    void rescore();
  }, delayMs);
}

export async function rescore(force = false): Promise<void> {
  if (!isHosted) return;
  if (state.phase !== "draft") return;
  if (scoringBlocker()) {
    state.scoring = "idle";
    return;
  }

  const fp = scoringFingerprint();
  // A failed call is not retried until the inputs change or you ask; otherwise every
  // client event would re-fire the same doomed request.
  if (!force && fp === state.scoredFor) return;
  state.scoredFor = fp;
  state.scoring = "loading";
  state.scoringError = null;
  rerender();

  try {
    const res = await call<ShortlistResponse>("recs.request", {
      role: state.role, championKeys: availablePool(), draft: draftSummary(),
    });

    if (state.scoredFor !== fp) return; // a newer draft came in while we waited

    state.recommendations = res.recommendations;
    state.records = res.records;
    if (!state.selected || !res.recommendations.some((r) => r.championKey === state.selected)) {
      state.selected = res.recommendations[0]?.championKey ?? null;
    }
    state.scoring = "idle";

    // Prefetch: the brief for the top candidates is probably the one you will read -
    // but only once the lane is settled, or it is a brief about the wrong matchup.
    const foe = laneOpponent();
    if (foe && laneSettled()) for (const r of res.recommendations.slice(0, 2)) void ensureBrief(r.championKey, foe);
  } catch (e) {
    if (state.scoredFor !== fp) return;
    state.scoring = "error";
    state.scoringError = e instanceof Error ? e.message : String(e);
  }
  rerender();
}

/* ── briefs ──────────────────────────────────────────────────────────── */

export async function ensureBrief(championKey: string, opponentKey: string, force = false): Promise<void> {
  if (!isHosted) return;
  const key = noteKey(championKey, opponentKey, state.role);
  const existing = state.briefs[key];
  if (!force && existing && existing.status !== "error") return;

  state.briefs[key] = { status: "loading" };
  rerender();
  try {
    const res = await call<{ brief: Brief; cached: boolean }>("brief.request", {
      championKey, opponentKey, role: state.role, draft: draftSummary(), force,
    });
    state.briefs[key] = { status: "ready", brief: res.brief, cached: res.cached };
  } catch (e) {
    state.briefs[key] = { status: "error", error: e instanceof Error ? e.message : String(e) };
  }
  rerender();
}

/* ── roles ───────────────────────────────────────────────────────────── */

/** Score a different role from here on: its pool, its lane opponent, a fresh shortlist. */
function followRole(role: Role): void {
  if (role === state.role && !state.poolLoading) return;
  setRole(role);
  state.recommendations = [];
  state.selected = null;
  state.scoredFor = null;
  state.scoring = "idle";
  void loadPool().then(() => {
    // A swap after you locked changes which brief you need.
    if (state.phase === "locked" && state.picked) lockIn(state.picked);
    else scheduleRescore(0);
    rerender();
  });
}

/** The Settings role: the default for drafts where the client does not assign one. */
export async function changeRole(role: Role): Promise<void> {
  if (state.config) state.config.primaryRole = role;
  await callOr("config.setRole", null, { role });
  setRole(role);
  await loadPool();
  state.scoredFor = null;
  scheduleRescore(0);
  rerender();
}

/* ── clock ───────────────────────────────────────────────────────────── */

/** Milliseconds left on the client's pick clock, counted down locally between updates. */
export function timerRemaining(): number {
  if (!state.live) return 0;
  return Math.max(0, state.timerMs - (Date.now() - state.timerAt));
}

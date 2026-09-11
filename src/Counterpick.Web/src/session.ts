/**
 * The live session: draft updates from the League client, scoring from Claude, and the
 * transitions between the three phases. main.ts owns rendering and events; this owns
 * the rules for when to call the host and what to do with the answer.
 */

import { call, callOr, isHosted, on } from "./bridge";
import { setCatalog } from "./champions";
import { laneOpponent, noteKey, setRole, state, emptyDraft } from "./state";
import type {
  AppConfigView, Brief, CatalogChampion, ClientStatus, DraftSummary, LiveDraft, NoteRecord, PickRef,
  Role, ShortlistResponse,
} from "./types";

let rerender: () => void = () => {};

export function bindRender(fn: () => void): void {
  rerender = fn;
}

/* ── boot ────────────────────────────────────────────────────────────── */

/** Load config, pool and catalog, then attach to whatever the client is doing. */
export async function boot(): Promise<void> {
  if (!isHosted) return;

  const config = await callOr<AppConfigView | null>("config.get", null);
  if (config) {
    state.config = config;
    setRole(config.primaryRole);
  }

  await Promise.all([loadPool(), loadCatalog()]);

  on("client.status", (p) => applyClientStatus(p as ClientStatus));
  on("draft.changed", (p) => applyLiveDraft(p as LiveDraft));
  on("draft.ended", () => endLiveDraft());

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

export async function loadPool(): Promise<void> {
  const rows = await callOr<{ championKey: string }[]>("pool.get", [], { role: state.role });
  state.pool = rows.map((r) => r.championKey);
}

export async function loadCatalog(): Promise<void> {
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
  if (!state.live) {
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
    state.screen = "session";
  }

  // Enemy roles, by precedence: yours, then the client's, then Claude's last guess for
  // a champion still on the board. Anything else is unknown until the next re-score.
  const locked = new Set(d.enemy.filter((s) => s.championKey && !s.hovering).map((s) => s.championKey!));
  const roles: Record<string, Role> = {};
  const sources: typeof state.roleSource = {};
  for (const [key, role] of Object.entries(state.draft.enemyRoles)) {
    const source = state.roleSource[key];
    if (locked.has(key) && (source === "user" || source === "claude")) {
      roles[key] = role;
      sources[key] = source;
    }
  }
  for (const [key, role] of Object.entries(d.enemyRoles)) {
    if (sources[key] === "user") continue;
    roles[key] = role;
    sources[key] = "client";
  }
  state.draft = {
    ally: d.ally,
    enemy: d.enemy,
    enemyRoles: roles,
    bans: d.bans,
    enemyPicksRemaining: d.enemyPicksRemaining,
  };
  state.roleSource = sources;

  state.timerMs = d.timerMs;
  state.timerAt = Date.now();
  state.timerInfinite = d.timerInfinite;
  state.timerPhase = d.timerPhase;
  state.yourTurn = d.yourTurn;
  state.hoverKey = d.hoverKey;

  if (d.lockedKey && state.phase === "draft") {
    lockIn(d.lockedKey);
  } else {
    scheduleRescore();
  }
  rerender();
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
  const foe = laneOpponent();
  if (foe && isHosted) {
    void ensureBrief(championKey, foe);
    void loadNotes(championKey, foe);
  }
  rerender();
}

/** Back to an empty draft, by hand. The client does the same thing on its own. */
export function newDraft(): void {
  state.phase = "draft";
  state.picked = null;
  state.result = null;
  if (!state.live) {
    state.draft = emptyDraft(state.role);
    state.recommendations = [];
    state.scoredFor = null;
    state.selected = null;
    state.roleSource = {};
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

/** Locked picks on one side. Enemy roles are "?" where nobody has settled them yet. */
function picks(side: "ally" | "enemy"): PickRef[] {
  return state.draft[side]
    .filter((s) => s.championKey && !s.hovering && !s.isYou)
    .map((s) => ({
      championKey: s.championKey!,
      role: side === "enemy"
        ? (state.draft.enemyRoles[s.championKey!] ?? ("?" as Role))
        : s.role,
    }));
}

export function draftSummary(): DraftSummary {
  const foe = laneOpponent();
  return {
    laneOpponent: foe ? { championKey: foe, role: state.role } : null,
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
  if (availablePool().length === 0) return "pool";
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

    // Claude's reading of the enemy side fills whatever the client and you left open.
    for (const [key, role] of Object.entries(res.enemyRoles)) {
      const source = state.roleSource[key];
      if (source === "user" || source === "client") continue;
      if (!state.draft.enemy.some((s) => s.championKey === key && !s.hovering)) continue;
      state.draft.enemyRoles[key] = role;
      state.roleSource[key] = "claude";
    }

    state.recommendations = res.recommendations;
    state.records = res.records;
    if (!state.selected || !res.recommendations.some((r) => r.championKey === state.selected)) {
      state.selected = res.recommendations[0]?.championKey ?? null;
    }
    state.scoring = "idle";

    // Prefetch: the brief for the top candidates is probably the one you will read.
    const foe = laneOpponent();
    if (foe) for (const r of res.recommendations.slice(0, 2)) void ensureBrief(r.championKey, foe);
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

export async function changeRole(role: Role): Promise<void> {
  setRole(role);
  await callOr("config.setRole", null, { role });
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

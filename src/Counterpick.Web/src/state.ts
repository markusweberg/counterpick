import type {
  AppConfigView, BackupInfo, BackupStatus, Brief, ClientStatus, DraftState, NoteRecord, Phase,
  Recommendation, Role, RoleSource, StoredNote,
} from "./types";
import { ROLES } from "./types";
import { isHosted } from "./bridge";
import { INITIAL_DRAFT, RECOMMENDATIONS, SEED_NOTES, SEED_RECORDS } from "./data/scenario";

/** Which screen is showing. The three phases live inside "session". */
export type Screen = "session" | "data" | "settings";

export type ScoringStatus = "idle" | "loading" | "error";

/** A brief in flight, arrived, or failed. Keyed by matchup in state.briefs. */
export type BriefSlot =
  | { status: "loading" }
  | { status: "ready"; brief: Brief; cached: boolean }
  | { status: "error"; error: string };

/** Everything the Data panel needs. Loaded from the host, never mocked. */
export interface DataState {
  loading: boolean;
  error: string | null;
  status: BackupStatus | null;
  snapshots: BackupInfo[];
  notes: StoredNote[];
  /** Note being edited inline. */
  editing: number | null;
  /** Second click confirms; avoids a modal dialog. */
  confirmDelete: number | null;
  confirmRestore: string | null;
  flash: string | null;
  flashError: boolean;
}

export interface SettingsState {
  /** Which role's pool is being edited. */
  poolRole: Role;
  poolDraft: string[];
  search: string;
  flash: string | null;
  flashError: boolean;
  confirmClear: boolean;
}

export interface AppState {
  screen: Screen;
  data: DataState;
  settings: SettingsState;
  config: AppConfigView | null;
  phase: Phase;
  /**
   * The role being scored; decides which enemy pick is your lane opponent. Starts as the
   * Settings role and follows whatever the client assigns you for the length of a draft.
   */
  role: Role;
  /** The role the client assigned you in the current draft, if it said. */
  assignedRole: Role | null;
  /** The client put you in a role you did not queue for. */
  autofilled: boolean;
  /** Your champion pool being scored. */
  pool: string[];
  /**
   * Which role's pool that is. Normally `role`; when the assigned role has no pool the
   * primary role's pool stands in, so an autofill still gets a ranked shortlist.
   */
  poolRole: Role;
  poolLoading: boolean;
  /** Champion currently being considered in draft. Null until there is something to consider. */
  selected: string | null;
  /** Champion you locked in. Null until you do. */
  picked: string | null;
  result: "win" | "loss" | null;
  draft: DraftState;
  /**
   * Where each entry in draft.enemyRoles came from. Your own choices win over the client,
   * the client wins over Claude, and Claude's guesses are re-asked on every re-score.
   */
  roleSource: Record<string, RoleSource>;
  recommendations: Recommendation[];
  scoring: ScoringStatus;
  scoringError: string | null;
  /** Fingerprint of the inputs the current recommendations were scored on. */
  scoredFor: string | null;
  briefs: Record<string, BriefSlot>;
  notes: Record<string, NoteRecord[]>;
  records: Record<string, { wins: number; losses: number }>;
  /** True while a draft from the League client is on screen. */
  live: boolean;
  client: ClientStatus;
  /** Pick clock as last reported, and when. The topbar counts down from there. */
  timerMs: number;
  timerAt: number;
  timerInfinite: boolean;
  timerPhase: string;
  yourTurn: boolean;
  /** Your own hover, before you lock. */
  hoverKey: string | null;
}

export const emptyDataState = (): DataState => ({
  loading: false,
  error: null,
  status: null,
  snapshots: [],
  notes: [],
  editing: null,
  confirmDelete: null,
  confirmRestore: null,
  flash: null,
  flashError: false,
});

export const emptySettingsState = (role: Role): SettingsState => ({
  poolRole: role,
  poolDraft: [],
  search: "",
  flash: null,
  flashError: false,
  confirmClear: false,
});

/** Five empty seats a side, with your seat marked. */
export function emptyDraft(role: Role): DraftState {
  return {
    ally: ROLES.map((r) => ({ championKey: null, role: r, isYou: r === role })),
    enemy: ROLES.map((r) => ({ championKey: null, role: r })),
    enemyRoles: {},
    bans: [],
    enemyPicksRemaining: 5,
  };
}

// In a plain browser there is no host, so the worked example stands in for a draft.
// Inside the app everything starts empty and fills from the client and from Claude.
const example = !isHosted;

export const state: AppState = {
  screen: "session",
  data: emptyDataState(),
  settings: emptySettingsState("Top"),
  config: null,
  phase: "draft",
  role: "Top",
  assignedRole: null,
  autofilled: false,
  pool: example ? RECOMMENDATIONS.map((r) => r.championKey) : [],
  poolRole: "Top",
  poolLoading: false,
  selected: example ? RECOMMENDATIONS[0]!.championKey : null,
  picked: null,
  result: null,
  draft: example ? structuredClone(INITIAL_DRAFT) : emptyDraft("Top"),
  roleSource: example ? { Darius: "client", Nidalee: "client" } : {},
  recommendations: example ? RECOMMENDATIONS : [],
  scoring: "idle",
  scoringError: null,
  scoredFor: null,
  briefs: {},
  notes: example ? structuredClone(SEED_NOTES) : {},
  records: example ? structuredClone(SEED_RECORDS) : {},
  live: false,
  client: { connected: false, phase: "Offline" },
  timerMs: 0,
  timerAt: 0,
  timerInfinite: false,
  timerPhase: "",
  yourTurn: false,
  hoverKey: null,
};

/** Whichever enemy champion is assigned to your role. Null if none is. */
export function laneOpponent(): string | null {
  const found = Object.entries(state.draft.enemyRoles).find(([, r]) => r === state.role);
  return found ? found[0] : null;
}

export function recommendationFor(championKey: string): Recommendation | undefined {
  return state.recommendations.find((r) => r.championKey === championKey);
}

/** The champion the hero banner shows: your lock, else your pick, else your hover. */
export function heroChampion(): string | null {
  return state.picked ?? state.selected ?? state.hoverKey;
}

export function noteKey(championKey: string, opponentKey: string, role: Role): string {
  return `${championKey}|${opponentKey}|${role}`;
}

export function notesFor(championKey: string, opponentKey: string | null): NoteRecord[] {
  if (!opponentKey) return [];
  return state.notes[noteKey(championKey, opponentKey, state.role)] ?? [];
}

/** The brief for a matchup, from the live cache or from the worked example. */
export function briefFor(championKey: string, opponentKey: string | null): BriefSlot | null {
  if (!opponentKey) return null;
  const live = state.briefs[noteKey(championKey, opponentKey, state.role)];
  if (live) return live;
  const example = recommendationFor(championKey)?.brief;
  return example ? { status: "ready", brief: example, cached: true } : null;
}

export function addNote(championKey: string, opponentKey: string, body: string): void {
  const key = noteKey(championKey, opponentKey, state.role);
  const list = state.notes[key] ?? (state.notes[key] = []);
  const nextId = Math.max(0, ...Object.values(state.notes).flat().map((n) => n.id)) + 1;
  list.unshift({ id: nextId, body, createdAt: new Date().toISOString().slice(0, 10) });
  // The brief that predates this note is stale; the host's cache knows the same thing.
  delete state.briefs[key];
}

/**
 * Reassigning a role swaps with whoever held it, rather than leaving two champions
 * claiming the same lane.
 */
export function assignRole(championKey: string, role: Role): void {
  const previous = state.draft.enemyRoles[championKey];
  const clash = Object.keys(state.draft.enemyRoles).find(
    (k) => k !== championKey && state.draft.enemyRoles[k] === role,
  );
  if (clash && previous) {
    state.draft.enemyRoles[clash] = previous;
    state.roleSource[clash] = "user";
  } else if (clash) {
    // The other champion's role was a guess and you just took it; leave them unassigned.
    delete state.draft.enemyRoles[clash];
    delete state.roleSource[clash];
  }
  state.draft.enemyRoles[championKey] = role;
  state.roleSource[championKey] = "user";
}

/** Changing your role moves your seat on an empty board and re-reads the lane opponent. */
export function setRole(role: Role): void {
  state.role = role;
  if (!state.live) state.draft = emptyDraft(role);
  state.settings.poolRole = role;
}

/** The pool being scored is a stand-in from another role. */
export function poolBorrowed(): boolean {
  return state.poolRole !== state.role;
}

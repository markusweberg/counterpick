import type { DraftState, NoteRecord, Phase, Recommendation, Role } from "./types";
import { INITIAL_DRAFT, RECOMMENDATIONS, SEED_NOTES, SEED_RECORDS } from "./data/scenario";

export interface AppState {
  phase: Phase;
  /** The role you queue for; decides which enemy pick is your lane opponent. */
  role: Role;
  /** Champion currently being considered in draft. */
  selected: string;
  /** Champion you locked in. Null until you do. */
  picked: string | null;
  result: "win" | "loss" | null;
  draft: DraftState;
  recommendations: Recommendation[];
  notes: Record<string, NoteRecord[]>;
  records: Record<string, { wins: number; losses: number }>;
  /** False while running on the worked example rather than live client data. */
  live: boolean;
}

export const state: AppState = {
  phase: "draft",
  role: "Top",
  selected: RECOMMENDATIONS[0]!.championKey,
  picked: null,
  result: null,
  draft: structuredClone(INITIAL_DRAFT),
  recommendations: RECOMMENDATIONS,
  notes: structuredClone(SEED_NOTES),
  records: structuredClone(SEED_RECORDS),
  live: false,
};

/** Whichever enemy champion is assigned to your role. Null if none is. */
export function laneOpponent(): string | null {
  const found = Object.entries(state.draft.enemyRoles).find(([, r]) => r === state.role);
  return found ? found[0] : null;
}

export function recommendationFor(championKey: string): Recommendation | undefined {
  return state.recommendations.find((r) => r.championKey === championKey);
}

/** The champion the hero banner shows: your lock if you have one, else your current pick. */
export function heroChampion(): string {
  return state.picked ?? state.selected;
}

export function noteKey(championKey: string, opponentKey: string, role: Role): string {
  return `${championKey}|${opponentKey}|${role}`;
}

export function notesFor(championKey: string, opponentKey: string | null): NoteRecord[] {
  if (!opponentKey) return [];
  return state.notes[noteKey(championKey, opponentKey, state.role)] ?? [];
}

export function addNote(championKey: string, opponentKey: string, body: string): void {
  const key = noteKey(championKey, opponentKey, state.role);
  const list = state.notes[key] ?? (state.notes[key] = []);
  const nextId = Math.max(0, ...Object.values(state.notes).flat().map((n) => n.id)) + 1;
  list.unshift({ id: nextId, body, createdAt: new Date().toISOString().slice(0, 10) });
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
  if (clash && previous) state.draft.enemyRoles[clash] = previous;
  state.draft.enemyRoles[championKey] = role;
}

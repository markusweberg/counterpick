/** Domain model shared by every view. Mirrors the C# records in Services/. */

export type Role = "Top" | "Jungle" | "Mid" | "Bot" | "Support";
export const ROLES: readonly Role[] = ["Top", "Jungle", "Mid", "Bot", "Support"];

export type Verdict = "Favorable" | "Even" | "Difficult" | "Losing";
export type Phase = "draft" | "locked" | "after";

/** A champion as Data Dragon knows it. `key` is the Data Dragon id, e.g. "MonkeyKing". */
export interface Champion {
  key: string;
  name: string;
  klass: string;
  /** background-position for the hero crop; splash art frames each champion differently. */
  splashPos: string;
}

export interface LaneBeat {
  /** "Lv 3", "Bleed", "1st back" - a point in the game, not a sequence number. */
  mark: string;
  /** May contain <strong>. Authored copy, never user input. */
  text: string;
}

export interface Setup {
  keystone: string;
  secondary: string;
  summoners: string;
  first: string;
}

/** What you read once you have locked in. */
export interface Brief {
  headline: string;
  lane: LaneBeat[];
  youKill: string[];
  theyKill: string[];
  jungle: string;
  comp: string;
  setup: Setup;
  setupNote: string;
}

/** One champion from your pool, scored against the current draft. */
export interface Recommendation {
  championKey: string;
  score: number;
  verdict: Verdict;
  /** One line. This is what you actually read during pick phase. */
  why: string;
  /** Two or three fragments under the why line. */
  hints: string[];
  brief: Brief;
}

export interface DraftSlot {
  championKey: string | null;
  role: Role;
  isYou?: boolean;
  onTheClock?: boolean;
}

export interface DraftState {
  ally: DraftSlot[];
  enemy: DraftSlot[];
  /** Enemy champions that have been picked, mapped to the role you believe they play. */
  enemyRoles: Record<string, Role>;
}

export interface NoteRecord {
  id: number;
  body: string;
  createdAt: string;
}

export interface MatchupRecord {
  wins: number;
  losses: number;
}

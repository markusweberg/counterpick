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

/** One row of Data Dragon's champion.json, as `champions.list` returns it. */
export interface CatalogChampion {
  key: string;
  name: string;
  numericId: number;
  tags: string[];
}

export interface LaneBeat {
  /** "Lv 3", "Bleed", "1st back" - a point in the game, not a sequence number. */
  mark: string;
  /** May contain <strong>. Rendered through rich(), which allows nothing else. */
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
  /**
   * Present on the worked example only. Live briefs are a separate, slower call and
   * live in state.briefs, keyed by matchup.
   */
  brief?: Brief;
}

export interface DraftSlot {
  championKey: string | null;
  role: Role;
  isYou?: boolean;
  onTheClock?: boolean;
  /** Shown but not locked. Hovers appear on the board without triggering a re-score. */
  hovering?: boolean;
  /**
   * The role came from the client. False means seat order, a placeholder: in a normal
   * draft the client never assigns enemy positions, so those come from Claude or from you.
   */
  roleKnown?: boolean;
}

/**
 * Where an enemy role on the board came from. "inferred" is the app's placement from
 * play rates, re-done on every pick; "client" is the League client's assignment (never
 * for enemies in a normal draft); "user" is the dropdown; "game" is the running game,
 * which settles it for certain.
 */
export type RoleSource = "client" | "user" | "inferred" | "game";

/** What `roles.infer` returns: the likeliest role for each locked enemy and how sure that is (0-1). */
export interface RoleInference {
  roles: Record<string, Role>;
  confidence: Record<string, number>;
}

/** The `game.roles` payload: positions as the running game reports them, on both sides. */
export interface GameRoles {
  enemyRoles: Record<string, Role>;
  allyRoles: Record<string, Role>;
  yourRole: Role | null;
}

/** What `recs.request` returns. */
export interface ShortlistResponse {
  recommendations: Recommendation[];
  /**
   * The best picks in the role from the whole roster, pool or not, scored on the same
   * scale as `recommendations`. Empty when the play-rate table has not loaded.
   */
  openPicks: Recommendation[];
  records: Record<string, MatchupRecord>;
}

export interface DraftState {
  ally: DraftSlot[];
  enemy: DraftSlot[];
  /** Enemy champions that have been locked, mapped to the role you believe they play. */
  enemyRoles: Record<string, Role>;
  bans: string[];
  /** How many enemy picks are still hidden. */
  enemyPicksRemaining: number;
}

/** The `draft.changed` payload from the League client listener (DraftPayload in C#). */
export interface LiveDraft {
  ally: DraftSlot[];
  enemy: DraftSlot[];
  enemyRoles: Record<string, Role>;
  bans: string[];
  timerMs: number;
  /** Practice tool runs with no clock. */
  timerInfinite: boolean;
  /** "PLANNING", "BAN_PICK", "FINALIZATION", "GAME_STARTING". */
  timerPhase: string;
  yourTurn: boolean;
  lockedKey: string | null;
  hoverKey: string | null;
  enemyPicksRemaining: number;
  /** The role the client assigned you, or null when it did not (blind pick, custom games). */
  yourRole: Role | null;
  /** The client put you somewhere other than the positions you queued for. */
  autofilled: boolean;
}

/** The `client.status` payload. `phase` is the gameflow phase verbatim, or "Offline". */
export interface ClientStatus {
  connected: boolean;
  phase: string;
  message?: string | null;
}

/** A champion on the board as the model should see it. */
export interface PickRef {
  championKey: string;
  role: Role;
  /** The role is an unsettled inference from play rates, not a fact. */
  guessed?: boolean;
}

/** What the page sends with `recs.request` and `brief.request`. */
export interface DraftSummary {
  laneOpponent: PickRef | null;
  enemyPicks: PickRef[];
  allyPicks: PickRef[];
  enemyPicksRemaining: number;
  bans: string[];
}

export interface AppConfigView {
  hasApiKey: boolean;
  /** Empty unless the key is organisation-level and needs the workspace header. */
  workspaceId: string;
  model: string;
  shortlistModel: string;
  primaryRole: Role;
  dataDragonVersion: string | null;
  /** The patch the enemy-role play rates describe, and where they came from ("feed", "cache", "bundled"). */
  roleRatesPatch: string | null;
  roleRatesSource: string;
}

/** Mirrors UpdateService.View(). Pushed as "update.state" whenever it changes. */
export interface UpdateView {
  version: string;
  /** False for a build run from bin/, which cannot be swapped in place. */
  installed: boolean;
  /** The release folder the app watches; null when the build was never packed. */
  source: string | null;
  state: "idle" | "checking" | "up-to-date" | "downloading" | "ready" | "error" | "not-installed";
  available: string | null;
  progress: number;
  error: string | null;
  checkedAt: string | null;
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

/* ── data panel ──────────────────────────────────────────────────────── */

/** A note as stored, including which matchup it belongs to. */
export interface StoredNote {
  id: number;
  championKey: string;
  opponentKey: string;
  role: Role;
  body: string;
  createdAt: string;
  updatedAt: string;
  edited: boolean;
}

export interface BackupInfo {
  path: string;
  takenAt: string;
  bytes: number;
  /** "startup", "shutdown", "manual", "migration", "restore". */
  reason: string;
}

export interface BackupStatus {
  notes: number;
  games: number;
  pool: number;
  lastBackupAt: string | null;
  snapshotCount: number;
  backupsDir: string;
  mirrorDir: string | null;
  mirrorHealthy: boolean;
  /** Full path to counterpick.db. Lives in Documents, so OneDrive syncs it. */
  databaseFile: string;
}

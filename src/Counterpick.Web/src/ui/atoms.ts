import type { Verdict } from "../types";
import { squareUrl } from "../ddragon";
import { champion } from "../champions";

/** Escape anything that is not authored markup. */
export function esc(s: string): string {
  return s.replace(/[&<>"]/g, (c) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;" })[c]!);
}

/**
 * Brief copy may emphasise one phrase with <strong>. It comes back from a model, so
 * everything else is escaped and only that one tag is let through.
 */
export function rich(s: string): string {
  return esc(s).replace(/&lt;(\/?)strong&gt;/g, "<$1strong>");
}

export const VERDICT_CLASS: Record<Verdict, string> = {
  Favorable: "d-fav",
  Even: "d-even",
  Difficult: "d-hard",
  Losing: "d-lose",
};

export const VERDICT_COLOR: Record<Verdict, string> = {
  Favorable: "var(--fav)",
  Even: "var(--even)",
  Difficult: "var(--hard)",
  Losing: "var(--lose)",
};

/** Beveled champion portrait. Empty slot when the key is null. */
export function portrait(championKey: string | null, cls = ""): string {
  if (!championKey) return `<div class="port empty ${cls}">·</div>`;
  const c = champion(championKey);
  return `<div class="port ${cls}"><img src="${squareUrl(c.key)}" alt="${esc(c.name)}" loading="lazy"></div>`;
}

export function recordLabel(record: { wins: number; losses: number } | undefined): string {
  if (!record || record.wins + record.losses === 0) return "no games yet";
  return `${record.wins}–${record.losses} on record`;
}

/** m:ss for the pick clock. */
export function clock(ms: number): string {
  const s = Math.max(0, Math.ceil(ms / 1000));
  return `${Math.floor(s / 60)}:${String(s % 60).padStart(2, "0")}`;
}

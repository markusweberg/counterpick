import type { Verdict } from "../types";
import { squareUrl } from "../ddragon";
import { champion } from "../data/scenario";

/** Escape anything the user typed. Authored copy may contain <strong> and is not escaped. */
export function esc(s: string): string {
  return s.replace(/[&<>"]/g, (c) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;" })[c]!);
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

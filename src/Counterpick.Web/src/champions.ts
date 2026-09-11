/**
 * Champion lookup. Filled from Data Dragon through `champions.list` once the host has
 * the catalog; until then, and in a plain browser, the worked example's handful of
 * champions is all there is.
 */

import { CHAMPIONS as EXAMPLE } from "./data/scenario";
import type { CatalogChampion, Champion } from "./types";

const catalog = new Map<string, Champion>();
let sorted: Champion[] = [];

/** Data Dragon tags are already readable ("Fighter", "Marksman"); show the first one. */
function klassOf(tags: string[]): string {
  return tags[0] ?? "Champion";
}

export function setCatalog(list: CatalogChampion[]): void {
  catalog.clear();
  for (const c of list) {
    // The worked example carries hand-picked splash crops; keep those where they exist.
    const known = EXAMPLE[c.key];
    catalog.set(c.key, {
      key: c.key,
      name: c.name,
      klass: known?.klass ?? klassOf(c.tags),
      splashPos: known?.splashPos ?? "50% 30%",
    });
  }
  sorted = [...catalog.values()].sort((a, b) => a.name.localeCompare(b.name));
}

export function hasCatalog(): boolean {
  return catalog.size > 0;
}

/** Every champion, alphabetical. Empty until the catalog has loaded. */
export function allChampions(): Champion[] {
  return sorted;
}

export function champion(key: string | null): Champion {
  if (key) {
    const hit = catalog.get(key) ?? EXAMPLE[key];
    if (hit) return hit;
  }
  return { key: key ?? "", name: key ?? "Unknown", klass: "Unknown", splashPos: "50% 30%" };
}

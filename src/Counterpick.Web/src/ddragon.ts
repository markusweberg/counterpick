/**
 * Data Dragon - Riot's static CDN for champion metadata and art. No API key.
 *
 * The prototype had to inline every image as a data URI because published artifacts
 * block external images. Inside WebView2 there is no such restriction, so we point at
 * the CDN directly and let the browser cache do the work.
 */

const BASE = "https://ddragon.leagueoflegends.com";

/** Fallback used until the live version resolves; kept current with the repo. */
let version = "16.18.1";

/** Resolve the newest patch. Falls back silently - stale art beats a broken UI. */
export async function initDataDragon(): Promise<string> {
  try {
    const res = await fetch(`${BASE}/api/versions.json`);
    if (!res.ok) return version;
    const versions = (await res.json()) as unknown;
    if (Array.isArray(versions) && typeof versions[0] === "string") {
      version = versions[0];
    }
  } catch {
    // Offline, or League's CDN is having a day. Keep the fallback.
  }
  return version;
}

export function dataDragonVersion(): string {
  return version;
}

/** 120x120 champion portrait, for draft slots and pool cards. */
export function squareUrl(championKey: string): string {
  return `${BASE}/cdn/${version}/img/champion/${championKey}.png`;
}

/** 1215x717 splash, for the hero banner. Skin 0 is the base skin. */
export function splashUrl(championKey: string): string {
  return `${BASE}/cdn/img/champion/splash/${championKey}_0.jpg`;
}

/** Tall portrait, unused today but the natural art for a compact list view. */
export function loadingUrl(championKey: string): string {
  return `${BASE}/cdn/img/champion/loading/${championKey}_0.jpg`;
}

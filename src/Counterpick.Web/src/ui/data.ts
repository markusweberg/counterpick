/**
 * Data & backups panel.
 *
 * Unlike the session views, nothing here is mocked - every number comes from the host.
 * Running in a plain browser via `npm run dev` there is no host, so the panel says so
 * rather than inventing plausible figures about the safety of your notes.
 */

import { call, isHosted } from "../bridge";
import { state } from "../state";
import type { BackupInfo, StoredNote } from "../types";
import { esc } from "./atoms";

/* ── loading ─────────────────────────────────────────────────────────── */

export async function loadData(): Promise<void> {
  const d = state.data;
  d.loading = true;
  d.error = null;

  try {
    const [status, snapshots, notes] = await Promise.all([
      call<typeof d.status>("backup.status"),
      call<BackupInfo[]>("backup.list"),
      call<StoredNote[]>("notes.all"),
    ]);
    d.status = status;
    d.snapshots = snapshots;
    d.notes = notes;
  } catch (e) {
    d.error = e instanceof Error ? e.message : String(e);
  } finally {
    d.loading = false;
  }
}

/* ── formatting ──────────────────────────────────────────────────────── */

function ago(iso: string | null): string {
  if (!iso) return "never";
  const seconds = (Date.now() - new Date(iso).getTime()) / 1000;
  if (seconds < 90) return "just now";
  if (seconds < 3600) return `${Math.round(seconds / 60)} min ago`;
  if (seconds < 86400) return `${Math.round(seconds / 3600)} h ago`;
  return `${Math.round(seconds / 86400)} d ago`;
}

function day(iso: string): string {
  return new Date(iso).toISOString().slice(0, 10);
}

function stamp(iso: string): string {
  const d = new Date(iso);
  return `${d.toISOString().slice(0, 10)} ${d.toTimeString().slice(0, 5)}`;
}

function size(bytes: number): string {
  return bytes < 1024 * 1024
    ? `${Math.max(1, Math.round(bytes / 1024))} KB`
    : `${(bytes / 1024 / 1024).toFixed(1)} MB`;
}

/* ── view ────────────────────────────────────────────────────────────── */

export function dataView(): string {
  const d = state.data;

  const head = `<div class="bar page-bar">
      <div>
        <p class="eyebrow">Counterpick · this machine</p>
        <h2 class="page-title">Data &amp; backups</h2>
        <p class="subnote">Notes and results are the only thing here that cannot be regenerated.
          Briefs are cached separately and are safe to lose.</p>
      </div>
      <div class="bar-cta"><button class="ghost" data-act="back">← Back to draft</button></div>
    </div>`;

  if (!isHosted) {
    return `${head}<div class="notice quiet">
      <h3>Not running inside Counterpick</h3>
      <p>This panel reports on the real database, so it only works in the app itself.
         In the browser dev server there is no host to ask.</p>
      <p>Build and launch with <code>dotnet run --project src/Counterpick.App</code>.</p>
    </div>`;
  }

  if (d.loading && !d.status) return `${head}<p class="subnote">Reading the database…</p>`;

  if (d.error) {
    return `${head}<div class="notice warn">
      <h3>Could not read the database</h3>
      <p>${esc(d.error)}</p>
      <button class="ghost" data-act="reload">Try again</button>
    </div>`;
  }

  const s = d.status;
  if (!s) return `${head}<p class="subnote">No status available.</p>`;

  const flash = d.flash
    ? `<p class="flash ${d.flashError ? "bad" : "good"}">${esc(d.flash)}</p>`
    : "";

  const mirror = s.mirrorDir === null
    ? `<span class="pill warn">Not configured</span>`
    : s.mirrorHealthy
      ? `<span class="pill ok">Mirrored</span>`
      : `<span class="pill warn">Pending</span>`;

  const stats = `<div class="statgrid">
      <div class="statcard">
        <span class="k">Notes</span><span class="v">${s.notes}</span>
        <span class="n">yours, irreplaceable</span></div>
      <div class="statcard">
        <span class="k">Games</span><span class="v">${s.games}</span>
        <span class="n">recorded results</span></div>
      <div class="statcard">
        <span class="k">Pool</span><span class="v">${s.pool}</span>
        <span class="n">champions</span></div>
      <div class="statcard">
        <span class="k">Last backup</span><span class="v small">${ago(s.lastBackupAt)}</span>
        <span class="n">${s.snapshotCount} snapshot${s.snapshotCount === 1 ? "" : "s"} kept</span></div>
      <div class="statcard">
        <span class="k">Off-machine</span><span class="v small">${mirror}</span>
        <span class="n">${s.mirrorDir ? esc(s.mirrorDir) : "no OneDrive folder found"}</span></div>
    </div>`;

  const actions = `<div class="actions">
      <button class="lock" data-act="backup">Back up now</button>
      <button class="ghost" data-act="export">Export notes</button>
      <button class="ghost" data-act="import">Import from file…</button>
      <button class="ghost" data-act="reveal" data-path="${esc(s.backupsDir)}">Open backups folder</button>
    </div>`;

  return `${head}${flash}${stats}${actions}
    <div class="panels">
      ${snapshotsSection(d.snapshots, d.confirmRestore)}
      ${notesSection(d.notes, d.editing, d.confirmDelete)}
    </div>`;
}

function snapshotsSection(snapshots: BackupInfo[], confirming: string | null): string {
  if (snapshots.length === 0) {
    return `<section class="panel span"><div class="panel-head"><h3>Snapshots</h3></div>
      <p class="empty-note">No snapshots yet. One is taken automatically whenever your
        notes change.</p></section>`;
  }

  const rows = snapshots
    .map((b) => {
      const permanent = b.reason === "migration" || b.reason === "restore";
      const isConfirming = confirming === b.path;
      return `<div class="snaprow">
        <span class="when">${stamp(b.takenAt)}</span>
        <span class="why ${permanent ? "keep" : ""}">${esc(b.reason)}${permanent ? " · kept" : ""}</span>
        <span class="sz">${size(b.bytes)}</span>
        <span class="act">${
          isConfirming
            ? `<button class="mini danger" data-act="restore-go" data-path="${esc(b.path)}">Merge it in</button>
               <button class="mini" data-act="cancel">Cancel</button>`
            : `<button class="mini" data-act="restore" data-path="${esc(b.path)}">Restore</button>`
        }</span>
      </div>`;
    })
    .join("");

  return `<section class="panel span"><div class="panel-head"><h3>Snapshots</h3>
      <span class="panel-meta">${snapshots.length} kept</span></div>
    <p class="subnote">Restoring merges a snapshot back in. It never overwrites, so notes
      written since are kept.</p>
    <div class="snaplist">${rows}</div></section>`;
}

function notesSection(notes: StoredNote[], editing: number | null, confirmDelete: number | null): string {
  if (notes.length === 0) {
    return `<section class="panel span"><div class="panel-head"><h3>Notes</h3></div>
      <p class="empty-note">Nothing written yet. The line you write after a game shows up
        here, grouped by matchup.</p>
    </section>`;
  }

  // Group by matchup, keeping the order the host returned (role, champion, opponent).
  const groups = new Map<string, StoredNote[]>();
  for (const n of notes) {
    const key = `${n.role}|${n.championKey}|${n.opponentKey}`;
    (groups.get(key) ?? groups.set(key, []).get(key)!).push(n);
  }

  const blocks = [...groups.entries()]
    .map(([key, list]) => {
      const [role, champion, opponent] = key.split("|") as [string, string, string];
      const rows = list.map((n) => noteRow(n, editing === n.id, confirmDelete === n.id)).join("");
      return `<div class="notegroup">
        <div class="notegroup-head">
          <span class="mu">${esc(champion)} <em>into</em> ${esc(opponent)}</span>
          <span class="ro">${esc(role)}</span>
          <span class="ct">${list.length}</span>
        </div>
        ${rows}
      </div>`;
    })
    .join("");

  return `<section class="panel span"><div class="panel-head"><h3>Notes</h3>
      <span class="panel-meta">${notes.length} across ${groups.size} matchup${groups.size === 1 ? "" : "s"}</span></div>
    <div class="notegroups">${blocks}</div></section>`;
}

function noteRow(n: StoredNote, isEditing: boolean, isConfirming: boolean): string {
  if (isEditing) {
    return `<form class="noterow editing" data-edit-id="${n.id}">
      <span class="d">${day(n.createdAt)}</span>
      <textarea class="noteedit" name="body" rows="2">${esc(n.body)}</textarea>
      <span class="act">
        <button type="submit" class="mini go">Save</button>
        <button type="button" class="mini" data-act="cancel">Cancel</button>
      </span>
    </form>`;
  }

  return `<div class="noterow">
    <span class="d">${day(n.createdAt)}${n.edited ? '<em class="ed">edited</em>' : ""}</span>
    <p class="b">${esc(n.body)}</p>
    <span class="act">${
      isConfirming
        ? `<button class="mini danger" data-act="delete-go" data-id="${n.id}">Delete it</button>
           <button class="mini" data-act="cancel">Keep</button>`
        : `<button class="mini" data-act="edit" data-id="${n.id}">Edit</button>
           <button class="mini" data-act="delete" data-id="${n.id}">Delete</button>`
    }</span>
  </div>`;
}

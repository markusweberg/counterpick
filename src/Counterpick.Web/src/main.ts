import "./styles.css";

import { call, callOr, isHosted } from "./bridge";
import { initDataDragon } from "./ddragon";
import {
  addNote, emptyDataState, emptySettingsState, laneOpponent, state,
} from "./state";
import {
  bindRender, boot, changeRole, ensureBrief, loadPool, lockIn, newDraft, reloadConfig, rescore,
  timerRemaining, userAssignedRole,
} from "./session";
import type { Phase, Role } from "./types";
import { clock } from "./ui/atoms";
import { board, hero, topbar } from "./ui/chrome";
import { dataView, loadData } from "./ui/data";
import { poolChips, poolGrid, poolGridCount, settingsView } from "./ui/settings";
import { afterView, briefView, draftView } from "./ui/views";

const app = document.getElementById("app")!;

/**
 * The window is frameless and the topbar is its title bar, so the host has to be told how
 * tall that strip is: WindowChrome decides what counts as caption from a number, and the
 * page is the only thing that knows it. The bar wraps to two rows on a narrow window, so
 * this is measured rather than assumed, after every paint and on every resize.
 *
 * Only ever sent when it changes - it is the same number on almost every render.
 */
let sentCaptionHeight = -1;

function syncCaptionHeight(): void {
  if (!isHosted) return;
  const bar = app.querySelector<HTMLElement>(".topbar");
  if (!bar) return;
  const height = Math.round(bar.getBoundingClientRect().height);
  if (height === sentCaptionHeight) return;
  sentCaptionHeight = height;
  void call("window.setCaptionHeight", { height }).catch(() => {});
}

window.addEventListener("resize", () => requestAnimationFrame(syncCaptionHeight));

function render(focusNote = false): void {
  paint(focusNote);
  syncCaptionHeight();
}

function paint(focusNote = false): void {
  // The Data and Settings screens are about the app, not the game, so they drop the
  // hero and the board.
  if (state.screen === "data") {
    app.innerHTML = `<div class="app">${topbar()}<main class="wide">${dataView()}</main></div>`;
    return;
  }
  if (state.screen === "settings") {
    const search = document.getElementById("poolSearch") as HTMLInputElement | null;
    const hadFocus = search !== null && document.activeElement === search;
    const caret = search?.selectionStart ?? 0;
    app.innerHTML = `<div class="app">${topbar()}<main class="wide">${settingsView()}</main></div>`;
    if (hadFocus) {
      const again = document.getElementById("poolSearch") as HTMLInputElement | null;
      again?.focus();
      again?.setSelectionRange(caret, caret);
    }
    return;
  }

  const view =
    state.phase === "draft" ? draftView() : state.phase === "locked" ? briefView() : afterView();

  app.innerHTML = `<div class="app">
    ${topbar()}
    ${hero()}
    ${board()}
    <main>${view}</main>
  </div>`;

  if (focusNote) document.getElementById("noteInput")?.focus();
}

bindRender(() => render());

/**
 * The pool editor redraws only its own pieces while you filter or click, so the search
 * box keeps focus and the rest of the screen stays put.
 */
function refreshPool(chips = true): void {
  const grid = document.getElementById("poolGrid");
  if (!grid) return;
  grid.innerHTML = poolGrid();
  const count = document.getElementById("poolCount");
  if (count) count.textContent = poolGridCount();
  if (chips) {
    const c = document.getElementById("poolChips");
    if (c) c.innerHTML = poolChips();
    const n = state.settings.poolDraft.length;
    const meta = document.querySelector<HTMLElement>(".panel.span .panel-meta");
    if (meta) meta.textContent = n === 0 ? "empty" : `${n} in ${state.settings.poolRole.toLowerCase()}`;
  }
}

/** Run a host call, refresh the panel, and report the outcome in one line. */
async function dataAction(work: () => Promise<string | null>): Promise<void> {
  const d = state.data;
  d.confirmDelete = null;
  d.confirmRestore = null;
  d.editing = null;
  try {
    const message = await work();
    await loadData();
    d.flash = message;
    d.flashError = false;
  } catch (e) {
    // A rejected edit or a locked file is a message, not a reason to tear the panel down.
    d.flash = e instanceof Error ? e.message : String(e);
    d.flashError = true;
  }
  render();
}

/** Same shape for the settings screen. */
async function settingsAction(work: () => Promise<string | null>): Promise<void> {
  const s = state.settings;
  s.confirmClear = false;
  try {
    const message = await work();
    s.flash = message;
    s.flashError = false;
  } catch (e) {
    s.flash = e instanceof Error ? e.message : String(e);
    s.flashError = true;
  }
  render();
}

async function openData(): Promise<void> {
  state.screen = "data";
  state.data = emptyDataState();
  state.data.loading = true;
  render();
  await loadData();
  render();
}

async function openSettings(): Promise<void> {
  state.screen = "settings";
  state.settings = emptySettingsState(state.role);
  await loadPoolDraft();
  render();
}

async function loadPoolDraft(): Promise<void> {
  const role = state.settings.poolRole;
  if (isHosted) {
    const rows = await callOr<{ championKey: string }[]>("pool.get", [], { role });
    state.settings.poolDraft = rows.map((r) => r.championKey);
  } else {
    state.settings.poolDraft = role === state.role ? [...state.pool] : [];
  }
}

async function savePoolDraft(): Promise<void> {
  const role = state.settings.poolRole;
  const championKeys = [...state.settings.poolDraft];
  if (isHosted) {
    try {
      await call("pool.set", { role, championKeys });
    } catch (e) {
      // The tile already flipped; put it back and say why.
      state.settings.flash = `Could not save the pool: ${e instanceof Error ? e.message : String(e)}`;
      state.settings.flashError = true;
      await loadPoolDraft();
      render();
      return;
    }
  }
  // The pool on the board may be this role's own or borrowed from the primary role.
  if (role === state.role || role === state.poolRole) {
    await loadPool();
    state.scoredFor = null;
    void rescore();
  }
}

/* ── events ──────────────────────────────────────────────────────────── */

app.addEventListener("click", (e) => {
  const target = e.target as HTMLElement;

  const step = target.closest<HTMLButtonElement>(".step[data-phase]");
  if (step && !step.disabled) {
    // Picking a phase from another screen means "take me back to the session".
    state.screen = "session";
    state.phase = step.dataset.phase as Phase;
    return render();
  }

  const rec = target.closest<HTMLElement>(".rec");
  if (rec?.dataset.pick) {
    state.selected = rec.dataset.pick;
    return render();
  }

  const res = target.closest<HTMLElement>(".res");
  if (res?.dataset.res) {
    const picked = res.dataset.res as "win" | "loss";
    state.result = state.result === picked ? null : picked;
    const foe = laneOpponent();
    if (state.result && state.picked && foe) {
      void callOr("game.record", null, {
        championKey: state.picked,
        opponentKey: foe,
        role: state.role,
        won: state.result === "win",
      });
    }
    return render();
  }

  const actionEl = target.closest<HTMLElement>("[data-act]");
  const action = actionEl?.dataset.act;
  if (!action) return;

  if (action === "data") { void openData(); return; }
  if (action === "settings") { void openSettings(); return; }

  // ── window controls: the app is frameless, so these are the caption buttons ──
  // Nothing to report if they fail - the window is either there or the host is gone.
  if (action === "win-minimize") { void call("window.minimize").catch(() => {}); return; }
  if (action === "win-maximize") { void call("window.maximize").catch(() => {}); return; }
  if (action === "win-close") { void call("window.close").catch(() => {}); return; }

  // ── updates: reachable from the topbar as well as Settings ──────────
  if (action === "update-check") { void call("update.check").catch(() => {}); return; }
  if (action === "update-reveal") {
    if (state.update?.source) void call("shell.reveal", { path: state.update.source }).catch(() => {});
    return;
  }
  if (action === "update-restart") {
    // The host hands over to the installer and this process ends. If it refuses, say so
    // where the button is rather than doing nothing.
    void call("update.restart").catch((e: unknown) => {
      state.settings.flash = e instanceof Error ? e.message : String(e);
      state.settings.flashError = true;
      render();
    });
    return;
  }

  // ── data panel ──────────────────────────────────────────────────────
  if (state.screen === "data") {
    const path = actionEl?.dataset.path ?? "";
    const id = Number(actionEl?.dataset.id ?? 0);

    switch (action) {
      case "back":
        state.screen = "session";
        return render();
      case "reload":
        return void openData();
      case "cancel":
        state.data.confirmDelete = null;
        state.data.confirmRestore = null;
        state.data.editing = null;
        return render();
      case "backup":
        return void dataAction(async () => {
          await call("backup.now");
          return "Snapshot taken and mirrored.";
        });
      case "export":
        return void dataAction(async () => {
          const paths = await call<{ json: string; markdown: string }>("data.export");
          return `Exported notes.json and notes.md — ${paths.markdown}`;
        });
      case "import":
        return void dataAction(async () => {
          const added = await call<{ notes: number; games: number; pool: number } | null>(
            "data.importDialog",
          );
          if (added === null) return null; // cancelled
          return added.notes + added.games === 0
            ? "Nothing new - everything in that file was already here."
            : `Imported ${added.notes} note${added.notes === 1 ? "" : "s"} and ${added.games} game${added.games === 1 ? "" : "s"}.`;
        });
      case "reveal":
        return void dataAction(async () => {
          await call("shell.reveal", { path });
          return null;
        });
      case "restore":
        state.data.confirmRestore = path;
        state.data.confirmDelete = null;
        return render();
      case "restore-go":
        return void dataAction(async () => {
          const added = await call<{ notes: number; games: number }>("backup.restore", { path });
          return added.notes === 0
            ? "Nothing to bring back - that snapshot is already fully covered."
            : `Brought back ${added.notes} note${added.notes === 1 ? "" : "s"}.`;
        });
      case "edit":
        state.data.editing = id;
        state.data.confirmDelete = null;
        return render();
      case "delete":
        state.data.confirmDelete = id;
        state.data.confirmRestore = null;
        return render();
      case "delete-go":
        return void dataAction(async () => {
          await call("notes.delete", { id });
          return "Note deleted. The last snapshot still has it.";
        });
    }
    return;
  }

  // ── settings ────────────────────────────────────────────────────────
  if (state.screen === "settings") {
    const key = actionEl?.dataset.key ?? "";
    const s = state.settings;

    switch (action) {
      case "back":
        state.screen = "session";
        return render();
      case "cancel":
        s.confirmClear = false;
        return render();
      case "pool-role":
        s.poolRole = actionEl!.dataset.role as Role;
        return void loadPoolDraft().then(() => render());
      case "pool-toggle":
        if (!key) return;
        s.poolDraft = s.poolDraft.includes(key) ? s.poolDraft.filter((k) => k !== key) : [...s.poolDraft, key];
        refreshPool();
        return void savePoolDraft();
      case "key-clear":
        return void settingsAction(async () => {
          await call("config.setApiKey", { apiKey: "" });
          await reloadConfig();
          return "API key removed.";
        });
      case "clear-briefs":
        s.confirmClear = true;
        return render();
      case "clear-briefs-go":
        return void settingsAction(async () => {
          await call("brief.clearCache");
          state.briefs = {};
          return "Cached briefs cleared.";
        });
    }
    return;
  }

  // ── session ─────────────────────────────────────────────────────────
  switch (action) {
    case "lock": {
      // The button names the top recommendation when nothing has been clicked; lock that.
      const key = state.selected ?? state.recommendations[0]?.championKey;
      if (!key) return;
      // In a live draft the client does the locking; its session update moves the app on.
      if (state.live && isHosted) {
        state.lockError = null;
        void call("draft.lockIn", { championKey: key }).catch((e: Error) => {
          state.lockError = e.message;
          render();
        });
        return render();
      }
      lockIn(key);
      return;
    }
    case "after":
      state.phase = "after";
      break;
    case "draft":
      return newDraft();
    case "rescore":
      return void rescore(true);
    case "brief-retry": {
      const foe = laneOpponent();
      if (state.picked && foe) void ensureBrief(state.picked, foe, true);
      return;
    }
    case "brief-now": {
      // Write it on the current placement without waiting for the game to confirm it.
      const foe = laneOpponent();
      if (state.picked && foe) void ensureBrief(state.picked, foe);
      return;
    }
    case "reset-roles":
      state.draft.enemyRoles = { Darius: "Top", Nidalee: "Jungle" };
      break;
  }
  render();
});

app.addEventListener("keydown", (e) => {
  const rec = (e.target as HTMLElement).closest<HTMLElement>(".rec");
  if (rec?.dataset.pick && (e.key === "Enter" || e.key === " ")) {
    e.preventDefault();
    state.selected = rec.dataset.pick;
    render();
  }
});

app.addEventListener("change", (e) => {
  const el = e.target as HTMLElement;

  const roleSelect = el.closest<HTMLSelectElement>(".rolesel");
  if (roleSelect?.dataset.champ) {
    userAssignedRole(roleSelect.dataset.champ, roleSelect.value as Role);
    return render();
  }

  const setting = el.closest<HTMLSelectElement>("[data-setting]");
  if (setting) {
    const name = setting.dataset.setting!;
    const value = setting.value;
    if (name === "primaryRole") {
      void changeRole(value as Role).then(() => loadPoolDraft()).then(() => render());
      return;
    }
    void settingsAction(async () => {
      await call("config.set", { [name]: value });
      await reloadConfig();
      return null;
    });
  }
});

app.addEventListener("input", (e) => {
  const el = e.target as HTMLElement;
  if (el.id === "poolSearch") {
    state.settings.search = (el as HTMLInputElement).value;
    refreshPool(false);
  }
});

app.addEventListener("submit", (e) => {
  const form = e.target as HTMLFormElement;

  if (form.id === "apiKeyForm") {
    e.preventDefault();
    const input = form.elements.namedItem("apiKey") as HTMLInputElement;
    const apiKey = input.value.trim();
    if (!apiKey) return;
    void settingsAction(async () => {
      await call("config.setApiKey", { apiKey });
      await reloadConfig();
      state.scoredFor = null;
      return "API key saved.";
    });
    return;
  }

  if (form.id === "workspaceForm") {
    e.preventDefault();
    const workspaceId = (form.elements.namedItem("workspaceId") as HTMLInputElement).value.trim();
    void settingsAction(async () => {
      await call("config.set", { workspaceId });
      await reloadConfig();
      state.scoredFor = null;
      return workspaceId ? "Workspace id saved." : "Workspace id cleared.";
    });
    return;
  }

  const editId = form.dataset?.editId;
  if (editId) {
    e.preventDefault();
    const body = (form.elements.namedItem("body") as HTMLTextAreaElement).value.trim();
    if (!body) return;
    void dataAction(async () => {
      await call("notes.update", { id: Number(editId), body });
      return "Note updated.";
    });
    return;
  }

  if (form.id !== "noteForm") return;
  e.preventDefault();

  const input = document.getElementById("noteInput") as HTMLInputElement | null;
  const body = input?.value.trim();
  const foe = laneOpponent();
  if (!body || !state.picked || !foe) return;

  addNote(state.picked, foe, body);
  void callOr("notes.add", null, {
    championKey: state.picked,
    opponentKey: foe,
    role: state.role,
    body,
  });
  render(true);
});

/* ── clock ───────────────────────────────────────────────────────────── */

// Only the digits change each second; a full re-render would drop a focused dropdown.
setInterval(() => {
  if (state.screen !== "session" || state.phase !== "draft" || !state.live || state.timerInfinite) return;
  const el = document.querySelector<HTMLElement>(".clock .t");
  if (el) el.textContent = clock(timerRemaining());
}, 1000);

/* ── boot ────────────────────────────────────────────────────────────── */

async function start(): Promise<void> {
  render();

  // Resolve the current patch, then repaint so art comes from the right version.
  await initDataDragon();
  render();

  await boot();
  void rescore();
  render();
}

void start();

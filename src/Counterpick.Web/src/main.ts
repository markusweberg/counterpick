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
import { settingsView } from "./ui/settings";
import { afterView, briefView, draftView } from "./ui/views";

const app = document.getElementById("app")!;

function render(focusNote = false): void {
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
  const championKeys = state.settings.poolDraft;
  await callOr("pool.set", null, { role, championKeys });
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
        s.search = "";
        return void loadPoolDraft().then(() => render());
      case "pool-add":
        if (key && !s.poolDraft.includes(key)) s.poolDraft.push(key);
        s.search = "";
        return void savePoolDraft().then(() => render());
      case "pool-remove":
        s.poolDraft = s.poolDraft.filter((k) => k !== key);
        return void savePoolDraft().then(() => render());
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
    case "lock":
      if (state.selected) lockIn(state.selected);
      return;
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
    render();
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

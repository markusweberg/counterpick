import "./styles.css";

import { call, callOr, isHosted } from "./bridge";
import { initDataDragon } from "./ddragon";
import { addNote, assignRole, emptyDataState, laneOpponent, state } from "./state";
import type { Phase, Role } from "./types";
import { board, hero, topbar } from "./ui/chrome";
import { dataView, loadData } from "./ui/data";
import { afterView, briefView, draftView } from "./ui/views";

const app = document.getElementById("app")!;

function render(focusNote = false): void {
  // The Data panel is about the database, not the game, so it drops the hero and board.
  if (state.screen === "data") {
    app.innerHTML = `<div class="app">${topbar()}<main class="wide">${dataView()}</main></div>`;
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

async function openData(): Promise<void> {
  state.screen = "data";
  state.data = emptyDataState();
  state.data.loading = true;
  render();
  await loadData();
  render();
}

/* ── events ──────────────────────────────────────────────────────────── */

app.addEventListener("click", (e) => {
  const target = e.target as HTMLElement;

  const step = target.closest<HTMLButtonElement>(".step");
  if (step && !step.disabled) {
    // Picking a phase from the Data panel means "take me back to the session".
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

  // ── data panel ──────────────────────────────────────────────────────
  if (action === "data") { void openData(); return; }

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

  switch (action) {
    case "lock":
      state.picked = state.selected;
      state.phase = "locked";
      break;
    case "after":
      state.phase = "after";
      break;
    case "draft":
      state.phase = "draft";
      state.picked = null;
      state.result = null;
      break;
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
  const select = (e.target as HTMLElement).closest<HTMLSelectElement>(".rolesel");
  if (!select?.dataset.champ) return;
  assignRole(select.dataset.champ, select.value as Role);
  render();
});

app.addEventListener("submit", (e) => {
  const form = e.target as HTMLFormElement;

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

/* ── boot ────────────────────────────────────────────────────────────── */

async function start(): Promise<void> {
  render();

  // Resolve the current patch, then repaint so art comes from the right version.
  await initDataDragon();

  if (isHosted) {
    const config = await callOr<{ primaryRole?: string }>("config.get", {});
    if (config.primaryRole) state.role = config.primaryRole as Role;
  }

  render();
}

void start();

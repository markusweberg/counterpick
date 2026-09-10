import "./styles.css";

import { callOr, isHosted } from "./bridge";
import { initDataDragon } from "./ddragon";
import { addNote, assignRole, laneOpponent, state } from "./state";
import type { Phase, Role } from "./types";
import { board, hero, topbar } from "./ui/chrome";
import { afterView, briefView, draftView } from "./ui/views";

const app = document.getElementById("app")!;

function render(focusNote = false): void {
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

/* ── events ──────────────────────────────────────────────────────────── */

app.addEventListener("click", (e) => {
  const target = e.target as HTMLElement;

  const step = target.closest<HTMLButtonElement>(".step");
  if (step && !step.disabled) {
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

  const action = target.closest<HTMLElement>("[data-act]")?.dataset.act;
  if (!action) return;

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
  const form = e.target as HTMLElement;
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

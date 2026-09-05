// The target editor: an admin names the pins, moves them, adds and removes
// them, and looks at each one in 3D to decide. Everything edits state.targets
// in place so the map relabels as you type; Save writes the list back.
//
// A separate module because the rest of the viewer never edits map data, and
// the one card that does should not be mixed into the code that reads it.
import { state } from "./state.js?v=114";
import { putTargets } from "./api.js?v=114";

let callbacks = {
  onSetTarget: () => {},
  onLook: () => {},
  onChanged: () => {},
  status: () => {},
};

const editor = () => document.getElementById("target-editor");
const list = () => document.getElementById("target-list");
let dirty = false;

export function initAdmin(cb) {
  callbacks = { ...callbacks, ...cb };
  document.getElementById("targets-add").addEventListener("click", () => {
    if (!state.target || state.target.length < 3) {
      callbacks.status("set a target with a height first (a 3D click, or pick a floor) - that becomes the new pin");
      return;
    }
    state.targets.push({
      id: null,
      name: "new target",
      named: false,
      pos: [...state.target].map(v => Math.round(v * 10) / 10),
      landings: 0,
      spread: 0,
    });
    markDirty();
    renderAdmin();
    list().lastElementChild?.querySelector("input[type=text]")?.focus();
  });
  document.getElementById("targets-save").addEventListener("click", save);
  list().addEventListener("input", e => {
    const row = e.target.closest("[data-index]");
    if (!row) {
      return;
    }
    const t = state.targets[Number(row.dataset.index)];
    if (e.target.matches("input[type=text]")) {
      t.name = e.target.value;
      // Typed by a person: this is the name the server gets, and it is a
      // name, not a guess.
      t.serverName = e.target.value;
      t.named = e.target.value.trim().length > 0;
      row.querySelector("input[type=checkbox]").checked = t.named;
    } else if (e.target.matches("input[type=checkbox]")) {
      t.named = e.target.checked;
    }
    markDirty();
    callbacks.onChanged();
  });
  list().addEventListener("click", e => {
    const btn = e.target.closest("button[data-act]");
    const row = e.target.closest("[data-index]");
    if (!btn || !row) {
      return;
    }
    const i = Number(row.dataset.index);
    const t = state.targets[i];
    switch (btn.dataset.act) {
      case "look":
        callbacks.onSetTarget([...t.pos]);
        callbacks.onLook(t);
        break;
      case "here":
        if (!state.target || state.target.length < 3) {
          callbacks.status("set a target with a height first, then move the pin to it");
          return;
        }
        t.pos = [...state.target].map(v => Math.round(v * 10) / 10);
        markDirty();
        renderAdmin();
        callbacks.onChanged();
        break;
      case "drop":
        state.targets.splice(i, 1);
        markDirty();
        renderAdmin();
        callbacks.onChanged();
        break;
      default:
        break;
    }
  });
  // Leaving with unsaved names is the one way to lose ten minutes of work.
  window.addEventListener("beforeunload", e => {
    if (dirty) {
      e.preventDefault();
    }
  });
}

function markDirty() {
  dirty = true;
  const save = document.getElementById("targets-save");
  save.disabled = false;
  save.textContent = "Save changes";
}

// Whether the editor is available at all: the Targets mode tile shows for
// admins, and the panel shows the editor when that mode is chosen.
export function syncAdminMode() {
  const admin = !!state.account?.admin;
  document.getElementById("mode-targets").hidden = !admin;
  if (!admin && state.panelMode === "targets") {
    state.panelMode = "results";
  }
}

export function renderAdmin() {
  syncAdminMode();
  const el = editor();
  el.hidden = !(state.account?.admin && state.panelMode === "targets");
  if (el.hidden) {
    return;
  }
  const ol = list();
  ol.innerHTML = "";
  // The queue first: what still needs a person's word, then what has it.
  const order = state.targets.map((t, i) => ({ t, i })).sort((a, b) => (a.t.named ? 1 : 0) - (b.t.named ? 1 : 0));
  let lastNamed = null;
  for (const { t, i } of order) {
    if (t.named !== lastNamed) {
      lastNamed = t.named;
      const head = document.createElement("li");
      head.className = "exec-head";
      const n = state.targets.filter(x => !!x.named === t.named).length;
      head.textContent = t.named ? `confirmed (${n})` : `to confirm (${n})`;
      ol.appendChild(head);
    }
    const li = document.createElement("li");
    li.className = "target-row" + (t.named ? " confirmed" : " provisional");
    li.dataset.index = String(i);
    const current = state.target && Math.hypot(state.target[0] - t.pos[0], state.target[1] - t.pos[1]) < 1;
    if (current) {
      li.classList.add("current");
    }
    li.innerHTML =
      `<div class="line"><input type="text" value="${escapeAttr(t.name)}" maxlength="40" aria-label="Target name" placeholder="name">` +
      `<label class="named" title="Confirmed: a person has checked the name and the position. Unticked, it is a guess from the nearest callout and shows a ? on the map"><input type="checkbox" ${t.named ? "checked" : ""} aria-label="Confirmed"> ${t.named ? "\u{1F512} confirmed" : "confirm"}</label></div>` +
      `<div class="line"><span class="pos" title="${t.landings ? `${t.landings} pro landings, spread ${Math.round(t.spread)}u` : "added by hand"}">${t.pos[0].toFixed(0)}, ${t.pos[1].toFixed(0)}, ${t.pos[2].toFixed(0)}</span>` +
      `<span class="acts">` +
      `<button type="button" class="btn" data-act="look" title="Make this the target and look at it in 3D">Look</button>` +
      `<button type="button" class="btn" data-act="here" title="Move this pin to the current target position">Here</button>` +
      `<button type="button" class="btn danger" data-act="drop" title="Delete this pin" aria-label="Delete">×</button>` +
      `</span></div>`;
    ol.appendChild(li);
  }
  document.getElementById("head-count").textContent =
    `${state.targets.filter(t => !t.named).length} open \u00b7 ${state.targets.filter(t => t.named).length} confirmed`;
  const save = document.getElementById("targets-save");
  if (!dirty) {
    save.disabled = true;
    save.textContent = "Saved";
  }
}

async function save() {
  const save = document.getElementById("targets-save");
  save.disabled = true;
  save.textContent = "Saving…";
  try {
    const saved = await putTargets(state.currentMap, state.targets);
    state.targets = saved;
    dirty = false;
    callbacks.status(`${saved.length} targets saved for ${state.currentMap}`);
    renderAdmin();
    callbacks.onChanged();
  } catch (err) {
    callbacks.status(`targets not saved: ${err.message}`);
    markDirty();
  }
}

function escapeAttr(s) {
  return String(s).replace(/&/g, "&amp;").replace(/"/g, "&quot;").replace(/</g, "&lt;");
}

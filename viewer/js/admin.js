// The target editor: an admin names the pins, moves them, adds and removes
// them, and looks at each one in 3D to decide. Everything edits state.targets
// in place so the map relabels as you type; Save writes the list back.
//
// A separate module because the rest of the viewer never edits map data, and
// the one card that does should not be mixed into the code that reads it.
import { state } from "./state.js?v=105";
import { putTargets } from "./api.js?v=105";

let callbacks = {
  onSetTarget: () => {},
  onLook: () => {},
  onChanged: () => {},
  status: () => {},
};

const card = () => document.getElementById("card-targets");
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
      // A name typed by a person is a name, not a guess.
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

export function renderAdmin() {
  const el = card();
  el.hidden = !state.account?.admin;
  if (el.hidden) {
    return;
  }
  const ol = list();
  ol.innerHTML = "";
  document.getElementById("targets-count").textContent = `(${state.targets.length})`;
  state.targets.forEach((t, i) => {
    const li = document.createElement("li");
    li.className = "target-row" + (t.named ? "" : " provisional");
    li.dataset.index = String(i);
    const current = state.target && Math.hypot(state.target[0] - t.pos[0], state.target[1] - t.pos[1]) < 1;
    if (current) {
      li.classList.add("current");
    }
    li.innerHTML =
      `<div class="line"><input type="text" value="${escapeAttr(t.name)}" maxlength="40" aria-label="Target name" placeholder="name">` +
      `<label class="named" title="Confirmed by a person - not a guess from the nearest callout"><input type="checkbox" ${t.named ? "checked" : ""} aria-label="Name confirmed"> named</label></div>` +
      `<div class="line"><span class="pos" title="${t.landings ? `${t.landings} pro landings, spread ${Math.round(t.spread)}u` : "added by hand"}">${t.pos[0].toFixed(0)}, ${t.pos[1].toFixed(0)}, ${t.pos[2].toFixed(0)}</span>` +
      `<span class="acts">` +
      `<button type="button" class="btn" data-act="look" title="Make this the target and look at it in 3D">Look</button>` +
      `<button type="button" class="btn" data-act="here" title="Move this pin to the current target position">Here</button>` +
      `<button type="button" class="btn danger" data-act="drop" title="Delete this pin" aria-label="Delete">×</button>` +
      `</span></div>`;
    ol.appendChild(li);
  });
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

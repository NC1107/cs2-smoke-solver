// Lineup card rendering, the paste-getpos box, and copy buttons (including
// the practice-setup card wired via wireCopyButtons on document.body).
// Setting a target routes through the callback main.js registers.

import { state, filtered, typeShort, clickShort, clickClass } from "./state.js";

const statusEl = state.statusEl;

let callbacks = {
  onSetTarget: () => {},
};

// The panel shows only the lineup picked by clicking its map marker; the map
// itself is the list.
export function renderLineups() {
  const list = document.getElementById("lineup-list");
  list.innerHTML = "";
  const shown = filtered();
  statusEl.textContent = state.result ? `${shown.length} lineups - click a marker` : statusEl.textContent;
  if (state.selected < 0 || !state.result) {
    wireCopyButtons(list);
    return;
  }
  const l = state.result.lineups[state.selected];
  const i = state.selected;
  const el = document.createElement("div");
  el.className = "lineup selected";
  el.innerHTML =
    `<div class="row1"><b class="${clickClass(l.strength)}">${clickShort(l.strength)}</b><span>${typeShort[l.type]}</span>` +
    `<span>${l.Bounces}b</span><span>${l.flightTime.toFixed(1)}s</span>` +
    `<span class="pct">${(l.stability * 100).toFixed(0)}%</span></div>` +
    `<div style="margin:4px 0 2px">${l.how}</div>` +
    `<div class="cmd" id="cmd-l${i}">${l.console}<button data-copy="cmd-l${i}">copy</button></div>`;
  list.appendChild(el);
  wireCopyButtons(list);
}

function setTargetFromGetpos() {
  const m = document.getElementById("getpos-in").value
    .match(/setpos\s+(-?[\d.]+)\s+(-?[\d.]+)\s+(-?[\d.]+)/);
  if (!m) {
    statusEl.textContent = "cannot parse getpos";
    return;
  }
  callbacks.onSetTarget(
    [parseFloat(m[1]), parseFloat(m[2]), parseFloat(m[3]) - 64],
    `target ${(+m[1]).toFixed(0)}, ${(+m[2]).toFixed(0)}`);
}

export function wireCopyButtons(container) {
  for (const btn of container.querySelectorAll("[data-copy]")) {
    btn.addEventListener("click", e => {
      e.stopPropagation();
      const node = document.getElementById(btn.dataset.copy).cloneNode(true);
      node.querySelector("button").remove();
      navigator.clipboard.writeText(node.textContent.trim()).then(() => {
        btn.textContent = "copied";
        setTimeout(() => { btn.textContent = "copy"; }, 1200);
      });
    });
  }
}

export function initPanel(cb) {
  callbacks = cb;
  document.getElementById("getpos-set").addEventListener("click", setTargetFromGetpos);
  document.getElementById("getpos-in").addEventListener("keydown", e => {
    if (e.key === "Enter") {
      setTargetFromGetpos();
    }
  });
  wireCopyButtons(document.body);
}

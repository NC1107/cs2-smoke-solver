// Lineup card rendering, the keyboard-navigable result list, the
// paste-getpos box, and copy buttons (including the practice-setup card
// wired via wireCopyButtons on document.body). Setting a target and
// selecting a lineup route through the callbacks main.js registers.

import { state, filtered, typeShort, clickShort, clickClass, esc, skyAngle, DEFAULT_EYE_HEIGHT } from "./state.js";

const statusEl = state.statusEl;
const LIST_CAP = 50;

let callbacks = {
  onSetTarget: () => {},
  onSelect: () => {},
  onPreview: () => {},
  onGoTo: () => {},
  onFavorite: () => {},
  onRemove: () => {},
  onShare: () => {},
};

function lineupSummaryHtml(l) {
  // Aim reference badge: SKY = nothing to line the crosshair against, shown
  // with how far above the horizon it points (CS2's grenade reticle reaches the
  // screen edge, so a shallow sky shot can still be aligned against the skyline
  // while a steep one has nothing on screen at all); flat = geometry but no
  // silhouette; edge = a silhouette within X degrees of the crosshair (smaller
  // = easier to align).
  const ref = !l.aimRef ? ""
    : l.aimRef.tier === "sky" ? `<span class="ref sky" title="aims ${skyAngle(l).toFixed(0)} deg above the horizon with nothing on screen to line up against">SKY ${skyAngle(l).toFixed(0)}°</span>`
    : l.aimRef.tier === "reticle" ? `<span class="ref reticle" title="open sky at the crosshair, but the grenade reticle's arms cross a silhouette ${l.aimRef.reticleDeg.toFixed(0)} deg out - line it up on that">reticle ${l.aimRef.reticleDeg.toFixed(0)}°</span>`
    : l.aimRef.tier === "flat" ? `<span class="ref flat" title="aims at featureless surface - weak reference">flat</span>`
    : `<span class="ref edge" title="silhouette ${l.aimRef.edgeDeg.toFixed(1)} deg from crosshair">${l.aimRef.edgeDeg.toFixed(1)}°</span>`;
  // Geometry-pinned stand spots: the wall places the player, so only aim
  // remains - the easiest lineups to reproduce in a real round.
  const pin = l.pin === "corner" ? `<span class="ref pin" title="stand spot is wedged into a corner - walk into it and your position is exact">corner</span>`
    : l.pin === "wall" ? `<span class="ref pin" title="stand spot presses against a wall - walk into it to remove position error">wall</span>`
    : "";
  const fav = l._favorite ? `<span class="ref fav" title="favorited">★</span>` : "";
  return `<b class="${clickClass(l.strength)}">${clickShort(l.strength)}</b><span>${typeShort[l.type]}</span>` +
    `<span>${l.Bounces}b</span><span>${l.flightTime.toFixed(1)}s</span>${pin}${ref}${fav}` +
    `<span class="pct">${(l.stability * 100).toFixed(0)}%</span>`;
}

// Roving tabindex: one list button is tabbable; arrows/Home/End move focus.
function onListKeydown(e) {
  if (!["ArrowDown", "ArrowUp", "Home", "End"].includes(e.key)) {
    return;
  }
  e.preventDefault();
  const btns = [...e.currentTarget.querySelectorAll(".lineup-option")];
  const cur = btns.indexOf(document.activeElement);
  const next = e.key === "Home" ? 0
    : e.key === "End" ? btns.length - 1
    : Math.min(Math.max(cur + (e.key === "ArrowDown" ? 1 : -1), 0), btns.length - 1);
  if (next === cur || !btns[next]) {
    return;
  }
  if (cur >= 0) {
    btns[cur].tabIndex = -1;
  }
  btns[next].tabIndex = 0;
  btns[next].focus();
}

// Detail card for the pinned lineup plus a capped, keyboard-navigable list
// of the filtered results (H19). The canvas stays the visual index; the
// list drives the exact same select path as clicking a marker.
// One copy of the results status line; main.js's heat toggle prints it too.
// Naming the hidden count matters: the sky filter ships pre-set, so a user's
// very first solve can silently drop results with no visible cause beyond a
// small count on a collapsed accordion.
export function resultStatusText(shown) {
  const total = state.result ? state.result.lineups.filter(l => !l._removed).length : shown;
  const hidden = total - shown;
  return `${shown} lineups - click a marker or use the list` +
    (hidden > 0 ? ` · ${hidden} hidden by filters` : "");
}

export function renderLineups() {
  // The panel earns its screen space only once there are results to show;
  // empty it reads as a stray bar of chrome (worst on phones, where it
  // anchors to the bottom edge like a footer).
  document.getElementById("panel").hidden = !state.result;
  const list = document.getElementById("lineup-list");
  const focusIdx = list.contains(document.activeElement)
    ? document.activeElement.dataset.idx : undefined;
  list.innerHTML = "";
  if (!state.result) {
    return;
  }
  const shown = filtered();
  statusEl.textContent = resultStatusText(shown.length);

  // The preview lives in the panel's fixed header, outside the scrolling list,
  // so hunting down the results never scrolls the render you are comparing
  // against off screen.
  const pane = document.getElementById("preview-pane");
  pane.hidden = state.selected < 0;
  if (state.selected >= 0) {
    callbacks.onPreview(state.result.lineups[state.selected], document.getElementById("preview-thumb"));
  }

  if (shown.length === 0) {
    return;
  }

  const note = document.createElement("div");
  note.className = "list-note";
  note.textContent = shown.length > LIST_CAP
    ? `top ${LIST_CAP} of ${shown.length} results`
    : `${shown.length} result${shown.length === 1 ? "" : "s"}`;
  list.appendChild(note);

  const box = document.createElement("div");
  box.className = "lineup-options";
  box.setAttribute("role", "group");
  box.setAttribute("aria-label", `lineup results, ${note.textContent}`);
  for (const l of shown.slice(0, LIST_CAP)) {
    // The selected lineup expands where it sits. Rendering its detail card at
    // the top of the panel instead meant that picking the 40th result put the
    // preview image somewhere far above the scroll position, out of sight.
    box.appendChild(l._idx === state.selected ? detailCard(l) : optionButton(l));
  }
  box.addEventListener("keydown", onListKeydown);
  const home = box.querySelector(".lineup-option") ?? box.firstElementChild;
  if (home.classList.contains("lineup-option")) {
    home.tabIndex = 0;
  }
  list.appendChild(box);

  // Selecting re-renders the list; keep keyboard focus on the same lineup.
  if (focusIdx !== undefined) {
    const again = box.querySelector(`.lineup-option[data-idx="${focusIdx}"]`);
    if (again) {
      home.tabIndex = -1;
      again.tabIndex = 0;
      again.focus();
    }
  }
}

function optionButton(l) {
  const b = document.createElement("button");
  b.type = "button";
  b.className = "lineup-option";
  b.dataset.idx = l._idx;
  b.setAttribute("aria-pressed", "false");
  b.tabIndex = -1;
  b.innerHTML = lineupSummaryHtml(l);
  b.addEventListener("click", () => callbacks.onSelect(l._idx));
  return b;
}

function detailCard(l) {
  const i = l._idx;
  const el = document.createElement("div");
  el.className = "lineup selected";
  el.innerHTML =
    `<div class="row1">${lineupSummaryHtml(l)}</div>` +
    `<div style="margin:4px 0 2px">${esc(l.how)}</div>` +
    `<div class="cmd" id="cmd-l${i}">${esc(l.console)}<button data-copy="cmd-l${i}" class="btn">copy</button></div>` +
    `<div class="lineup-actions">` +
    `<button type="button" class="btn share-btn" title="copy a link that opens this exact lineup">Share</button>` +
    `<button type="button" class="btn goto-btn" title="move the free 3D camera into this lineup's exact throw spot">Go to</button>` +
    `<button type="button" class="btn fav-btn">${l._favorite ? "★ favorited" : "☆ favorite"}</button>` +
    `<button type="button" class="btn remove-btn">Remove</button>` +
    `</div>`;
  // Card click toggles the selection off, matching marker behavior (L16).
  el.addEventListener("click", () => callbacks.onSelect(i));
  wireCopyButtons(el);

  for (const [selector, action] of [
    [".share-btn", callbacks.onShare],
    [".goto-btn", callbacks.onGoTo],
    [".fav-btn", callbacks.onFavorite],
    [".remove-btn", callbacks.onRemove],
  ]) {
    el.querySelector(selector).addEventListener("click", e => {
      e.stopPropagation();
      action(l);
    });
  }
  return el;
}

// Selecting from the map (rather than the list) can expand a card that is
// scrolled out of view; bring it in without yanking the panel around.
export function revealSelected() {
  document.querySelector("#lineup-list .lineup.selected")
    ?.scrollIntoView({ block: "nearest", behavior: "smooth" });
}

function setTargetFromGetpos() {
  const m = document.getElementById("getpos-in").value
    .match(/setpos\s+(-?[\d.]+)\s+(-?[\d.]+)\s+(-?[\d.]+)/);
  if (!m) {
    statusEl.textContent = "cannot parse getpos";
    return;
  }
  callbacks.onSetTarget(
    [parseFloat(m[1]), parseFloat(m[2]), parseFloat(m[3]) - DEFAULT_EYE_HEIGHT],
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

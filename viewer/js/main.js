// Boot and cross-module orchestration. This is the only module allowed to
// import the feature modules; they call back into the orchestrators defined
// here (setTarget, select, runQuery) via the init*/set*Callbacks hooks.

import { state, filtered, esc } from "./state.js";
import { loadMapData, runQuery as postLineupQuery } from "./api.js";
import { loadRadar, readColors, recolorRadar, draw, resize, resetView, initMap2d } from "./map2d.js";
import { ensure3d, resetEnsure3d, current3d, sync3d, set3dCallbacks, applyTheme3d } from "./view3d.js";
import { renderLineups, initPanel } from "./panel.js";

(async () => {
  function bootError(file) {
    const box = document.createElement("div");
    box.style.cssText = "position:fixed; inset:0; z-index:60; display:flex; align-items:center; justify-content:center";
    box.innerHTML =
      `<div style="background:var(--panel); border:1px solid var(--line); border-radius:8px; padding:18px 24px; max-width:460px">` +
      `<b>failed to load ${esc(file)}</b><br>` +
      `<span style="color:var(--muted)">regenerate it with the <code>viewerdata</code> CLI command, then reload</span></div>`;
    document.body.appendChild(box);
  }

  try {
    state.mapData = await loadMapData();
  } catch {
    bootError("data/viewer-map.json");
    return;
  }
  document.getElementById("brand").textContent = state.mapData.map;
  document.getElementById("meta").textContent =
    `build ${state.mapData.build}`;

  const canvas = state.canvas;
  const stage3d = state.stage3d;
  const statusEl = state.statusEl;
  const heatBtn = document.getElementById("heat");
  const keyEl = document.getElementById("key-dots");

  try {
    await loadRadar();
  } catch {
    bootError("data/" + state.mapData.image);
    return;
  }

  function select(i) {
    state.selected = i === state.selected ? -1 : i;
    renderLineups();
    draw();
    sync3d();
  }

  // Heat mode swaps the map key from marker shapes to coverage colors (L23).
  function setHeat(on) {
    state.heatOn = on;
    heatBtn.classList.toggle("primary", on);
    keyEl.classList.toggle("heat", on);
  }

  const solvingOverlay = document.getElementById("solving-overlay");
  const cancelBtn = document.getElementById("solve-cancel");
  let solveController = null;
  cancelBtn.addEventListener("click", () => solveController?.abort());

  async function runQuery(body) {
    state.busy = true;
    statusEl.textContent = "solving…";
    solveController = new AbortController();
    const prevFocus = document.activeElement;
    solvingOverlay.classList.add("on");
    cancelBtn.focus();
    try {
      const { error, data } = await postLineupQuery(body, solveController.signal);
      if (error) {
        statusEl.textContent = error;
        return;
      }
      const next = data;
      if (next.lineups.length === 0) {
        statusEl.textContent = `none from there (${next.origins} spots); try another`;
        return;
      }
      next.lineups.forEach((l, i) => { l._idx = i; });
      state.result = next;
      state.selected = -1;
      heatBtn.disabled = !next.coverage;
      renderLineups();
      sync3d();
    } catch (err) {
      statusEl.textContent = err.name === "AbortError" ? "cancelled" : `error: ${err.message}`;
    } finally {
      state.busy = false;
      solveController = null;
      solvingOverlay.classList.remove("on");
      if (prevFocus instanceof HTMLElement && document.contains(prevFocus)) {
        prevFocus.focus();
      }
      draw();
    }
  }

  function setTarget(t, note) {
    state.target = t;
    state.picking = false;
    state.result = null;
    state.selected = -1;
    setHeat(false);
    canvas.classList.remove("picking");
    document.getElementById("search-all").disabled = false;
    heatBtn.disabled = true;
    statusEl.textContent = note;
    renderLineups();
    draw();
    sync3d();
  }

  document.getElementById("pick").addEventListener("click", () => {
    state.picking = true;
    canvas.classList.add("picking");
    statusEl.textContent = "click target";
  });
  document.getElementById("search-all").addEventListener("click", () => {
    if (state.target && !state.busy) {
      statusEl.textContent = "searching map…";
      runQuery({ target: state.target });
    }
  });
  document.getElementById("clear").addEventListener("click", () => {
    state.picking = false;
    state.target = null;
    state.result = null;
    state.selected = -1;
    setHeat(false);
    canvas.classList.remove("picking");
    document.getElementById("search-all").disabled = true;
    heatBtn.disabled = true;
    statusEl.textContent = "";
    renderLineups();
    draw();
  });
  heatBtn.addEventListener("click", () => {
    setHeat(!state.heatOn);
    statusEl.textContent = state.heatOn
      ? "heatmap: blue fill = reachable (brighter = more options), orange outline = standable but NO throw reaches"
      : `${filtered().length} lineups - click a marker or use the list`;
    draw();
  });
  for (const f of Object.values(state.filters)) {
    f.addEventListener("change", () => { state.selected = -1; renderLineups(); draw(); sync3d(); });
  }

  initPanel({ onSetTarget: setTarget, onSelect: select });
  initMap2d({ onSetTarget: setTarget, onSelect: select, onRunQuery: runQuery });
  set3dCallbacks({ onSetTarget: setTarget, onSelect: select });

  document.getElementById("view3d").addEventListener("click", async () => {
    const on = stage3d.style.display === "none";
    stage3d.style.display = on ? "block" : "none";
    canvas.style.display = on ? "none" : "block";
    document.getElementById("view3d").classList.toggle("primary", on);
    if (on) {
      statusEl.textContent = "loading 3D mesh…";
      try {
        const t3 = await ensure3d();
        if (stage3d.style.display === "none") { // toggled off while loading
          statusEl.textContent = "";
          return;
        }
        statusEl.textContent = "3D: WASD fly (QE up/down, shift fast) · drag orbit · right-drag pan · scroll zoom · click terrain = set target · click marker = pin";
        t3.start();
        sync3d();
      } catch {
        resetEnsure3d();
        stage3d.style.display = "none";
        canvas.style.display = "block";
        document.getElementById("view3d").classList.remove("primary");
        statusEl.textContent = "3D unavailable: serve needs --geo";
        draw();
      }
    } else if (current3d()) {
      current3d().stop();
      draw();
    }
  });

  matchMedia("(prefers-color-scheme: dark)").addEventListener("change", () => {
    readColors();
    recolorRadar();
    draw();
    applyTheme3d();
  });

  // A resolution media query fires once per boundary crossing, so re-register
  // after each change to keep tracking DPR across monitor moves (M45).
  (function watchDpr() {
    matchMedia(`(resolution: ${window.devicePixelRatio}dppx)`).addEventListener(
      "change", () => { resize(); current3d()?.resize3d(); watchDpr(); }, { once: true });
  })();

  // Below the breakpoint the control cards collapse to <details>; CSS cannot
  // force a closed details open again at desktop width, so sync it here.
  const compactMq = matchMedia("(max-width: 640px)");
  const syncCompactControls = () => {
    for (const d of document.querySelectorAll("#controls details")) {
      d.open = !compactMq.matches;
    }
  };
  syncCompactControls();
  compactMq.addEventListener("change", syncCompactControls);

  readColors();
  recolorRadar();
  resize();
  resetView();
})();

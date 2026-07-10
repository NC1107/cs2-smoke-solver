// Boot and cross-module orchestration. This is the only module allowed to
// import the feature modules; they call back into the orchestrators defined
// here (setTarget, select, runQuery) via the init*/set*Callbacks hooks.

import { state, filtered } from "./state.js";
import { loadMapData, runQuery as postLineupQuery } from "./api.js";
import { loadRadar, readColors, recolorRadar, draw, resize, resetView, initMap2d } from "./map2d.js";
import { ensure3d, resetEnsure3d, current3d, sync3d, set3dCallbacks } from "./view3d.js";
import { renderLineups, initPanel } from "./panel.js";

(async () => {
  function bootError(file) {
    const box = document.createElement("div");
    box.style.cssText = "position:fixed; inset:0; z-index:60; display:flex; align-items:center; justify-content:center";
    box.innerHTML =
      `<div style="background:var(--panel); border:1px solid var(--line); border-radius:8px; padding:18px 24px; max-width:460px">` +
      `<b>failed to load ${file}</b><br>` +
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

  const solvingOverlay = document.getElementById("solving-overlay");

  async function runQuery(body) {
    state.busy = true;
    statusEl.textContent = "solving…";
    solvingOverlay.classList.add("on");
    try {
      const { error, data } = await postLineupQuery(body);
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
      document.getElementById("heat").disabled = !next.coverage;
      renderLineups();
      sync3d();
    } catch (err) {
      statusEl.textContent = `error: ${err.message}`;
    } finally {
      state.busy = false;
      solvingOverlay.classList.remove("on");
      draw();
    }
  }

  function setTarget(t, note) {
    state.target = t;
    state.picking = false;
    state.result = null;
    state.selected = -1;
    state.heatOn = false;
    canvas.classList.remove("picking");
    document.getElementById("search-all").disabled = false;
    document.getElementById("heat").disabled = true;
    document.getElementById("heat").classList.remove("primary");
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
    state.heatOn = false;
    canvas.classList.remove("picking");
    document.getElementById("search-all").disabled = true;
    document.getElementById("heat").disabled = true;
    document.getElementById("heat").classList.remove("primary");
    statusEl.textContent = "";
    renderLineups();
    draw();
  });
  document.getElementById("heat").addEventListener("click", () => {
    state.heatOn = !state.heatOn;
    document.getElementById("heat").classList.toggle("primary", state.heatOn);
    statusEl.textContent = state.heatOn
      ? "heatmap: green = reachable (brighter = more options), red = standable but NO throw reaches"
      : `${filtered().length} lineups - click a marker`;
    draw();
  });
  for (const f of Object.values(state.filters)) {
    f.addEventListener("change", () => { state.selected = -1; renderLineups(); draw(); sync3d(); });
  }

  initPanel({ onSetTarget: setTarget });
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

  readColors();
  recolorRadar();
  resize();
  resetView();
})();

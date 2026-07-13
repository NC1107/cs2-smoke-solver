// Boot and cross-module orchestration. This is the only module allowed to
// import the feature modules; they call back into the orchestrators defined
// here (setTarget, select, runQuery) via the init*/set*Callbacks hooks.

import { state, filtered, esc } from "./state.js";
import { loadMapData, runQuery as postLineupQuery } from "./api.js";
import { loadRadar, readColors, recolorRadar, draw, scheduleDraw, resize, resetView, initMap2d } from "./map2d.js";
import { ensure3d, resetEnsure3d, current3d, sync3d, set3dCallbacks, applyTheme3d, capturePreview } from "./view3d.js";
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
  const texturedBtn = document.getElementById("textured3d");
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

  const cancelBtn = document.getElementById("solve-cancel");
  let solveController = null;
  cancelBtn.addEventListener("click", () => solveController?.abort());

  // Progress lines stream in every ~100ms; painting each batch as dots shows
  // both sweep speed and which standable origins were actually evaluated.
  function onSolveProgress(msg) {
    const p = state.progress;
    if (!p) {
      return;
    }
    if (msg.phase === "prepare") {
      statusEl.textContent = "preparing voxel grid…";
    } else if (msg.phase === "sweep") {
      p.phase = "sweep";
      p.total = msg.count;
    } else if (msg.phase === "verify") {
      p.phase = "verify";
      p.candidates = msg.count;
      statusEl.textContent = `verifying 0 / ${msg.count} candidates against the exact sim…`;
    } else if (msg.checked) {
      p.checked.push(...msg.checked);
      statusEl.textContent = `checked ${p.checked.length}${p.total ? ` / ${p.total}` : ""} positions…`;
    } else if (msg.verified) {
      p.verified.push(...msg.verified);
      statusEl.textContent = `verifying ${p.verified.length} / ${p.candidates ?? "?"} candidates against the exact sim…`;
    }
    scheduleDraw();
  }

  async function runQuery(body) {
    state.busy = true;
    state.progress = { phase: "sweep", total: 0, candidates: 0, checked: [], verified: [] };
    statusEl.textContent = "solving…";
    solveController = new AbortController();
    cancelBtn.hidden = false;
    try {
      const { error, data } = await postLineupQuery(body, solveController.signal, onSolveProgress);
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
      state.progress = null;
      solveController = null;
      cancelBtn.hidden = true;
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
      ? "heatmap: solid blue = verified lineup, faint blue = sim reaches but unverified, orange outline = standable but NO throw reaches"
      : `${filtered().length} lineups - click a marker or use the list`;
    draw();
  });
  for (const f of Object.values(state.filters)) {
    f.addEventListener("change", () => { state.selected = -1; renderLineups(); draw(); sync3d(); });
  }

  const previewModal = document.getElementById("preview-modal");
  const previewImg = document.getElementById("preview-img");
  document.getElementById("preview-close").addEventListener("click", () => { previewModal.hidden = true; });
  previewModal.addEventListener("click", e => { if (e.target === previewModal) { previewModal.hidden = true; } });

  // Renders entirely client-side (capturePreview reuses the shared
  // camera/canvas already in this page), so no server round-trip - just a
  // one-time ~300MB texture load the first time any preview is requested.
  async function showPreview(l, btn) {
    const prevLabel = btn.textContent;
    btn.disabled = true;
    btn.textContent = "rendering…";
    statusEl.textContent = "rendering preview (loads real map textures, ~300MB one-time)…";
    try {
      previewImg.src = await capturePreview({ feet: l.feet, type: l.type, pitchDeg: l.pitch, yawDeg: l.yaw });
      previewModal.hidden = false;
      statusEl.textContent = "";
    } catch (err) {
      statusEl.textContent = `preview failed: ${err.message}`;
    } finally {
      btn.disabled = false;
      btn.textContent = prevLabel;
    }
  }

  initPanel({ onSetTarget: setTarget, onSelect: select, onPreview: showPreview });
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
        texturedBtn.disabled = false;
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
      texturedBtn.disabled = true;
      draw();
    }
  });

  texturedBtn.addEventListener("click", async () => {
    const t3 = current3d();
    if (!t3) {
      return;
    }
    const wantOn = !t3.isTextured;
    texturedBtn.disabled = true;
    statusEl.textContent = wantOn ? "loading real map textures (~300MB, one-time)…" : "";
    try {
      await t3.setTextured(wantOn);
      texturedBtn.classList.toggle("primary", wantOn);
      statusEl.textContent = wantOn
        ? "textured: drag orbit · right-drag pan · scroll zoom · WASD fly"
        : "3D: WASD fly (QE up/down, shift fast) · drag orbit · right-drag pan · scroll zoom · click terrain = set target · click marker = pin";
    } catch (err) {
      statusEl.textContent = `failed to load textures: ${err.message}`;
    } finally {
      texturedBtn.disabled = false;
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

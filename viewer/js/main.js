// Boot and cross-module orchestration. This is the only module allowed to
// import the feature modules; they call back into the orchestrators defined
// here (setTarget, select, runQuery) via the init*/set*Callbacks hooks.

import { state, filtered, esc } from "./state.js";
import { loadMapList, loadMapData, runQuery as postLineupQuery } from "./api.js";
import { loadRadar, readColors, recolorRadar, draw, scheduleDraw, resize, resetView, initMap2d } from "./map2d.js";
import { ensure3d, resetEnsure3d, resetEnsureTexturedScene, teardown3d, current3d, sync3d, set3dCallbacks, applyTheme3d, capturePreview } from "./view3d.js";
import { renderLineups, initPanel } from "./panel.js";

(async () => {
  // Map switching means a failed load is no longer necessarily terminal (the
  // user can just pick a different map), so a stale error from a previous
  // attempt must not linger once a later one succeeds.
  function clearBootError() {
    document.getElementById("boot-error")?.remove();
  }
  function bootError(file) {
    clearBootError();
    const box = document.createElement("div");
    box.id = "boot-error";
    box.style.cssText = "position:fixed; inset:0; z-index:60; display:flex; align-items:center; justify-content:center";
    box.innerHTML =
      `<div style="background:var(--panel); border:1px solid var(--line); border-radius:8px; padding:18px 24px; max-width:460px">` +
      `<b>failed to load ${esc(file)}</b><br>` +
      `<span style="color:var(--muted)">regenerate it with the <code>viewerdata</code> CLI command, then reload</span></div>`;
    document.body.appendChild(box);
  }

  const canvas = state.canvas;
  const stage3d = state.stage3d;
  const statusEl = state.statusEl;
  const heatBtn = document.getElementById("heat");
  const texturedBtn = document.getElementById("textured3d");
  const keyEl = document.getElementById("key-dots");
  const mapSelect = document.getElementById("map-select");
  const cancelBtn = document.getElementById("solve-cancel");
  let solveController = null;
  cancelBtn.addEventListener("click", () => solveController?.abort());

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

  // A different map is entirely different geometry/nav/lineups, so this is a
  // full reset (target, results, 3D scene) rather than an incremental swap -
  // there is only ever "the current map," never a per-map cache to switch
  // back into. Returns false (and shows the boot error) if the map's data
  // failed to load, so the caller can bail out the same way initial boot did.
  async function loadMap(name) {
    solveController?.abort();
    teardown3d();
    stage3d.style.display = "none";
    canvas.style.display = "block";
    document.getElementById("view3d").classList.remove("primary");
    texturedBtn.disabled = true;
    texturedBtn.classList.remove("primary");
    state.currentMap = name;
    state.picking = false;
    state.target = null;
    state.result = null;
    state.selected = -1;
    setHeat(false);
    canvas.classList.remove("picking");
    document.getElementById("search-all").disabled = true;
    heatBtn.disabled = true;

    try {
      state.mapData = await loadMapData(name);
    } catch {
      bootError(`data/${name}.viewer-map.json`);
      return false;
    }
    document.getElementById("meta").textContent = `build ${state.mapData.build}`;
    try {
      await loadRadar();
    } catch {
      bootError("data/" + state.mapData.image);
      return false;
    }
    clearBootError();
    recolorRadar();
    resize();
    resetView();
    renderLineups();
    statusEl.textContent = "";
    return true;
  }

  let mapList;
  try {
    mapList = await loadMapList();
  } catch {
    bootError("/api/maps");
    return;
  }
  if (mapList.length === 0) {
    bootError("data/*.s2geo (no maps extracted yet)");
    return;
  }
  mapSelect.innerHTML = mapList.map(m => `<option value="${esc(m.map)}">${esc(m.map)}</option>`).join("");
  const urlMap = new URLSearchParams(location.search).get("map");
  const initialMap = mapList.some(m => m.map === urlMap) ? urlMap : mapList[0].map;
  mapSelect.value = initialMap;
  mapSelect.addEventListener("change", () => loadMap(mapSelect.value));

  readColors();
  if (!(await loadMap(initialMap))) {
    return;
  }

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
      const { error, data } = await postLineupQuery({ ...body, map: state.currentMap }, solveController.signal, onSolveProgress);
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

  // capturePreview() borrows the single shared camera/canvas, so two
  // in-flight captures would stomp each other's saved camera state -
  // serialize them behind one chain rather than letting rapid clicks
  // through the lineup list race.
  let previewChain = Promise.resolve();
  function queuePreview(fn) {
    const p = previewChain.then(fn, fn);
    previewChain = p.catch(() => {});
    return p;
  }

  // Renders entirely client-side (capturePreview reuses the shared
  // camera/canvas already in this page), so no server round-trip - just a
  // one-time texture load (size varies by map, 26-92MB) the first time any
  // preview is requested.
  // Cached on the lineup itself so reselecting it (or the same result set
  // surviving a re-render) never re-renders a frame that already exists.
  async function loadPreviewThumb(l, thumbEl) {
    if (l._previewUrl) {
      thumbEl.innerHTML = `<img src="${l._previewUrl}" alt="first-person preview of this lineup">`;
      thumbEl.onclick = () => enlargePreview(l);
      return;
    }
    thumbEl.textContent = "rendering preview…";
    thumbEl.classList.add("loading");
    try {
      const url = await queuePreview(() => capturePreview({ feet: l.feet, type: l.type, pitchDeg: l.pitch, yawDeg: l.yaw }));
      l._previewUrl = url;
      thumbEl.classList.remove("loading");
      thumbEl.innerHTML = `<img src="${url}" alt="first-person preview of this lineup">`;
      thumbEl.onclick = () => enlargePreview(l);
    } catch (err) {
      resetEnsureTexturedScene();
      thumbEl.classList.remove("loading");
      thumbEl.textContent = `preview failed: ${err.message}`;
    }
  }

  function enlargePreview(l) {
    if (!l._previewUrl) {
      return;
    }
    previewImg.src = l._previewUrl;
    previewModal.hidden = false;
  }

  function toggleFavorite(l) {
    l._favorite = !l._favorite;
    renderLineups();
  }

  function removeLineup(l) {
    l._removed = true;
    if (state.selected === l._idx) {
      state.selected = -1;
    }
    renderLineups();
    draw();
    sync3d();
  }

  // Shared by the "3D" button and "Go to" (fly into a lineup's throw spot):
  // both need the mesh loaded and the loop running first. Returns the live
  // bundle on success, null if the load failed or was cancelled mid-flight.
  async function openView3d() {
    stage3d.style.display = "block";
    canvas.style.display = "none";
    document.getElementById("view3d").classList.add("primary");
    statusEl.textContent = "loading 3D mesh…";
    try {
      const t3 = await ensure3d();
      if (stage3d.style.display === "none") { // toggled off while loading
        statusEl.textContent = "";
        return null;
      }
      t3.start();
      sync3d();
      texturedBtn.disabled = false;
      return t3;
    } catch {
      resetEnsure3d();
      stage3d.style.display = "none";
      canvas.style.display = "block";
      document.getElementById("view3d").classList.remove("primary");
      statusEl.textContent = "3D unavailable: serve needs --geo";
      draw();
      return null;
    }
  }

  async function goToLineup(l) {
    const t3 = await openView3d();
    if (!t3) {
      return;
    }
    previewModal.hidden = true;
    t3.flyTo({ feet: l.feet, type: l.type, pitchDeg: l.pitch, yawDeg: l.yaw });
    statusEl.textContent = "dropped into this lineup's throw spot - drag to look, WASD to move";
  }

  initPanel({
    onSetTarget: setTarget, onSelect: select, onPreview: loadPreviewThumb,
    onGoTo: goToLineup, onFavorite: toggleFavorite, onRemove: removeLineup,
  });
  initMap2d({ onSetTarget: setTarget, onSelect: select, onRunQuery: runQuery });
  set3dCallbacks({ onSetTarget: setTarget, onSelect: select });

  document.getElementById("view3d").addEventListener("click", async () => {
    if (stage3d.style.display !== "none") {
      current3d()?.stop();
      stage3d.style.display = "none";
      canvas.style.display = "block";
      document.getElementById("view3d").classList.remove("primary");
      texturedBtn.disabled = true;
      draw();
      return;
    }
    const t3 = await openView3d();
    if (t3) {
      statusEl.textContent = "3D: WASD fly (QE up/down, shift fast) · drag to look · right-drag pan · scroll zoom · click terrain = set target · click marker = pin";
    }
  });

  texturedBtn.addEventListener("click", async () => {
    const t3 = current3d();
    if (!t3) {
      return;
    }
    const wantOn = !t3.isTextured;
    texturedBtn.disabled = true;
    statusEl.textContent = wantOn ? "loading real map textures (one-time, size varies by map)…" : "";
    try {
      await t3.setTextured(wantOn);
      texturedBtn.classList.toggle("primary", wantOn);
      statusEl.textContent = wantOn
        ? "textured: drag to look · right-drag pan · scroll zoom · WASD fly"
        : "3D: WASD fly (QE up/down, shift fast) · drag to look · right-drag pan · scroll zoom · click terrain = set target · click marker = pin";
    } catch (err) {
      resetEnsureTexturedScene();
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
})();

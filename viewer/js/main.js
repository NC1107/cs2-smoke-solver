// Boot and cross-module orchestration. This is the only module allowed to
// import the feature modules; they call back into the orchestrators defined
// here (setTarget, select, runQuery) via the init*/set*Callbacks hooks.

import { state, filtered, esc, lowMemoryDevice } from "./state.js?v=15";
import { loadMapList, loadMapData, runQuery as postLineupQuery, fetchTrajectory, fetchLineupOne, fetchSlack, fetchSpawns, fetchProSmokes } from "./api.js?v=15";
import { loadRadar, readColors, recolorRadar, draw, scheduleDraw, resize, resetView, initMap2d } from "./map2d.js?v=15";
import { ensure3d, resetEnsure3d, teardown3d, current3d, sync3d, syncProgress3d, set3dCallbacks, applyTheme3d } from "./view3d.js?v=15";
import { resetEnsureTexturedScene } from "./textured-scene.js?v=15";
import { capturePreview } from "./preview.js?v=15";
// Every local import across viewer/js carries the SAME ?v= token, bumped
// together on any change. The HTML is served no-cache, so a fresh load pulls
// main.js?v=N, which pulls every module at ?v=N - the whole graph refreshes as
// one consistent set past Cloudflare's 4h JS cache, with no duplicate module
// instances (which a partial versioning would cause). Bump the token everywhere.
import { renderLineups, initPanel, revealSelected, resultStatusText } from "./panel.js?v=15";

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
  const pickBtn = document.getElementById("pick");
  const searchBtn = document.getElementById("search-all");
  const heatBtn = document.getElementById("heat");
  const view3dBtn = document.getElementById("view3d");
  const resetViewBtn = document.getElementById("reset-view");
  const spawnsBtn = document.getElementById("spawns");
  const proSmokesBtn = document.getElementById("prosmokes");
  const proSideSeg = document.getElementById("prosmokes-side");
  const texturedBtn = document.getElementById("textured3d");
  const topDownBtn = document.getElementById("topdown");
  const crosshairBtn = document.getElementById("crosshair3d");
  const collisionBtn = document.getElementById("collision3d");
  const reticleBtn = document.getElementById("reticle3d");
  const viewIcons = document.getElementById("view-icons");
  const rulerEl = document.getElementById("lineup-ruler");
  const clearBtn = document.getElementById("clear");
  const panelEl = document.getElementById("panel");
  const panelCollapseBtn = document.getElementById("panel-collapse");
  const keyEl = document.getElementById("key-dots");
  const mapSelect = document.getElementById("map-select");
  const cancelBtn = document.getElementById("solve-cancel");
  let solveController = null;
  cancelBtn.addEventListener("click", () => solveController?.abort());

  // The 3D crosshair is a preference, not per-session state: someone who
  // hides it to take clean screenshots wants it to stay hidden next visit.
  state.crosshairOn = localStorage.getItem("smokesolver.crosshair3d") !== "0";
  crosshairBtn.addEventListener("click", () => {
    state.crosshairOn = !state.crosshairOn;
    localStorage.setItem("smokesolver.crosshair3d", state.crosshairOn ? "1" : "0");
    syncControls();
    current3d()?.focusStage();
  });
  collisionBtn.addEventListener("click", () => {
    state.collisionOn = !state.collisionOn;
    current3d()?.setCollisionOverlay(state.collisionOn);
    syncControls();
    current3d()?.focusStage();
  });
  reticleBtn.addEventListener("click", () => {
    state.reticleOn = !state.reticleOn;
    buildRuler(rulerEl);
    syncControls();
    current3d()?.focusStage();
  });

  // The full-screen "+" lineup crosshair: a numbered tick ruler (-5..5) on both
  // axes, so a grenade lines up by tick like CS2's grenade crosshair. Built once
  // as percentage-positioned ticks so it scales with the viewport, no resize
  // handler needed.
  function buildRuler(el) {
    if (el.dataset.built) { return; }
    el.dataset.built = "1";
    const frag = document.createDocumentFragment();
    frag.append(
      Object.assign(document.createElement("div"), { className: "rl-h" }),
      Object.assign(document.createElement("div"), { className: "rl-v" }));
    const rl = (cls, css, text) => {
      const d = document.createElement("div");
      d.className = cls;
      d.style.cssText = css;
      if (text !== undefined) { d.textContent = text; }
      return d;
    };
    const UNIT = 8, LEN = 10; // percent per division; tick length in px
    for (let i = -5; i <= 5; i++) {
      if (i === 0) { continue; }
      const x = 50 + i * UNIT, y = 50 - i * UNIT; // right and up are positive
      frag.append(
        rl("rl-tick", `left:${x}%;top:calc(50% - ${LEN / 2}px);width:2px;height:${LEN}px;transform:translateX(-50%)`),
        rl("rl-num", `left:${x}%;top:calc(50% + ${LEN / 2 + 3}px);transform:translateX(-50%)`, String(i)),
        rl("rl-tick", `top:${y}%;left:calc(50% - ${LEN / 2}px);height:2px;width:${LEN}px;transform:translateY(-50%)`),
        rl("rl-num", `top:${y}%;left:calc(50% + ${LEN / 2 + 5}px);transform:translateY(-50%)`, String(i)));
    }
    el.append(frag);
  }

  // Fetched once per lineup and cached on it: a throw's arc is fixed for a given
  // map build, and only the selected one is ever drawn. A failure here is not
  // worth interrupting anyone over - the straight throw-spot-to-landing line is
  // still drawn, it just does not curve.
  async function loadPath(l) {
    if (l._path || l._pathFailed) {
      return;
    }
    try {
      l._path = (await fetchTrajectory(state.currentMap, l)).points;
    } catch {
      l._pathFailed = true;
      return;
    }
    if (state.result?.lineups[state.selected] === l) {
      draw();
      sync3d();
    }
  }

  function select(i) {
    state.selected = i === state.selected ? -1 : i;
    syncUrl();
    renderLineups();
    if (state.selected >= 0) {
      revealSelected();
      loadPath(state.result.lineups[state.selected]);
    }
    draw();
    sync3d();
  }

  // Every control's state is a pure function of what the user has actually done,
  // so it is derived in one place rather than patched from each handler - which
  // is how "Target" ended up looking permanently pressed. A control that cannot
  // do anything yet is absent, not greyed out, so the card reads as a sequence.
  function syncControls() {
    const hasTarget = !!state.target;
    const in3d = stage3d.style.display !== "none";

    // Exactly one control is ever the filled primary: whatever the next step in
    // the sequence is. Pick a target, then search from it, then the tool has
    // nothing to urge and everything goes quiet.
    pickBtn.textContent = state.picking ? "Click the map…" : hasTarget ? "Re-target" : "Target";
    pickBtn.classList.toggle("armed", state.picking);
    pickBtn.classList.toggle("primary", !state.picking && !hasTarget);

    searchBtn.hidden = !hasTarget;
    searchBtn.disabled = state.busy;
    searchBtn.classList.toggle("primary", hasTarget && !state.result && !state.busy);
    clearBtn.hidden = !hasTarget;

    heatBtn.hidden = !state.result?.coverage;
    heatBtn.classList.toggle("active", state.heatOn);
    // The button walks off -> coverage -> stand spots -> off; its label names
    // the view it is currently showing so the cycle is legible.
    heatBtn.textContent = !state.heatOn ? "Heatmap" : state.heatSpots ? "Heatmap: stand spots" : "Heatmap: coverage";
    document.getElementById("key-heat-cover").hidden = state.heatSpots;
    document.getElementById("key-heat-spots").hidden = !state.heatSpots;

    view3dBtn.classList.toggle("active", in3d);
    spawnsBtn.hidden = !(state.spawns && (state.spawns.t.length || state.spawns.ct.length));
    spawnsBtn.classList.toggle("active", state.spawnsOn);
    proSmokesBtn.hidden = !(state.prosmokes && (state.prosmokes.throws.length || state.prosmokes.lands.length));
    proSmokesBtn.classList.toggle("active", state.prosmokesOn);
    // The T/CT filter only makes sense while the heatmap is on.
    proSideSeg.hidden = proSmokesBtn.hidden || !state.prosmokesOn;
    for (const b of proSideSeg.children) {
      b.classList.toggle("active", b.dataset.side === state.proSide);
    }
    // 2D's "recenter" is Reset view; the 3D view controls are an icon strip.
    resetViewBtn.hidden = in3d;
    viewIcons.hidden = !in3d;
    crosshairBtn.classList.toggle("active", state.crosshairOn);
    collisionBtn.classList.toggle("active", state.collisionOn);
    reticleBtn.classList.toggle("active", state.reticleOn);
    document.body.classList.toggle("crosshair-3d", in3d && state.crosshairOn);
    rulerEl.hidden = !(in3d && state.reticleOn);

    // Nothing to explain until there are markers on the map to explain.
    keyEl.hidden = !hasTarget;
    keyEl.classList.toggle("heat", state.heatOn);

    // The 3D controls chip lives on the stage, not in the ephemeral status
    // line, so the bindings stay findable after the status text moves on.
    // Auto-open once per browser; a chip after that.
    const key3d = document.getElementById("key-3d");
    key3d.hidden = !in3d;
    if (in3d && !localStorage.getItem("smokesolver.seen3dHelp")) {
      key3d.open = true;
      localStorage.setItem("smokesolver.seen3dHelp", "1");
    }

    // A collapsed filters card silently hiding active filters would be a trap:
    // the result count would look wrong with no visible cause. A filter that
    // ships pre-set (sky) only counts when moved OFF its default.
    const active = filterEls.filter(f => f.value !== (f.dataset.default ?? "")).length;
    document.getElementById("filter-count").textContent = active ? `(${active})` : "";
    const adv = Object.keys(advancedParams()).length;
    document.getElementById("advanced-count").textContent = adv ? `(${adv})` : "";
  }

  // Heat mode swaps the map key from marker shapes to coverage colors (L23).
  function setHeat(on) {
    state.heatOn = on;
    syncControls();
  }

  // The one definition of "clear the search state": every field added here
  // (picking, results, selection, heat) used to be reset by hand at three
  // call sites, which is exactly how a new field gets missed at one of them.
  function resetSearch({ keepTarget = false } = {}) {
    state.picking = false;
    if (!keepTarget) {
      state.target = null;
    }
    state.result = null;
    state.selected = -1;
    state.heatOn = false;
    state.heatSpots = false;
    canvas.classList.remove("picking");
  }

  // Flag lineups thrown from (near) a player spawn so the card can badge them
  // [spawn] - the "can I smoke this off spawn?" question. The map sweep samples
  // a grid rather than landing exactly on each spawn, so ~48u (about one player
  // width) counts a stand spot as "at" a spawn.
  const SPAWN_LINEUP_RADIUS = 48;
  function tagSpawnLineups(lineups) {
    const spawns = state.spawns ? [...state.spawns.t, ...state.spawns.ct] : [];
    for (const l of lineups) {
      l._spawn = spawns.some(s => Math.hypot(s[0] - l.feet[0], s[1] - l.feet[1]) <= SPAWN_LINEUP_RADIUS);
    }
  }

  // A different map is entirely different geometry/nav/lineups, so this is a
  // full reset (target, results, 3D scene) rather than an incremental swap -
  // there is only ever "the current map," never a per-map cache to switch
  // back into. Returns false (and shows the boot error) if the map's data
  // failed to load, so the caller can bail out the same way initial boot did.
  async function loadMap(name) {
    // A newer loadMap (or any other map-scoped async load) invalidates this one:
    // capture the generation now and bail after each await if it moved, so two
    // overlapping switches can't interleave their commits. The map dropdown fires
    // rapid change events on wheel/arrow, so this is ordinary UI, not an edge case.
    const gen = ++state.mapGeneration;
    solveController?.abort();
    teardown3d();
    stage3d.style.display = "none";
    canvas.style.display = "block";
    texturedBtn.classList.remove("active");
    state.currentMap = name;
    state.spawns = null;
    state.spawnsOn = false;
    state.prosmokes = null;
    state.prosmokesOn = false;
    localStorage.setItem("smokesolver.lastMap", name);
    resetSearch();
    syncUrl();
    syncControls();

    let mapData;
    try {
      mapData = await loadMapData(name);
    } catch {
      if (state.mapGeneration !== gen) { return false; }
      bootError(`data/${name}.viewer-map.json`);
      return false;
    }
    if (state.mapGeneration !== gen) { return false; }
    state.mapData = mapData;
    // Spawns are a bonus overlay: fetch without blocking the map load, and
    // reveal the toggle once they arrive (syncControls hides it when absent).
    fetchSpawns(name).then(s => {
      if (state.mapGeneration !== gen) { return; }
      state.spawns = s;
      // A solve may have finished before spawns arrived; tag and redraw so its
      // spawn lineups get their badge without needing a re-solve.
      if (state.result?.lineups) { tagSpawnLineups(state.result.lineups); renderLineups(); }
      syncControls();
    }).catch(e => console.warn("spawns unavailable for", name, e));
    fetchProSmokes(name).then(d => {
      if (state.mapGeneration === gen) { state.prosmokes = d; syncControls(); }
    }).catch(e => console.warn("pro smokes unavailable for", name, e));
    try {
      await loadRadar();
    } catch {
      if (state.mapGeneration !== gen) { return false; }
      bootError("data/" + state.mapData.image);
      return false;
    }
    if (state.mapGeneration !== gen) { return false; }
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
  // A map with no nav mesh has no walkable ground to sweep, so it can never
  // return a lineup - that is the simulation test bed (flatgrass), not something
  // to offer a player. Still reachable by an explicit ?map= for testing.
  const playable = mapList.filter(m => m.hasLineups);
  mapSelect.innerHTML = playable.map(m => `<option value="${esc(m.map)}">${esc(m.map)}</option>`).join("");
  mapSelect.addEventListener("change", () => loadMap(mapSelect.value));
  readColors();

  // Land somewhere deliberate: an explicit ?map= wins, otherwise the map picked
  // last visit, and only a genuinely first-time visitor gets the intro. Skipping
  // it for returning users is the point - it is onboarding, not a gate.
  const LAST_MAP_KEY = "smokesolver.lastMap";
  const known = name => mapList.some(m => m.map === name);
  const urlMap = new URLSearchParams(location.search).get("map");
  const savedMap = localStorage.getItem(LAST_MAP_KEY);
  const initialMap = known(urlMap) ? urlMap : known(savedMap) ? savedMap : null;
  // ?map=flatgrass is the deliberate escape hatch to the test bed; give the
  // switcher an entry for it so it does not sit there showing the wrong map.
  if (initialMap && !playable.some(m => m.map === initialMap)) {
    mapSelect.insertAdjacentHTML("beforeend", `<option value="${esc(initialMap)}">${esc(initialMap)}</option>`);
  }

  // Minimal modal discipline shared by the intro and the preview modal: focus
  // moves in on open, Tab cycles inside, Escape closes (via the dialog's own
  // close path), and focus returns to whatever opened it. Without this a
  // keyboard or screen-reader user tabs straight into the page behind an
  // open "modal" and never finds it.
  function openModal(el, onEscape) {
    el._returnFocus = document.activeElement;
    el._keydown = e => {
      if (e.key === "Escape") {
        e.stopPropagation();
        onEscape?.();
        return;
      }
      if (e.key !== "Tab") {
        return;
      }
      const focusables = [...el.querySelectorAll("button, [href], select, input, [tabindex]:not([tabindex='-1'])")]
        .filter(f => !f.hidden && f.offsetParent !== null);
      if (focusables.length === 0) {
        return;
      }
      const first = focusables[0], last = focusables.at(-1);
      if (e.shiftKey && document.activeElement === first) {
        e.preventDefault();
        last.focus();
      } else if (!e.shiftKey && document.activeElement === last) {
        e.preventDefault();
        first.focus();
      }
    };
    el.addEventListener("keydown", el._keydown);
    el.hidden = false;
    (el.querySelector("button, [href], select, input") ?? el).focus();
  }
  function closeModal(el) {
    el.hidden = true;
    el.removeEventListener("keydown", el._keydown);
    el._returnFocus?.focus?.();
    el._returnFocus = null;
  }

  const intro = document.getElementById("intro");
  const introMapStep = document.getElementById("intro-map");
  const introFilterStep = document.getElementById("intro-filters");
  const filterBody = document.getElementById("filter-body");

  // The intro borrows the real <select> elements rather than cloning them, so
  // there is only ever one source of truth for a filter's value; they are handed
  // back to the sidebar card, in order, when it closes. Which means the borrowing
  // must only happen if the intro is actually going to be shown - doing it up
  // front left every returning visitor, and every ?map= link, with the filters
  // stranded inside a hidden dialog and an empty filters card.
  const filterEls = Object.values(state.filters);
  const filterRowsHtml = () => filterEls
    .map(f => `<div class="filter-row" data-for="${f.id}">` +
      `<label class="filter-head" for="${f.id}">` +
      `<b class="filter-info" tabindex="0" role="button" aria-label="What does ${esc(f.dataset.label)} do?">${esc(f.dataset.label)}:</b>` +
      `<span class="filter-slot"></span></label>` +
      `<p class="filter-desc">${esc(f.dataset.desc)}</p></div>`)
    .join("");
  // The label+description rows render in the sidebar too, permanently - they
  // used to exist only inside the intro, so the one explanation of what
  // "reliability" or "sky aim" means vanished forever the moment it closed
  // (and returning visitors never saw it at all).
  // Each title carries a dotted underline; its explanation pops up on hover or
  // keyboard focus, and a click pins it open (touch has no hover). One delegated
  // set of handlers serves every context (sidebar, intro, advanced card).
  const descOf = el => el.closest(".filter-row")?.querySelector(".filter-desc");
  // Only ever one explanation open at a time - stacking them (as happened on
  // touch, where tapping several labels pinned several popups) was unreadable.
  // Visibility rides the .open class so it can fade+slide in via CSS rather than
  // snapping on. A pinned one (tapped open) survives the pointer leaving.
  const closeAllDescs = except => {
    for (const d of document.querySelectorAll(".filter-desc.open")) {
      if (d !== except) { d.classList.remove("open", "pinned"); }
    }
  };
  const showDesc = el => { const d = descOf(el); if (d) { closeAllDescs(d); d.classList.add("open"); } };
  const hideDesc = el => { const d = descOf(el); if (d && !d.classList.contains("pinned")) { d.classList.remove("open"); } };
  document.addEventListener("pointerover", e => {
    if (e.target instanceof Element && e.target.classList.contains("filter-info")) { showDesc(e.target); }
  });
  document.addEventListener("pointerout", e => {
    if (e.target instanceof Element && e.target.classList.contains("filter-info")) { hideDesc(e.target); }
  });
  document.addEventListener("focusin", e => {
    if (e.target instanceof Element && e.target.classList.contains("filter-info")) { showDesc(e.target); }
  });
  document.addEventListener("focusout", e => {
    if (e.target instanceof Element && e.target.classList.contains("filter-info")) { hideDesc(e.target); }
  });
  document.addEventListener("click", e => {
    if (!(e.target instanceof Element) || !e.target.classList.contains("filter-info")) {
      // A tap anywhere else dismisses a pinned explanation.
      closeAllDescs(null);
      return;
    }
    e.preventDefault();
    const desc = descOf(e.target);
    if (desc) {
      const pin = !desc.classList.contains("pinned");
      closeAllDescs(desc);
      desc.classList.toggle("pinned", pin);
      desc.classList.toggle("open", pin);
    }
  });
  filterBody.innerHTML = filterRowsHtml();
  const slotFor = (container, f) => container.querySelector(`.filter-row[data-for="${f.id}"] .filter-slot`);
  for (const f of filterEls) {
    slotFor(filterBody, f).appendChild(f);
  }
  function mountIntroFilters() {
    document.getElementById("intro-filter-rows").innerHTML = filterRowsHtml();
    for (const f of filterEls) {
      slotFor(document.getElementById("intro-filter-rows"), f).appendChild(f);
    }
  }

  function closeIntro() {
    for (const f of filterEls) {
      slotFor(filterBody, f).appendChild(f);
    }
    closeModal(intro);
    syncControls();
    statusEl.textContent = "click anywhere on the map to set your smoke target";
  }

  document.getElementById("intro-map-grid").innerHTML = playable
    .map(m => `<button type="button" class="map-pick" data-map="${esc(m.map)}" title="${esc(m.map)}">` +
      // Root-absolute: a url() inside a custom property resolves against the
      // stylesheet that consumes it (viewer/app.css), not the document.
      `<span class="thumb" style="--thumb:url('/data/${esc(m.map)}.thumb.png')"></span>` +
      `${esc(m.map.replace(/^de_/, ""))}</button>`)
    .join("");
  for (const b of document.querySelectorAll(".map-pick")) {
    b.addEventListener("click", async () => {
      if (!(await loadMap(b.dataset.map))) {
        closeIntro();
        return;
      }
      mapSelect.value = b.dataset.map;
      introMapStep.hidden = true;
      introFilterStep.hidden = false;
    });
  }
  document.getElementById("intro-done").addEventListener("click", closeIntro);
  document.getElementById("intro-back").addEventListener("click", () => {
    introFilterStep.hidden = true;
    introMapStep.hidden = false;
  });

  if (initialMap) {
    mapSelect.value = initialMap;
    const bootParams = new URLSearchParams(location.search);
    if (!(await loadMap(initialMap))) {
      return;
    }
    applyPermalink(bootParams);
  } else {
    mountIntroFilters();
    // Escape may only skip the intro once a map exists - it is the map picker,
    // and closing it with nothing loaded would strand the page empty.
    openModal(intro, () => { if (state.mapData) { closeIntro(); } });
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
      // Naming what is being counted matters: the total is every spot a player
      // can stand within throw range of the target, not the whole map, which is
      // why it lands near the same figure regardless of how big the map is.
      const ofTotal = p.total ? ` / ${p.total}` : "";
      statusEl.textContent = `checked ${p.checked.length}${ofTotal} stand spots in throw range…`;
    } else if (msg.verified) {
      p.verified.push(...msg.verified);
      statusEl.textContent = `verifying ${p.verified.length} / ${p.candidates ?? "?"} candidates against the exact sim…`;
    }
    scheduleDraw();
    syncProgress3d();
  }

  // Solver-side knobs from the advanced card: unlike the display filters,
  // these change what gets COMPUTED, so they ride along with each solve
  // request. Only non-defaults are sent; the server owns the defaults.
  function advancedParams() {
    const p = {};
    const tol = document.getElementById("a-tolerance").value;
    if (tol) { p.tolerance = Number.parseFloat(tol); }
    const reach = document.getElementById("a-reach").value;
    if (reach) { p.originReach = Number.parseFloat(reach); }
    const stab = document.getElementById("a-stability").value;
    if (stab) { p.minStability = Number.parseFloat(stab); }
    if (document.getElementById("a-scan").value === "fine") { p.fineScan = true; }
    return p;
  }

  // Rough solve-cost multiplier of the current advanced settings: fine scan
  // sweeps ~3x the angles, and probe origins grow with the square of the
  // search radius. Used only to warn, never to block.
  function advancedCostFactor() {
    const p = advancedParams();
    let factor = p.fineScan ? 3 : 1;
    if (p.originReach) {
      factor *= Math.max((p.originReach / 300) ** 2, 0.1);
    }
    return factor;
  }
  function advancedCostNote() {
    const factor = advancedCostFactor();
    const note = document.getElementById("advanced-note");
    note.textContent = "These change how the solver searches - they apply to your next Search / spot click." +
      (factor >= 2 ? ` Current settings make each solve roughly ${Math.round(factor)}x slower.` : "");
  }

  // The movement and click filters, when set BEFORE solving, scope the sweep
  // itself: the solver skips every other type/strength combination from every
  // origin (up to 15x less work), instead of computing everything and hiding
  // most of it. state.solveScope remembers it so the status line can say the
  // results were solved narrow.
  function solveScopeParams() {
    const p = {};
    const type = state.filters.type.value;
    if (type) { p.types = [type]; }
    const strength = state.filters.strength.value;
    if (strength) { p.strengths = [Number.parseFloat(strength)]; }
    return p;
  }

  async function runQuery(body) {
    state.busy = true;
    state.progress = { phase: "sweep", total: 0, candidates: 0, checked: [], verified: [] };
    const cost = advancedCostFactor();
    statusEl.textContent = cost >= 2 ? `solving… (advanced settings ≈ ${Math.round(cost)}x slower)` : "solving…";
    solveController = new AbortController();
    cancelBtn.hidden = false;
    try {
      const scope = solveScopeParams();
      state.solveScope = Object.keys(scope).length ? scope : null;
      const { error, data } = await postLineupQuery({ ...advancedParams(), ...scope, ...body, map: state.currentMap }, solveController.signal, onSolveProgress);
      if (error) {
        statusEl.textContent = error;
        return;
      }
      const next = data;
      if (next.lineups.length === 0) {
        // A single-origin probe checked one spot, not the map; saying "any of
        // N stand spots" for it would wrongly read as an exhaustive sweep.
        statusEl.textContent = body.origin
          ? `no throw from that spot reaches the target - "Search map" sweeps every spot that can`
          : `no throw reaches there from any of the ${next.origins} stand spots in range - try another target`;
        return;
      }
      next.lineups.forEach((l, i) => { l._idx = i; });
      tagSpawnLineups(next.lineups);
      state.result = next;
      // Adopt the server's resolved target, which carries the ground Z it snapped
      // a 2D (Z-less) pick onto. Keeps the 2D and 3D target at the same height the
      // sim actually used, so switching views no longer moves or floats it.
      if (Array.isArray(next.target) && next.target.length > 2) {
        state.target = next.target;
        syncUrl();
      }
      state.selected = -1;
      renderLineups();
      sync3d();
    } catch (err) {
      statusEl.textContent = err.name === "AbortError" ? "cancelled" : `error: ${err.message}`;
    } finally {
      state.busy = false;
      state.progress = null;
      solveController = null;
      cancelBtn.hidden = true;
      syncControls();
      draw();
      syncProgress3d();
    }
  }

  // Shareable permalinks. t=x,y,z is the resolved target; l identifies the
  // selected lineup by its PHYSICAL parameters (type:strength:fx:fy:fz:
  // pitch:yaw) - result indices are unstable across data redeploys, the
  // physics is the lineup's identity. The address bar always holds a valid
  // share link for the current view.
  function permalink() {
    const p = new URLSearchParams({ map: state.currentMap });
    if (state.target) {
      p.set("t", state.target.map(v => Math.round(v * 10) / 10).join(","));
    }
    const l = state.selected >= 0 ? state.result?.lineups[state.selected] : null;
    if (l) {
      // The run direction is part of the physics identity; appended only when
      // set so links minted before it existed keep their exact old shape.
      const parts = [l.type, l.strength, ...l.feet.map(v => Math.round(v * 10) / 10), l.pitch.toFixed(2), l.yaw.toFixed(2)];
      if (l.runDeg) {
        parts.push(l.runDeg.toFixed(0));
      }
      p.set("l", parts.join(":"));
    }
    return `${location.pathname}?${p}`;
  }
  function syncUrl() {
    history.replaceState(null, "", permalink());
  }

  async function shareLineup() {
    const url = location.origin + permalink();
    if (navigator.share) {
      try {
        await navigator.share({ url });
        return;
      } catch {
        // Cancelled share sheets fall through to the clipboard path.
      }
    }
    try {
      await navigator.clipboard.writeText(url);
      statusEl.textContent = "link copied - it opens this exact lineup";
    } catch {
      // Clipboard denied/unfocused: show the URL so the link is still gettable.
      statusEl.textContent = `copy failed - link: ${url}`;
    }
  }

  // Landing on a shared link: set the target, then render the ONE throw the
  // link fully describes - no sweeping the map for other spots the sharer did
  // not point at. A target-only link (no `l`) just lands on the target with
  // "Search map" queued up.
  async function applyPermalink(params) {
    const t = params.get("t")?.split(",").map(Number);
    if (!t || t.length < 2 || t.some(v => !Number.isFinite(v))) {
      return;
    }
    setTarget(t, `target ${t[0].toFixed(0)}, ${t[1].toFixed(0)} (from link)`);
    const parts = params.get("l")?.split(":");
    if (!parts || parts.length < 7 || parts.length > 8) {
      return;
    }
    const type = parts[0];
    const [strength, fx, fy, fz, pitch, yaw] = parts.slice(1, 7).map(Number);
    const runDeg = parts.length === 8 ? Number(parts[7]) : 0;
    if ([strength, fx, fy, fz, pitch, yaw, runDeg].some(v => !Number.isFinite(v))) {
      return;
    }
    await showSingleLineup({ type, strength, feet: [fx, fy, fz], pitch, yaw, runDeg });
  }

  // Render exactly one lineup from its physical spec: the server analyzes just
  // that throw (path, rest, aim reference, pin, scatter) and the viewer shows
  // it selected, bypassing the filters since it is an explicit pick. "Search
  // map" is still one click away for anyone who wants the alternatives.
  async function showSingleLineup(spec) {
    state.busy = true;
    statusEl.textContent = "loading the shared lineup…";
    syncControls();
    try {
      const { points, lineup } = await fetchLineupOne(state.currentMap, state.target, spec);
      lineup._idx = 0;
      lineup._path = points; // pre-set so select() draws without a second fetch
      state.result = { target: [...state.target], origins: 0, coverage: null, lineups: [lineup], single: true };
      state.selected = -1;
      select(0);
      statusEl.textContent = `showing the shared lineup · "Search map" finds other spots for this target`;
    } catch (err) {
      statusEl.textContent = err.name === "AbortError"
        ? "cancelled"
        : `could not load the shared lineup (${err.message}) - "Search map" to solve the target`;
    } finally {
      state.busy = false;
      syncControls();
    }
  }

  function setTarget(t, note) {
    state.target = t;
    resetSearch({ keepTarget: true });
    syncUrl();
    // Lead with the action most users want next (the full sweep); the
    // narrower solve-one-spot click is the refinement, not the default.
    statusEl.textContent = `${note} - "Search map" finds every throw spot · or click one spot to solve just it · right-click/long-press moves the target`;
    syncControls();
    renderLineups();
    draw();
    sync3d();
  }

  pickBtn.addEventListener("click", () => {
    state.picking = !state.picking;
    canvas.classList.toggle("picking", state.picking);
    statusEl.textContent = state.picking ? "click the map to place your smoke target (Esc cancels)" : "";
    syncControls();
  });
  document.addEventListener("keydown", e => {
    if (e.key === "Escape" && state.picking) {
      state.picking = false;
      canvas.classList.remove("picking");
      statusEl.textContent = "";
      syncControls();
    }
  });
  resetViewBtn.addEventListener("click", resetView);
  spawnsBtn.addEventListener("click", () => {
    state.spawnsOn = !state.spawnsOn;
    spawnsBtn.classList.toggle("active", state.spawnsOn);
    draw();
    sync3d();
  });
  // Collapse the results panel out of the way to see the map beneath it. A fresh
  // solve re-expands it (panel.js) since the point of searching is to see them.
  panelCollapseBtn.addEventListener("click", () => {
    const collapsed = panelEl.classList.toggle("collapsed");
    panelCollapseBtn.setAttribute("aria-expanded", collapsed ? "false" : "true");
    panelCollapseBtn.title = collapsed ? "Show results" : "Hide results";
    panelCollapseBtn.setAttribute("aria-label", collapsed ? "Show results" : "Hide results");
  });
  proSmokesBtn.addEventListener("click", () => {
    state.prosmokesOn = !state.prosmokesOn;
    syncControls();
    draw();
  });
  proSideSeg.addEventListener("click", (e) => {
    const btn = e.target.closest(".seg-btn");
    if (!btn) {
      return;
    }
    state.proSide = btn.dataset.side;
    syncControls();
    draw();
  });
  searchBtn.addEventListener("click", () => {
    if (state.target && !state.busy) {
      statusEl.textContent = "searching map…";
      runQuery({ target: state.target });
    }
  });
  clearBtn.addEventListener("click", () => {
    resetSearch();
    syncUrl();
    statusEl.textContent = "";
    syncControls();
    renderLineups();
    draw();
  });
  heatBtn.addEventListener("click", () => {
    // Cycle: off -> coverage -> stand spots -> off.
    if (!state.heatOn) {
      state.heatSpots = false;
      setHeat(true);
    } else if (!state.heatSpots) {
      state.heatSpots = true;
      syncControls();
    } else {
      state.heatSpots = false;
      setHeat(false);
    }
    statusEl.textContent = !state.heatOn ? resultStatusText(filtered().length)
      : state.heatSpots
        ? "stand spots: bright = corner-pinned with a verified lineup, mid = wall-pinned, faint = open ground - see legend"
        : "heatmap: where a throw reaches, and where nothing does - see legend · click again for stand-spot quality";
    draw();
  });
  for (const el of ["a-tolerance", "a-reach", "a-stability", "a-scan"].map(id => document.getElementById(id))) {
    el.addEventListener("change", () => {
      syncControls();
      advancedCostNote();
      const factor = advancedCostFactor();
      statusEl.textContent = "advanced settings apply to your next Search / spot click" +
        (factor >= 2 ? ` - roughly ${Math.round(factor)}x slower` : "");
    });
  }
  for (const f of Object.values(state.filters)) {
    f.addEventListener("change", () => {
      state.selected = -1;
      syncControls();
      renderLineups();
      draw();
      sync3d();
    });
  }

  const previewModal = document.getElementById("preview-modal");
  const previewImg = document.getElementById("preview-img");
  document.getElementById("preview-close").addEventListener("click", () => closeModal(previewModal));
  previewModal.addEventListener("click", e => { if (e.target === previewModal) { closeModal(previewModal); } });

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

  // Rendering a preview pulls in the textured GLB. Even the smaller mobile tier
  // (~120-200MB decoded) is worth loading only on a deliberate tap on a phone,
  // never automatically - a shared-lineup link auto-selects its one lineup on
  // load, so an automatic heavy load there could still stutter or, on the
  // weakest devices, reload. So on touch / low-memory devices the preview is an
  // explicit tap, never automatic, and the heavy load only follows a deliberate
  // action (never a page load). Same device test that picks the mobile GLB tier.
  const heavyPreviewRisk = lowMemoryDevice;

  function previewTapButton(l, thumbEl, label) {
    thumbEl.classList.remove("loading");
    thumbEl.innerHTML = `<button type="button" class="preview-load">${label}</button>`;
    thumbEl.querySelector("button").onclick = () => {
      l._previewRequested = true;
      loadPreviewThumb(l, thumbEl);
    };
  }

  // Renders entirely client-side (capturePreview reuses the shared
  // camera/canvas already in this page), so no server round-trip.
  // Cached on the lineup itself so reselecting it (or the same result set
  // surviving a re-render) never re-renders a frame that already exists.
  async function loadPreviewThumb(l, thumbEl) {
    if (l._previewUrl) {
      thumbEl.innerHTML = `<img src="${l._previewUrl}" alt="first-person preview of this lineup">`;
      thumbEl.onclick = () => enlargePreview(l);
      return;
    }
    if (heavyPreviewRisk && !l._previewRequested) {
      previewTapButton(l, thumbEl, "Tap to load preview");
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
      l._previewRequested = false;
      if (heavyPreviewRisk) {
        previewTapButton(l, thumbEl, "Preview failed - tap to retry");
      } else {
        thumbEl.classList.remove("loading");
        thumbEl.textContent = `preview failed: ${err.message}`;
      }
    }
  }

  function enlargePreview(l) {
    if (!l._previewUrl) {
      return;
    }
    previewImg.src = l._previewUrl;
    openModal(previewModal, () => closeModal(previewModal));
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
    syncControls();
    statusEl.textContent = "loading 3D mesh…";
    try {
      const t3 = await ensure3d();
      // Stage hidden = toggled off or map switched mid-load; t3 undefined =
      // init3d bailed because the map changed. Either way, abandon quietly.
      if (!t3 || stage3d.style.display === "none") {
        statusEl.textContent = "";
        return null;
      }
      t3.start();
      sync3d();
      syncProgress3d();
      t3.focusStage();
      return t3;
    } catch {
      resetEnsure3d();
      stage3d.style.display = "none";
      canvas.style.display = "block";
      syncControls();
      statusEl.textContent = "3D unavailable - this browser could not start WebGL, or the map mesh failed to load";
      draw();
      return null;
    }
  }

  // The precision the accuracy ring is judged against: the precision filter
  // when set, otherwise whatever landing tolerance the solve itself used.
  function slackWithin() {
    return Number.parseFloat(state.filters.precision.value) ||
      Number.parseFloat(document.getElementById("a-tolerance").value) || 80;
  }

  async function goToLineup(l) {
    const t3 = await openView3d();
    if (!t3) {
      return;
    }
    if (!previewModal.hidden) {
      closeModal(previewModal);
    }
    t3.flyTo({ feet: l.feet, type: l.type, pitchDeg: l.pitch, yawDeg: l.yaw });
    statusEl.textContent = "dropped into this lineup's throw spot - drag to look, WASD to move";
    // The accuracy ring: how far the feet can drift before this exact aim
    // stops landing within the precision in play. Fetched lazily and cached
    // on the lineup; a failure only costs the ring, never the Go to itself.
    try {
      const within = slackWithin();
      if (l._slack?.within !== within) {
        l._slack = await fetchSlack(state.currentMap, l, state.target, within);
      }
      draw();
      sync3d();
      const rs = l._slack.dirs.map(d => d[1]);
      const rmax = Math.max(...rs);
      statusEl.textContent = rmax < 1
        ? `stand EXACTLY here - even 1u of drift stops this aim landing within ${within}u`
        : `the ring shows how far you can stand from this spot and still land within ${within}u ` +
          `(${Math.min(...rs).toFixed(0)}-${rmax.toFixed(0)}u of slack) - drag to look, WASD to move`;
    } catch {
      // Ring unavailable; the camera drop already succeeded.
    }
  }

  initPanel({
    onSetTarget: setTarget, onSelect: select, onPreview: loadPreviewThumb,
    onGoTo: goToLineup, onFavorite: toggleFavorite, onRemove: removeLineup,
    onShare: shareLineup,
  });
  initMap2d({ onSetTarget: setTarget, onSelect: select, onRunQuery: runQuery });
  set3dCallbacks({ onSetTarget: setTarget, onSelect: select, onRunQuery: runQuery });

  // Derived, not constant: "click terrain" means set-target only until a
  // target exists, then it means solve-from-here - a static string was
  // telling users the wrong thing for most of the session. Space/Ctrl leads
  // because that is CS2's own spectator freecam pair; Q/E stay as aliases.
  // Touch has no WASD/scroll/right-click, so a phone gets the short gesture
  // hint (which otherwise wraps to three wasted lines above the map).
  const coarsePointer = matchMedia("(pointer: coarse)").matches;
  const hint3d = () => coarsePointer
    ? "3D: 1 finger look · 2 fingers pan/zoom · " + (state.target ? "tap terrain = solve there · long-press = move target" : "tap terrain = set target")
    : "3D: WASD fly (Space/Ctrl up/down, Shift fast) · drag look · right-drag pan · scroll dolly · " +
      (state.target ? "click terrain = solve from that spot · right-click = move target" : "click terrain = set target");

  topDownBtn.addEventListener("click", () => {
    const t3 = current3d();
    if (!t3) {
      return;
    }
    t3.topDown();
    // Otherwise focus stays on the button, and the fly keys - which ignore
    // anything typed at a button - stay dead until the view is clicked.
    t3.focusStage();
    statusEl.textContent = "looking straight down at the map";
  });

  view3dBtn.addEventListener("click", async () => {
    if (stage3d.style.display !== "none") {
      current3d()?.stop();
      stage3d.style.display = "none";
      canvas.style.display = "block";
      syncControls();
      draw();
      // The 3D "WASD fly…" hint is meaningless in 2D; restore the 2D status
      // (result summary if a solve is up, otherwise clear it).
      statusEl.textContent = state.result && !state.heatOn ? resultStatusText(filtered().length) : "";
      return;
    }
    const t3 = await openView3d();
    if (t3) {
      statusEl.textContent = hint3d();
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
      texturedBtn.classList.toggle("active", wantOn);
      statusEl.textContent = hint3d();
    } catch (err) {
      resetEnsureTexturedScene();
      statusEl.textContent = `failed to load textures: ${err.message}`;
    } finally {
      texturedBtn.disabled = false;
      t3.focusStage();
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

  // A plain window resize (desktop drag, phone rotation) changes the canvas CSS
  // size but not its backing store, so the 2D map drew stale/blank until the
  // next interaction. Re-fit the backing store to the new size on resize; the
  // 3D view keeps its own resize handler. Coalesced to one redraw per frame.
  let resizeQueued = false;
  window.addEventListener("resize", () => {
    if (resizeQueued) { return; }
    resizeQueued = true;
    requestAnimationFrame(() => { resizeQueued = false; resize(); });
  });

  // Below the breakpoint the actions card collapses to a <details>; CSS cannot
  // force a closed details open again at desktop width, so sync it here. Filters
  // are excluded on purpose - that one is the user's to open and close.
  const compactMq = matchMedia("(max-width: 640px)");
  const syncCompactControls = () => {
    document.getElementById("card-view").open = !compactMq.matches;
  };
  syncCompactControls();
  compactMq.addEventListener("change", syncCompactControls);
})();

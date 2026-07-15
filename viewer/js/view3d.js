// ---------- 3D view (three.js, collision mesh) ----------
// three.js stays a lazily loaded window global (THREE); this module only
// wraps init/sync. Raycast picks route through callbacks that main.js
// registers, so this module never imports the orchestrator.

import { state, filtered, clickClass, SMOKE_BLOOM_RADIUS, EYE_HEIGHT_BY_TYPE, DEFAULT_EYE_HEIGHT } from "./state.js";
import { fetchMesh } from "./api.js";
import { createFlyCamera } from "./flycam.js";
import { loadScript, ensureTexturedScene, currentTexturedScene, disposeSceneContents, disposeTexturedScene } from "./textured-scene.js";

const stage3d = state.stage3d;
let three = null;
let threePromise = null; // memoized in-flight init so re-toggles share one

let callbacks = {
  onSelect: () => {},
  onSetTarget: () => {},
  onRunQuery: () => {},
};
export function set3dCallbacks(cb) {
  callbacks = cb;
}

export function ensure3d() {
  threePromise ??= init3d();
  return threePromise;
}
// Clears the memoized (rejected) init so the next toggle retries from scratch.
export function resetEnsure3d() {
  threePromise = null;
}
// The live bundle, or null before the first successful init.
export function current3d() {
  return three;
}


// A different map means entirely different geometry/nav/lineups, so the
// whole WebGL scene (built once in init3d() for whichever mesh was current
// at the time) is stale and must be rebuilt from scratch rather than kept
// across a map switch - there is no per-map cache here, only ever "the
// current one." Disposing GPU resources explicitly matters because browsers
// cap the number of live WebGL contexts; switching maps many times in one
// page session without this would eventually stop being able to create new
// ones.
export function teardown3d() {
  if (three) {
    three.stop();
    disposeSceneContents(three.scene);
    three.renderer.dispose();
    three.renderer.domElement.remove();
  }
  three = null;
  threePromise = null;
  disposeTexturedScene();
}


async function init3d() {
  // three.js is opt-in, so keep its ~740 KB off the 2D-only load path.
  await loadScript("viewer/lib/three.min.js");
  const buf = await fetchMesh(state.currentMap);
  const dv = new DataView(buf);
  const vCount = dv.getInt32(0, true);
  const iCount = dv.getInt32(4, true);
  const verts = new Float32Array(buf, 8, vCount * 3);
  const idx = new Uint32Array(buf, 8 + vCount * 12, iCount);

  const colors = state.colors;
  const [RX0, RY0, RX1, RY1] = state.mapData.region;

  const renderer = new THREE.WebGLRenderer({ antialias: true });
  renderer.setPixelRatio(window.devicePixelRatio);
  // Focusable so the fly keys have somewhere to live. Clicking a 3D control
  // leaves focus on that button, and the camera module deliberately ignores
  // keys aimed at a button - which is why WASD went dead after pressing
  // Top-down until you clicked the view again.
  renderer.domElement.tabIndex = 0;
  stage3d.appendChild(renderer.domElement);
  const scene = new THREE.Scene();
  scene.background = new THREE.Color(colors.surface);
  // The same FOV the game renders at, not an arbitrary one. A lineup is aimed by
  // eye, against whatever the crosshair happens to sit on, so a view that is
  // more zoomed-in than CS2's puts that geometry somewhere else relative to the
  // crosshair and the lineup cannot be copied off the screen.
  const camera = new THREE.PerspectiveCamera(verticalFovFromDesired(90), 1, 10, 40000);
  camera.up.set(0, 0, 1);
  const cx = (RX0 + RX1) / 2, cy = (RY0 + RY1) / 2;
  camera.position.set(cx, cy - 3800, 3600);
  camera.lookAt(cx, cy, 0);


  const geo = new THREE.BufferGeometry();
  geo.setAttribute("position", new THREE.BufferAttribute(verts, 3));
  geo.setIndex(new THREE.BufferAttribute(idx, 1));
  geo.computeVertexNormals();
  // Height-tinted vertex colors so floors and rooftops read at a glance.
  let zLo = Infinity, zHi = -Infinity;
  for (let i = 2; i < verts.length; i += 3) {
    const z = verts[i];
    if (z < zLo) { zLo = z; }
    if (z > zHi && z < 600) { zHi = z; }
  }
  const lo = new THREE.Color(colors["terrain-lo"]), hi = new THREE.Color(colors["terrain-hi"]);
  const cols = new Float32Array(verts.length);
  const tmp = new THREE.Color();
  for (let i = 0; i < verts.length; i += 3) {
    const t = Math.min(1, Math.max(0, (verts[i + 2] - zLo) / (zHi - zLo)));
    tmp.copy(lo).lerp(hi, 0.15 + 0.55 * t);
    cols[i] = tmp.r; cols[i + 1] = tmp.g; cols[i + 2] = tmp.b;
  }
  geo.setAttribute("color", new THREE.BufferAttribute(cols, 3));
  const mat = new THREE.MeshLambertMaterial({ vertexColors: true, side: THREE.DoubleSide });
  const collisionVisual = new THREE.Mesh(geo, mat);
  scene.add(collisionVisual);
  scene.add(new THREE.HemisphereLight(0xffffff, 0x33302a, 0.95));
  const sun = new THREE.DirectionalLight(0xffffff, 0.7);
  sun.position.set(0.4, 0.25, 1);
  scene.add(sun);

  const markerGroup = new THREE.Group();
  scene.add(markerGroup);
  const targetGroup = new THREE.Group();
  scene.add(targetGroup);
  const progressGroup = new THREE.Group();
  scene.add(progressGroup);

  // Shared marker assets: sync3d re-parents these instead of allocating new
  // GPU buffers per rebuild (three.js never frees them on plain .remove()).
  const markerGeo = new THREE.SphereGeometry(14, 10, 8);
  const markerMats = {
    left: new THREE.MeshBasicMaterial({ color: colors["click-left"] }),
    mid: new THREE.MeshBasicMaterial({ color: colors["click-mid"] }),
    right: new THREE.MeshBasicMaterial({ color: colors["click-right"] }),
  };
  const targetGeo = new THREE.SphereGeometry(10, 16, 12);
  const targetMat = new THREE.MeshBasicMaterial({ color: colors.target });
  const bloomGeo = new THREE.SphereGeometry(1, 24, 16); // unit sphere, scaled to the zone radius
  const bloomMat = new THREE.MeshBasicMaterial({ color: colors.target, transparent: true, opacity: 0.14, depthWrite: false });
  const lineMat = new THREE.LineBasicMaterial({ color: colors.accent });
  // A sweep evaluates tens of thousands of origins and restreams every ~100ms,
  // so the live progress dots are Points clouds (one draw call each) rather
  // than a mesh per origin, which would stall the view exactly when it is
  // meant to be showing that the solver is making progress.
  const progressCheckedMat = new THREE.PointsMaterial({
    size: 22, vertexColors: true, transparent: true, opacity: 0.6, depthWrite: false });
  const progressVerifiedMat = new THREE.PointsMaterial({
    size: 40, color: colors["heat-ok"], transparent: true, opacity: 0.95, depthWrite: false });

  const raycaster = new THREE.Raycaster();
  const meshObj = scene.children.find(o => o.isMesh);

  // Ground height at a horizontal point, by dropping a ray onto the collision
  // mesh. Lets the target dot sit on the surface when it was picked in 2D (which
  // carries no Z) instead of floating at world zero. Always the collision mesh,
  // never the textured GLB, so it reports the true surface the sim uses.
  const dropRay = new THREE.Raycaster();
  const straightDown = new THREE.Vector3(0, 0, -1);
  function surfaceZAt(x, y) {
    dropRay.set(new THREE.Vector3(x, y, 20000), straightDown);
    const hit = dropRay.intersectObject(meshObj, false);
    return hit.length > 0 ? hit[0].point.z : null;
  }



  // The terrain point under a screen position, or null. Markers win when
  // `preferMarkers` so tapping a dot near the ground selects, not re-solves.
  function pickAt(clientX, clientY, preferMarkers) {
    const rect = renderer.domElement.getBoundingClientRect();
    const ndc = new THREE.Vector2(
      ((clientX - rect.left) / rect.width) * 2 - 1,
      -((clientY - rect.top) / rect.height) * 2 + 1);
    raycaster.setFromCamera(ndc, camera);
    if (preferMarkers) {
      const mhits = raycaster.intersectObjects(markerGroup.children, false);
      if (mhits.length > 0 && mhits[0].object.userData.idx !== undefined) {
        return { markerIdx: mhits[0].object.userData.idx };
      }
    }
    const hits = raycaster.intersectObject(meshObj, false);
    return hits.length > 0 ? { point: hits[0].point } : null;
  }
  function setTargetAt(clientX, clientY) {
    const hit = pickAt(clientX, clientY, false);
    if (hit?.point) {
      const p = hit.point;
      callbacks.onSetTarget([p.x, p.y, p.z], `target ${p.x.toFixed(0)}, ${p.y.toFixed(0)}, ${p.z.toFixed(0)} (3D)`);
    }
  }
  const cam = createFlyCamera(camera, renderer.domElement, {
    requestRender: () => { dirty = true; },
    onLongPress: setTargetAt,
    // A tap's MEANING lives here, not in the camera module: right-click (or
    // long-press above) always sets/moves the target; a plain click selects a
    // marker, bootstraps the first target, or probes a throw origin.
    onTap: ({ x, y, button }) => {
      if (button === 2) {
        setTargetAt(x, y);
        return;
      }
      const hit = pickAt(x, y, true);
      if (!hit) { return; }
      if (hit.markerIdx !== undefined) {
        callbacks.onSelect(hit.markerIdx);
        return;
      }
      const pnt = hit.point;
      if (state.picking || !state.target) {
        callbacks.onSetTarget([pnt.x, pnt.y, pnt.z], `target ${pnt.x.toFixed(0)}, ${pnt.y.toFixed(0)}, ${pnt.z.toFixed(0)} (3D)`);
      } else if (!state.heatOn) {
        callbacks.onRunQuery({ target: state.target, origin: [pnt.x, pnt.y, pnt.z] });
      }
    },
    ignoreKeys: e => e.target instanceof Element &&
      e.target.closest("#controls, .panel, input, select, button, summary, details"),
  });

  function resize3d() {
    const w2 = stage3d.clientWidth, h2 = stage3d.clientHeight;
    if (w2 === 0) { return; }
    // Re-read DPR: it changes when the window moves between monitors (M45).
    if (renderer.getPixelRatio() !== window.devicePixelRatio) {
      renderer.setPixelRatio(window.devicePixelRatio);
    }
    renderer.setSize(w2, h2);
    camera.aspect = w2 / h2;
    camera.updateProjectionMatrix();
    dirty = true;
  }
  window.addEventListener("resize", resize3d);

  let live = false;
  // The loop used to call renderer.render() unconditionally every frame for
  // as long as the 3D view was open, even sitting perfectly still - a real
  // cost, confirmed in a profiler capture showing continuous renderBufferDirect
  // traffic across an entire idle timeline. Now it only renders when
  // something actually moved or changed; every camera/scene mutation above
  // and below sets this back to true.
  let dirty = true;
  // Mutable so the interactive "Textured" toggle can retarget the loop
  // without re-registering keys/controls; loop() always reads the current
  // value rather than closing over the flat scene permanently.
  let activeScene = scene;
  function loop(now = performance.now()) {
    if (!live) { return; }
    cam.tick(now);
    if (dirty) {
      renderer.render(activeScene, camera);
      dirty = false;
    }
    requestAnimationFrame(loop);
  }
  three = {
    renderer, scene, camera, markerGroup, targetGroup, progressGroup, resize3d,
    markerGeo, markerMats, targetGeo, targetMat, bloomGeo, bloomMat, lineMat,
    progressCheckedMat, progressVerifiedMat, surfaceZAt,
    get isLive() { return live; },
    get isTextured() { return activeScene !== scene; },
    start() {
      if (live) { return; }
      live = true;
      ensureCrosshair();
      // resize3d() also sets dirty, but only if the stage already has a
      // size - it can still be display:none mid-transition right here, so
      // set it directly too rather than relying on that as the only path.
      dirty = true;
      cam.start();
      resize3d();
      loop();
    },
    stop() {
      live = false;
      cam.stop();
    },
    // Switches the interactive (free-look + WASD) view between the flat
    // collision mesh and the real-textured GLB. Markers/target/progress follow
    // whichever scene is now active so picking, the target dot and a live
    // solve keep working in both modes.
    async setTextured(on) {
      if (on === this.isTextured) { return; }
      const dest = on ? await ensureTexturedScene() : scene;
      const src = on ? scene : currentTexturedScene();
      for (const g of [markerGroup, targetGroup, progressGroup]) {
        src?.remove(g);
        dest.add(g);
      }
      activeScene = dest;
      dirty = true;
    },
    // Any external state change (target/lineup selection, theme flip) that
    // doesn't go through camera movement still needs its one frame drawn.
    requestRender() {
      dirty = true;
    },
    // Hands keyboard control back to the view after a control was clicked.
    focusStage() {
      renderer.domElement.focus({ preventScroll: true });
    },
    // Drops the free camera directly into a lineup's exact throw position
    // and aim - "go stand there" rather than a single static preview frame,
    // so the player can then free-look/WASD around from that exact spot.
    // The camera module shares the solver's yaw/pitch sign convention, so
    // the lineup's raw angles pass straight through with no conversion.
    flyTo({ feet, type, pitchDeg, yawDeg }) {
      const eyeHeight = EYE_HEIGHT_BY_TYPE[type] ?? DEFAULT_EYE_HEIGHT;
      camera.position.set(feet[0], feet[1], feet[2] + eyeHeight);
      cam.setLook(yawDeg * Math.PI / 180, pitchDeg * Math.PI / 180);
    },
    // Straight down over the middle of the map, pulled back far enough that the
    // whole radar footprint fits the narrower of the two view angles.
    topDown() {
      const halfFovY = camera.fov * Math.PI / 360;
      const halfFovX = Math.atan(Math.tan(halfFovY) * camera.aspect);
      const height = Math.max(
        (RY1 - RY0) / 2 / Math.tan(halfFovY),
        (RX1 - RX0) / 2 / Math.tan(halfFovX));
      camera.position.set(cx, cy, height * 1.05);
      // Straight down: the camera module clamps pitch short of 90 degrees so
      // the view matrix cannot degenerate against camera.up.
      cam.setLook(-Math.PI / 2, Math.PI / 2);
    },
  };
  return three;
}

// Per-type eye height, matching GrenadeTrajectory.EyeHeight exactly so the
// preview camera sits where the solver's own aim ray actually starts.

// Source engine FOV is "Hor+": fov_desired is the horizontal FOV at a 4:3
// reference aspect, and the vertical FOV this implies stays constant across
// every other aspect ratio while the horizontal FOV widens. three.js's
// PerspectiveCamera.fov is vertical, so deriving and holding *that* fixed
// while letting camera.aspect drive the projection matrix reproduces the
// same widening automatically - no per-aspect-ratio special-casing needed.
export function verticalFovFromDesired(fovDesiredDeg) {
  const hHalf = fovDesiredDeg * Math.PI / 360;
  return 2 * Math.atan(Math.tan(hHalf) / (4 / 3)) * 180 / Math.PI;
}

let crosshairEl = null;
export function ensureCrosshair() {
  if (crosshairEl) {
    return;
  }
  crosshairEl = document.createElement("div");
  crosshairEl.className = "preview-crosshair";
  crosshairEl.innerHTML = "<span></span><span></span>";
  stage3d.appendChild(crosshairEl);
}



// Re-applies palette-dependent colors after a prefers-color-scheme flip
// (M45). The terrain's per-vertex height tint stays baked from init; a rare
// theme flip does not justify rewriting the whole color attribute.
export function applyTheme3d() {
  if (!three) {
    return;
  }
  const colors = state.colors;
  three.scene.background.set(colors.surface);
  three.markerMats.left.color.set(colors["click-left"]);
  three.markerMats.mid.color.set(colors["click-mid"]);
  three.markerMats.right.color.set(colors["click-right"]);
  three.targetMat.color.set(colors.target);
  three.bloomMat.color.set(colors.target);
  three.lineMat.color.set(colors.accent);
  three.progressVerifiedMat.color.set(colors["heat-ok"]);
  three.requestRender();
}

// Children share geometries/materials owned by init3d; only geometries
// flagged as owned (the per-selection trajectory line) need disposal.
function clearGroup(group) {
  while (group.children.length) {
    const child = group.children[0];
    if (child.userData.ownedGeometry) { child.geometry.dispose(); }
    group.remove(child);
  }
}

// Cells are [x, y, z, verdict]; the dot floats just clear of the floor the
// origin stands on so it does not z-fight with it.
function progressCloud(cells, material, colorOf) {
  const pos = new Float32Array(cells.length * 3);
  const col = colorOf ? new Float32Array(cells.length * 3) : null;
  const tmp = new THREE.Color();
  let n = 0;
  for (const cell of cells) {
    const [x, y, z] = cell;
    pos[n] = x; pos[n + 1] = y; pos[n + 2] = z + 6;
    if (col) {
      tmp.set(colorOf(cell));
      col[n] = tmp.r; col[n + 1] = tmp.g; col[n + 2] = tmp.b;
    }
    n += 3;
  }
  const geo = new THREE.BufferGeometry();
  geo.setAttribute("position", new THREE.BufferAttribute(pos, 3));
  if (col) { geo.setAttribute("color", new THREE.BufferAttribute(col, 3)); }
  const points = new THREE.Points(geo, material);
  points.userData.ownedGeometry = true;
  return points;
}

// The 3D counterpart of the 2D sweep dots: one dot per evaluated origin,
// blue where a throw reached the target and orange where none did, with the
// verify phase growing the confirmed ones into bright markers. Rejected
// candidates keep their plain "sim reached" dot, which is what the finished
// heatmap shows them as anyway.
export function syncProgress3d() {
  if (!three || stage3d.style.display === "none") {
    return;
  }
  const { progressGroup, progressCheckedMat, progressVerifiedMat } = three;
  clearGroup(progressGroup);
  const p = state.progress;
  if (p?.checked.length) {
    const ok = state.colors["heat-ok"], none = state.colors["heat-none"];
    progressGroup.add(progressCloud(p.checked, progressCheckedMat, c => c[3] > 0 ? ok : none));
  }
  if (p?.verified.length) {
    const confirmed = p.verified.filter(c => c[3]);
    if (confirmed.length) {
      progressGroup.add(progressCloud(confirmed, progressVerifiedMat, null));
    }
  }
  three.requestRender();
}

export function sync3d() {
  if (!three || stage3d.style.display === "none") {
    return;
  }
  const { markerGroup, targetGroup, markerGeo, markerMats, targetGeo, targetMat, bloomGeo, bloomMat, lineMat } = three;
  clearGroup(markerGroup);
  clearGroup(targetGroup);
  const target = state.target;
  if (target) {
    // A 3D-picked target carries its exact Z; a 2D-picked one does not, so drop
    // it onto the collision surface here rather than pinning it to world zero
    // (which read as a target floating in space above stairs and ledges).
    const tz = target.length > 2 ? target[2] : (three.surfaceZAt(target[0], target[1]) ?? 0);
    const dot = new THREE.Mesh(targetGeo, targetMat);
    dot.position.set(target[0], target[1], tz + 6);
    targetGroup.add(dot);
    const zoneRadius = state.filters.precision.value ? parseFloat(state.filters.precision.value) : SMOKE_BLOOM_RADIUS;
    const bloom = new THREE.Mesh(bloomGeo, bloomMat);
    bloom.scale.setScalar(zoneRadius);
    bloom.position.copy(dot.position);
    targetGroup.add(bloom);
  }
  if (state.result) {
    for (const l of filtered()) {
      const m = new THREE.Mesh(markerGeo, markerMats[clickClass(l.strength)]);
      m.position.set(l.feet[0], l.feet[1], l.feet[2] + 20);
      m.userData.idx = l._idx;
      if (l._idx === state.selected) { m.scale.setScalar(1.8); }
      markerGroup.add(m);
    }
    if (state.selected >= 0) {
      const l = state.result.lineups[state.selected];
      // The simulated arc once it has been fetched; until then a straight line
      // from the throw spot to where it lands, so selecting a lineup shows
      // something immediately rather than nothing.
      const pts = l._path
        ? l._path.map(p => new THREE.Vector3(p[0], p[1], p[2]))
        : [new THREE.Vector3(l.feet[0], l.feet[1], l.feet[2] + 20),
           new THREE.Vector3(l.rest[0], l.rest[1], l.rest[2] + 4)];
      const line = new THREE.Line(new THREE.BufferGeometry().setFromPoints(pts), lineMat);
      line.userData.ownedGeometry = true;
      markerGroup.add(line);
    }
  }
  three.requestRender();
}

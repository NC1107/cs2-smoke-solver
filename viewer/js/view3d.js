// ---------- 3D view (three.js, collision mesh) ----------
// three.js stays a lazily loaded window global (THREE); this module only
// wraps init/sync. Raycast picks route through callbacks that main.js
// registers, so this module never imports the orchestrator.

import { state, filtered, clickClass, SMOKE_BLOOM_RADIUS } from "./state.js";
import { fetchMesh } from "./api.js";

const stage3d = state.stage3d;
let three = null;
let threePromise = null; // memoized in-flight init so re-toggles share one

let callbacks = {
  onSelect: () => {},
  onSetTarget: () => {},
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

function disposeSceneContents(scene) {
  scene?.traverse(obj => {
    obj.geometry?.dispose();
    for (const m of [].concat(obj.material ?? [])) {
      m.map?.dispose();
      m.dispose?.();
    }
  });
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
    disposeSceneContents(texturedScene);
    three.renderer.dispose();
    three.renderer.domElement.remove();
  }
  three = null;
  threePromise = null;
  texturedScene = null;
  texturedScenePromise = null;
}

const scriptPromises = {};
function loadScript(src) {
  scriptPromises[src] ??= new Promise((resolve, reject) => {
    const s = document.createElement("script");
    s.src = src;
    s.onload = resolve;
    s.onerror = () => { delete scriptPromises[src]; s.remove(); reject(new Error(`failed to load ${src}`)); };
    document.head.appendChild(s);
  });
  return scriptPromises[src];
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
  // leaves focus on that button, and onKeyDown deliberately ignores keys aimed
  // at a button - which is why WASD went dead after pressing Top-down until you
  // clicked the view again.
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

  // Free-look: yaw/pitch of the camera's own facing direction (not an orbit
  // pivot far away), same sign convention as renderPreview's aim direction
  // (positive pitch looks down) so flyTo() can hand this lineup pitch/yaw
  // straight through with no conversion. Derived from the initial lookAt()
  // above rather than duplicated math, so both stay in lockstep.
  const initDir = new THREE.Vector3();
  camera.getWorldDirection(initDir);
  let yaw = Math.atan2(initDir.y, initDir.x);
  let pitch = -Math.asin(THREE.MathUtils.clamp(initDir.z, -1, 1));
  const PITCH_LIMIT = 89 * Math.PI / 180;
  const lookDir = new THREE.Vector3(), lookAt = new THREE.Vector3();
  function applyLook() {
    pitch = Math.max(-PITCH_LIMIT, Math.min(PITCH_LIMIT, pitch));
    const cp = Math.cos(pitch);
    lookDir.set(cp * Math.cos(yaw), cp * Math.sin(yaw), -Math.sin(pitch));
    camera.lookAt(lookAt.copy(camera.position).add(lookDir));
    dirty = true;
  }

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

  // Free camera: left-drag looks around from the current spot (rotates the
  // camera in place, like turning your head - not orbiting a distant pivot,
  // which used to swing the whole view when navigating up close to a wall).
  // Right-drag pans (translates without rotating); scroll dollies forward/
  // back along the view direction. A held button that never moves past the
  // 4px threshold still falls through to the click-to-pick handler below.
  const LOOK_SENSITIVITY = 0.0035;
  const PAN_SENSITIVITY = 1.35;
  const DOLLY_SENSITIVITY = 1.4;
  const panRight = new THREE.Vector3(), panUp = new THREE.Vector3(0, 0, 1), dollyDir = new THREE.Vector3();
  let down = null;
  let dragButton = -1;
  let lastX = 0, lastY = 0;
  renderer.domElement.addEventListener("contextmenu", e => e.preventDefault());
  renderer.domElement.addEventListener("pointerdown", e => {
    down = [e.clientX, e.clientY];
    if (e.button === 0 || e.button === 2) {
      dragButton = e.button;
      lastX = e.clientX;
      lastY = e.clientY;
      renderer.domElement.setPointerCapture(e.pointerId);
      renderer.domElement.classList.add(dragButton === 0 ? "looking" : "panning");
    }
  });
  renderer.domElement.addEventListener("pointermove", e => {
    if (dragButton === -1) { return; }
    const dx = e.clientX - lastX, dy = e.clientY - lastY;
    lastX = e.clientX;
    lastY = e.clientY;
    if (dragButton === 0) {
      yaw -= dx * LOOK_SENSITIVITY;
      // Positive pitch looks down (dir.z = -sin(pitch)), so dragging the
      // mouse down (dy > 0) - standard non-inverted mouselook - must
      // increase pitch, not decrease it.
      pitch += dy * LOOK_SENSITIVITY;
      applyLook();
    } else {
      panRight.set(Math.sin(yaw), -Math.cos(yaw), 0);
      camera.position.addScaledVector(panRight, dx * PAN_SENSITIVITY);
      camera.position.addScaledVector(panUp, dy * PAN_SENSITIVITY);
      dirty = true;
    }
  });
  renderer.domElement.addEventListener("pointerup", e => {
    dragButton = -1;
    renderer.domElement.classList.remove("looking", "panning");
    if (!down || Math.hypot(e.clientX - down[0], e.clientY - down[1]) > 4) { down = null; return; }
    down = null;
    const rect = renderer.domElement.getBoundingClientRect();
    const ndc = new THREE.Vector2(
      ((e.clientX - rect.left) / rect.width) * 2 - 1,
      -((e.clientY - rect.top) / rect.height) * 2 + 1);
    raycaster.setFromCamera(ndc, camera);
    // markers take priority over terrain
    const mhits = raycaster.intersectObjects(markerGroup.children, false);
    if (mhits.length > 0 && mhits[0].object.userData.idx !== undefined) {
      callbacks.onSelect(mhits[0].object.userData.idx);
      return;
    }
    const hits = raycaster.intersectObject(meshObj, false);
    if (hits.length > 0) {
      const pnt = hits[0].point;
      callbacks.onSetTarget([pnt.x, pnt.y, pnt.z], `target ${pnt.x.toFixed(0)}, ${pnt.y.toFixed(0)}, ${pnt.z.toFixed(0)} (3D)`);
    }
  });
  renderer.domElement.addEventListener("wheel", e => {
    e.preventDefault();
    camera.getWorldDirection(dollyDir);
    camera.position.addScaledVector(dollyDir, -e.deltaY * DOLLY_SENSITIVITY);
    dirty = true;
  }, { passive: false });

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

  // WASD freecam: fly relative to the camera heading; Space/E for up,
  // Ctrl/Q/C for down (matching CS2's own spectator free-cam scheme), shift
  // for 4x speed. Works alongside look (left-drag) and pan (right-drag).
  // Key listeners bind in start() and unbind in stop() so 2D mode never
  // sees them; UI interactions (controls, panel, any form control) are
  // ignored so typing or toggling never flies the camera (M13).
  // Every code here must be preventDefault()'d: the browser owns several of
  // these as chords before our handler ever runs them as game input - Space
  // scrolls the page, and Ctrl+W (an entirely plausible chord once Ctrl means
  // "down," since flying down-and-forward is a completely normal combined
  // move) closes the whole browser tab.
  const FLY_KEYS = new Set([
    "KeyW", "KeyA", "KeyS", "KeyD", "KeyQ", "KeyE", "KeyC", "Space",
    "ControlLeft", "ControlRight", "ShiftLeft", "ShiftRight",
  ]);
  const keys = new Set();
  const onKeyDown = e => {
    if (e.target instanceof Element &&
        e.target.closest("#controls, .panel, input, select, button, summary, details")) {
      return;
    }
    if (FLY_KEYS.has(e.code)) {
      e.preventDefault();
    }
    keys.add(e.code);
  };
  const onKeyUp = e => keys.delete(e.code);
  const onBlur = () => keys.clear();

  // Units per second, not per frame. The old code added a fixed step straight to
  // the position every frame, which made the camera travel 2.4x further on a
  // 144Hz monitor than a 60Hz one for the same key press.
  const SPEED = 430;
  const FAST_SPEED = 1500;
  // Rate the velocity closes on the speed the keys are asking for. Instant
  // velocity is what made this feel like it teleported rather than flew.
  const RESPONSIVENESS = 11;
  const STOPPED = 1;

  // Reused across frames; fly() runs every frame while the camera still has
  // velocity, which outlasts the key press now that it eases to a stop.
  const fwd = new THREE.Vector3(), right = new THREE.Vector3();
  const wanted = new THREE.Vector3(), velocity = new THREE.Vector3();
  let lastFrame = 0;
  function fly(now) {
    const dt = Math.min((now - lastFrame) / 1000, 0.1);
    lastFrame = now;
    if (keys.size === 0 && velocity.lengthSq() < STOPPED) {
      velocity.set(0, 0, 0);
      return;
    }
    camera.getWorldDirection(fwd);
    fwd.z = 0;
    if (fwd.lengthSq() < 1e-6) { fwd.set(0, 1, 0); }
    fwd.normalize();
    right.set(fwd.y, -fwd.x, 0);
    wanted.set(0, 0, 0);
    if (keys.has("KeyW")) { wanted.add(fwd); }
    if (keys.has("KeyS")) { wanted.sub(fwd); }
    if (keys.has("KeyD")) { wanted.add(right); }
    if (keys.has("KeyA")) { wanted.sub(right); }
    if (keys.has("KeyE") || keys.has("Space")) { wanted.z += 1; }
    if (keys.has("KeyQ") || keys.has("KeyC") || keys.has("ControlLeft") || keys.has("ControlRight")) { wanted.z -= 1; }
    if (wanted.lengthSq() > 0) {
      const fast = keys.has("ShiftLeft") || keys.has("ShiftRight");
      wanted.normalize().multiplyScalar(fast ? FAST_SPEED : SPEED);
    }
    // Frame-rate independent exponential approach: the fraction of the remaining
    // gap closed depends on elapsed time, not on how many frames elapsed.
    velocity.lerp(wanted, 1 - Math.exp(-RESPONSIVENESS * dt));
    if (velocity.lengthSq() < STOPPED) {
      velocity.set(0, 0, 0);
      return;
    }
    camera.position.addScaledVector(velocity, dt);
    dirty = true;
  }

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
    fly(now);
    if (dirty) {
      renderer.render(activeScene, camera);
      dirty = false;
    }
    requestAnimationFrame(loop);
  }
  three = {
    renderer, scene, camera, markerGroup, targetGroup, progressGroup, resize3d,
    markerGeo, markerMats, targetGeo, targetMat, bloomGeo, bloomMat, lineMat,
    progressCheckedMat, progressVerifiedMat,
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
      window.addEventListener("keydown", onKeyDown);
      window.addEventListener("keyup", onKeyUp);
      window.addEventListener("blur", onBlur);
      resize3d();
      loop();
    },
    stop() {
      live = false;
      keys.clear();
      window.removeEventListener("keydown", onKeyDown);
      window.removeEventListener("keyup", onKeyUp);
      window.removeEventListener("blur", onBlur);
    },
    // Switches the interactive (free-look + WASD) view between the flat
    // collision mesh and the real-textured GLB. Markers/target/progress follow
    // whichever scene is now active so picking, the target dot and a live
    // solve keep working in both modes.
    async setTextured(on) {
      if (on === this.isTextured) { return; }
      const dest = on ? await ensureTexturedScene() : scene;
      const src = on ? scene : texturedScene;
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
    // Reuses the same yaw/pitch sign convention applyLook() already uses
    // (see its derivation above), so the solver's raw pitch/yaw degrees
    // pass straight through with no conversion.
    flyTo({ feet, type, pitchDeg, yawDeg }) {
      const eyeHeight = EYE_HEIGHT[type] ?? DEFAULT_EYE_HEIGHT;
      camera.position.set(feet[0], feet[1], feet[2] + eyeHeight);
      yaw = yawDeg * Math.PI / 180;
      pitch = pitchDeg * Math.PI / 180;
      applyLook();
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
      // Positive pitch looks down (lookDir.z = -sin(pitch)). applyLook() clamps
      // to 89 degrees, which is what keeps the view matrix from degenerating
      // when the look direction lines up with camera.up.
      yaw = -Math.PI / 2;
      pitch = Math.PI / 2;
      applyLook();
    },
  };
  return three;
}

// Per-type eye height, matching GrenadeTrajectory.EyeHeight exactly so the
// preview camera sits where the solver's own aim ray actually starts.
const EYE_HEIGHT = { Crouch: 46.04, CrouchJumpThrow: 46.04 };
const DEFAULT_EYE_HEIGHT = 64.06;

// Source engine FOV is "Hor+": fov_desired is the horizontal FOV at a 4:3
// reference aspect, and the vertical FOV this implies stays constant across
// every other aspect ratio while the horizontal FOV widens. three.js's
// PerspectiveCamera.fov is vertical, so deriving and holding *that* fixed
// while letting camera.aspect drive the projection matrix reproduces the
// same widening automatically - no per-aspect-ratio special-casing needed.
function verticalFovFromDesired(fovDesiredDeg) {
  const hHalf = fovDesiredDeg * Math.PI / 360;
  return 2 * Math.atan(Math.tan(hHalf) / (4 / 3)) * 180 / Math.PI;
}

let crosshairEl = null;
function ensureCrosshair() {
  if (crosshairEl) {
    return;
  }
  crosshairEl = document.createElement("div");
  crosshairEl.className = "preview-crosshair";
  crosshairEl.innerHTML = "<span></span><span></span>";
  stage3d.appendChild(crosshairEl);
}

// Textured scene, shared by lineup previews and the interactive view's
// "Textured" toggle. A separate GLB per map, exported straight from the
// game's VPK with real materials/UVs (data/{map}_textured.glb, built via
// exportgltf + rig/optimize-textured-glb.mjs), loaded lazily since it is
// tens of MB.
let texturedScene = null;
let texturedScenePromise = null;
// Mirrors resetEnsure3d(): a failed load (e.g. called before ensure3d() has
// loaded THREE) would otherwise cache the rejected promise forever, so every
// later retry in the same page session replays the same stale failure.
export function resetEnsureTexturedScene() {
  texturedScenePromise = null;
}
export function ensureTexturedScene(url = `data/${state.currentMap}_textured.glb`) {
  texturedScenePromise ??= (async () => {
    await loadScript("viewer/lib/GLTFLoader.js");
    await loadScript("viewer/lib/DRACOLoader.js");
    const draco = new THREE.DRACOLoader();
    draco.setDecoderPath("viewer/lib/draco/");
    const loader = new THREE.GLTFLoader();
    loader.setDRACOLoader(draco);
    const gltf = await new Promise((resolve, reject) => {
      loader.load(url, resolve, undefined, reject);
    });
    const root = gltf.scene;
    // VRF exports in meters with a cyclic axis permutation, not a plain
    // Y-up/Z-up swap: raw (x,y,z) maps to Hammer (z,y,x) - Hammer_X=raw_z,
    // Hammer_Y=raw_x, Hammer_Z=raw_y (all times 1/0.0254), confirmed by
    // comparing this GLB's bounding box against the map's known Hammer-unit
    // region (viewer-map.json). A single Euler rotation can't express this
    // axis permutation without ambiguity over axis order - e.g. rotation.x =
    // 90deg only swaps two axes, leaving the frame rotated 90 degrees off
    // the collision mesh/solver frame - so the basis is set directly as a
    // matrix instead.
    const s = 1 / 0.0254;
    root.matrixAutoUpdate = false;
    root.matrix.set(
      0, 0, s, 0,
      s, 0, 0, 0,
      0, s, 0, 0,
      0, 0, 0, 1);
    root.updateMatrixWorld(true);

    // Render unlit rather than PBR-lit. CS2's own bake already puts lighting
    // into the textures/vertex tints, and the exporter can't classify
    // roughness/metalness channels for this game build's shader format (a
    // version-support gap in the VRF library) - the combination sent PBR
    // materials to both extremes (pitch black roof undersides, blown-out
    // white walls) depending on viewing angle. A flat, unlit material is
    // both simpler and closer to what the game itself would show here.
    // Source's compiler/editor-only materials (invisible clip brushes, light
    // helpers, nodraw, etc.) are all named "tools*" by convention and were
    // never meant to be seen in-game - the exporter includes them anyway,
    // and at least one (a giant "toolssolidblocklight"/"toolsblocklight"
    // ground-covering plane, its own debug texture reading "Solid"/"Block
    // Light" across the whole map) sat on top of the real geometry and made
    // the world look misaligned/rotated depending on the viewing angle.
    // A second junk category: materials/effects/smoke/* (dust motes, window
    // lightshafts, steam wisps - csgo_effects.vfx, GLTFLoader preserves the
    // source .vmat path/shader name on material.userData via extras). These
    // are additive particle-card VFX meant to render as faint animated
    // overlays; rendered as solid unlit geometry they show up as stark white
    // wedges and orange planes floating over rooftops, useless for a static
    // lineup reference, so they are dropped entirely rather than shaded.
    // A third junk category found across every map (not just one): the
    // "tools" naming convention isn't always reflected in the glTF material's
    // display name - some editor-only materials keep a friendly name (e.g.
    // Mirage's Retake-mode "Wrong Way" site markers are named "wrongway_timer"
    // but live at materials/tools/wrongway_timer.vmat) or live under
    // materials/dev/ instead of materials/tools/ (a level-wide "black_simple"
    // placeholder present on every single map checked, plus per-map lighting
    // reflectivity checkers like materials/dev/reflectivity_30.vmat on Nuke
    // and Ancient), or under models/ui/ (Nuke's solid red "retakes_blocker" -
    // a Retake-game-mode-only wall) - matched by the vmat path prefix instead
    // of the display name so these are caught regardless of what the
    // material happens to be called.
    const toRemove = [];
    root.traverse(o => {
      if (!o.isMesh || !o.material) {
        return;
      }
      const vmat = o.material.userData?.vmat;
      const vmatName = vmat?.Name ?? "";
      if (o.material.name?.toLowerCase().startsWith("tools") ||
          vmatName.startsWith("materials/effects/") ||
          vmatName.startsWith("materials/tools/") ||
          vmatName.startsWith("materials/dev/") ||
          vmatName.startsWith("models/ui/")) {
        toRemove.push(o);
        return;
      }
      // A few meshes (flags, banners) carry no diffuse texture at all and
      // rely on baked per-vertex color instead; without vertexColors those
      // fall back to material.color, which is white by default and paints
      // them as flat white triangles.
      const hasVertexColor = !!o.geometry.getAttribute("color");
      // Water and glass are procedural shaders (reflection/refraction/
      // caustics computed at runtime) with no baseColorTexture and no
      // baseColorFactor at all - GLTFLoader leaves material.color at its
      // default white, so every water/glass surface on every map rendered as
      // a flat opaque white plane. There is no static texture to fall back
      // to (it doesn't exist even in the raw export), so approximate each
      // with a translucent tint - water uses its own g_vWaterFogColor vmat
      // param (already per-map art-directed, e.g. muddy tan for Ancient's
      // jungle water vs. clear blue elsewhere) when present.
      let color = o.material.color;
      let { transparent, opacity } = o.material;
      const shaderName = vmat?.ShaderName ?? "";
      if (!o.material.map && shaderName === "csgo_water_fancy.vfx") {
        const fog = vmat?.VectorParams?.g_vWaterFogColor;
        color = fog ? new THREE.Color(fog[0], fog[1], fog[2]) : new THREE.Color(0x2f5f6b);
        transparent = true;
        opacity = 0.75;
      } else if (!o.material.map && shaderName === "csgo_glass.vfx") {
        color = new THREE.Color(0xbfd9e0);
        transparent = true;
        opacity = 0.35;
      }
      // Alpha-cutout meshes (fences, window bars, rusty grates) lose their
      // cutout pattern and render as solid colored quads unless the MASK
      // alphaTest GLTFLoader already computed from the glTF material is
      // carried over onto the replacement material.
      o.material = new THREE.MeshBasicMaterial({
        map: o.material.map,
        color,
        vertexColors: !o.material.map && hasVertexColor,
        transparent,
        alphaTest: o.material.alphaTest,
        opacity,
        side: o.material.side,
      });
    });
    for (const o of toRemove) {
      o.parent.remove(o);
    }

    const scene = new THREE.Scene();
    scene.background = new THREE.Color(state.colors.surface);
    scene.add(root);

    texturedScene = scene;
    return scene;
  })();
  return texturedScenePromise;
}

// Renders one first-person frame from a lineup's exact throw position and
// angle - what the player would line their crosshair against, not the
// orbiting overview camera. Used for headless preview capture (screenshots
// are taken by the automation driving the browser, not by this function).
// fovDesiredDeg matches the client's fov_desired convar (90 is the CS2/CS:GO
// default); pass the player's actual setting if they have changed it.
export function renderPreview({ feet, type, pitchDeg, yawDeg, fovDesiredDeg = 90 }) {
  if (!three) {
    throw new Error("renderPreview called before ensure3d()");
  }
  // The interactive view's own requestAnimationFrame loop (started by
  // three.start(), e.g. from clicking the 3D button) keeps re-rendering the
  // flat scene every frame with whatever camera state currently exists -
  // it will silently clobber this function's render within milliseconds if
  // left running, overwriting it before a screenshot can ever see it.
  three.stop();
  const { renderer, camera, markerGroup, targetGroup } = three;
  // Prefer the real-textured scene once ensureTexturedScene() has resolved;
  // fall back to the flat collision mesh otherwise so this still works
  // stand-alone (e.g. tests, or a map with no textured export yet).
  const scene = texturedScene ?? three.scene;
  const eyeHeight = EYE_HEIGHT[type] ?? DEFAULT_EYE_HEIGHT;
  const eye = new THREE.Vector3(feet[0], feet[1], feet[2] + eyeHeight);
  const pr = pitchDeg * Math.PI / 180, yr = yawDeg * Math.PI / 180;
  // Same convention as GrenadeTrajectory/AimReference: yaw around Z, pitch
  // tilts toward +Z as it goes negative (throws aim "down" at negative pitch).
  const dir = new THREE.Vector3(
    Math.cos(pr) * Math.cos(yr), Math.cos(pr) * Math.sin(yr), -Math.sin(pr));

  camera.fov = verticalFovFromDesired(fovDesiredDeg);
  camera.position.copy(eye);
  camera.lookAt(eye.clone().add(dir));
  camera.updateProjectionMatrix();

  // A clean shot of the world only: our own marker/target overlays would
  // never appear in a real player's view.
  const wasMarkerVisible = markerGroup.visible, wasTargetVisible = targetGroup.visible;
  markerGroup.visible = false;
  targetGroup.visible = false;
  renderer.render(scene, camera);
  markerGroup.visible = wasMarkerVisible;
  targetGroup.visible = wasTargetVisible;

  // The crosshair sits at the exact viewport center regardless of resolution,
  // matching where CS2 always draws it: it represents the aim direction the
  // camera is already looking along, not a 3D-projected world point.
  ensureCrosshair();
}

// Client-side lineup preview: everything renderPreview() needs already runs
// in the user's own browser, so no server or headless automation is needed
// to show one. Temporarily borrows the shared camera/canvas, snapshots it as
// a PNG data URL, then restores whatever the user was looking at (position,
// FOV, and the interactive loop if it was running) exactly as it was.
//
// The crosshair itself is an HTML overlay (ensureCrosshair()), not part of
// the canvas's own pixels, so a page-level screenshot (the headless capture
// path) picks it up for free but canvas.toDataURL() here would not - it is
// redrawn with 2D primitives onto a copy of the frame instead.
// Fixed 16:9 capture resolution: stage3d is usually display:none at this
// point (no one has opened the 3D view), so its clientWidth/Height read 0
// and resize3d() would leave the canvas at its tiny default size. A fixed
// size also makes every preview the same shape regardless of the browser
// window, rather than however wide the page happened to be.
const PREVIEW_WIDTH = 1600, PREVIEW_HEIGHT = 900;

export async function capturePreview({ feet, type, pitchDeg, yawDeg, fovDesiredDeg = 90 }) {
  await ensure3d();
  await ensureTexturedScene();
  const wasLive = three.isLive;
  const cam = three.camera;
  const savedPos = cam.position.clone();
  const savedQuat = cam.quaternion.clone();
  const savedFov = cam.fov;
  const savedAspect = cam.aspect;

  three.renderer.setSize(PREVIEW_WIDTH, PREVIEW_HEIGHT, false);
  cam.aspect = PREVIEW_WIDTH / PREVIEW_HEIGHT;
  renderPreview({ feet, type, pitchDeg, yawDeg, fovDesiredDeg });
  const src = three.renderer.domElement;
  const out = document.createElement("canvas");
  out.width = src.width;
  out.height = src.height;
  const ctx = out.getContext("2d");
  ctx.drawImage(src, 0, 0);
  const cx = out.width / 2, cy = out.height / 2, r = out.width / 120;
  ctx.strokeStyle = "#00ff00";
  ctx.lineWidth = Math.max(1, out.width / 900);
  ctx.beginPath();
  ctx.moveTo(cx - r, cy); ctx.lineTo(cx + r, cy);
  ctx.moveTo(cx, cy - r); ctx.lineTo(cx, cy + r);
  ctx.stroke();
  const dataUrl = out.toDataURL("image/png");

  cam.position.copy(savedPos);
  cam.quaternion.copy(savedQuat);
  cam.fov = savedFov;
  cam.aspect = savedAspect;
  cam.updateProjectionMatrix();
  // Resyncs the canvas to stage3d's real size (0 if the interactive 3D view
  // was never opened, in which case this is a harmless no-op - resize3d()
  // itself guards on that and start() will size it again if 3D mode opens
  // later).
  three.resize3d();
  if (wasLive) {
    three.start();
  }
  return dataUrl;
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
    const tz = target.length > 2 ? target[2] : 0;
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

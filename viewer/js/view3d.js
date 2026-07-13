// ---------- 3D view (three.js, collision mesh) ----------
// three.js stays a lazily loaded window global (THREE); this module only
// wraps init/sync. Raycast picks route through callbacks that main.js
// registers, so this module never imports the orchestrator.

import { state, filtered, clickClass, SMOKE_BLOOM_RADIUS } from "./state.js";
import { fetchMesh } from "./api.js";

const stage3d = state.stage3d;
let three = null; // lazy-initialized bundle of scene state
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
  await loadScript("viewer/lib/OrbitControls.js");
  const buf = await fetchMesh();
  const dv = new DataView(buf);
  const vCount = dv.getInt32(0, true);
  const iCount = dv.getInt32(4, true);
  const verts = new Float32Array(buf, 8, vCount * 3);
  const idx = new Uint32Array(buf, 8 + vCount * 12, iCount);

  const colors = state.colors;
  const [RX0, RY0, RX1, RY1] = state.mapData.region;

  const renderer = new THREE.WebGLRenderer({ antialias: true });
  renderer.setPixelRatio(window.devicePixelRatio);
  stage3d.appendChild(renderer.domElement);
  const scene = new THREE.Scene();
  scene.background = new THREE.Color(colors.surface);
  const camera = new THREE.PerspectiveCamera(55, 1, 10, 40000);
  camera.up.set(0, 0, 1);
  const cx = (RX0 + RX1) / 2, cy = (RY0 + RY1) / 2;
  camera.position.set(cx, cy - 3800, 3600);
  const controls = new THREE.OrbitControls(camera, renderer.domElement);
  controls.target.set(cx, cy, 0);
  controls.maxPolarAngle = Math.PI / 2 - 0.02;
  controls.update();

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
  // Textured GLB rendering disabled by request: the collision mesh with
  // elevation shading below is the visual. (Re-enable by restoring the
  // GLTFLoader block from git history / data/de_dust2.glb is still there.)
  scene.add(new THREE.HemisphereLight(0xffffff, 0x33302a, 0.95));
  const sun = new THREE.DirectionalLight(0xffffff, 0.7);
  sun.position.set(0.4, 0.25, 1);
  scene.add(sun);

  const markerGroup = new THREE.Group();
  scene.add(markerGroup);
  const targetGroup = new THREE.Group();
  scene.add(targetGroup);

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

  const raycaster = new THREE.Raycaster();
  const meshObj = scene.children.find(o => o.isMesh);
  let down = null;
  renderer.domElement.addEventListener("pointerdown", e => { down = [e.clientX, e.clientY]; });
  renderer.domElement.addEventListener("pointerup", e => {
    if (!down || Math.hypot(e.clientX - down[0], e.clientY - down[1]) > 4) { return; }
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
  }
  window.addEventListener("resize", resize3d);

  // WASD freecam: fly relative to the camera heading; Q/E for down/up,
  // shift for 4x speed. Works alongside orbit (drag) and pan (right-drag).
  // Key listeners bind in start() and unbind in stop() so 2D mode never
  // sees them; UI interactions (controls, panel, any form control) are
  // ignored so typing or toggling never flies the camera (M13).
  const keys = new Set();
  const onKeyDown = e => {
    if (e.target instanceof Element &&
        e.target.closest("#controls, .panel, input, select, button, summary, details")) {
      return;
    }
    keys.add(e.code);
  };
  const onKeyUp = e => keys.delete(e.code);
  const onBlur = () => keys.clear();
  // Reused across frames; fly() runs every frame while keys are held.
  const fwd = new THREE.Vector3(), right = new THREE.Vector3(), move = new THREE.Vector3();
  function fly() {
    if (keys.size === 0) { return; }
    const speed = (keys.has("ShiftLeft") || keys.has("ShiftRight")) ? 48 : 12;
    camera.getWorldDirection(fwd);
    fwd.z = 0;
    if (fwd.lengthSq() < 1e-6) { fwd.set(0, 1, 0); }
    fwd.normalize();
    right.set(fwd.y, -fwd.x, 0);
    move.set(0, 0, 0);
    if (keys.has("KeyW")) { move.add(fwd); }
    if (keys.has("KeyS")) { move.sub(fwd); }
    if (keys.has("KeyD")) { move.add(right); }
    if (keys.has("KeyA")) { move.sub(right); }
    if (keys.has("KeyE") || keys.has("Space")) { move.z += 1; }
    if (keys.has("KeyQ") || keys.has("KeyC")) { move.z -= 1; }
    if (move.lengthSq() === 0) { return; }
    move.normalize().multiplyScalar(speed);
    camera.position.add(move);
    controls.target.add(move);
  }

  let live = false;
  // Mutable so the interactive "Textured" toggle can retarget the loop
  // without re-registering keys/controls; loop() always reads the current
  // value rather than closing over the flat scene permanently.
  let activeScene = scene;
  function loop() {
    if (!live) { return; }
    fly();
    controls.update();
    renderer.render(activeScene, camera);
    requestAnimationFrame(loop);
  }
  three = {
    renderer, scene, camera, controls, markerGroup, targetGroup, resize3d,
    markerGeo, markerMats, targetGeo, targetMat, bloomGeo, bloomMat, lineMat,
    get isLive() { return live; },
    get isTextured() { return activeScene !== scene; },
    start() {
      if (live) { return; }
      live = true;
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
    // Switches the interactive (orbit + WASD) view between the flat
    // collision mesh and the real-textured GLB. Markers/target follow
    // whichever scene is now active so picking and the target dot keep
    // working in both modes.
    async setTextured(on) {
      if (on === this.isTextured) { return; }
      const dest = on ? await ensureTexturedScene() : scene;
      const src = on ? scene : texturedScene;
      src?.remove(markerGroup);
      src?.remove(targetGroup);
      dest.add(markerGroup);
      dest.add(targetGroup);
      activeScene = dest;
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
// "Textured" toggle. A separate GLB exported straight from the game's VPK
// with real materials/UVs (data/de_dust2_textured.glb, built via the
// exportgltf CLI command), loaded lazily since it is ~300 MB.
let texturedScene = null;
let texturedScenePromise = null;
export function ensureTexturedScene(url = "data/de_dust2_textured.glb") {
  texturedScenePromise ??= (async () => {
    await loadScript("viewer/lib/GLTFLoader.js");
    const gltf = await new Promise((resolve, reject) => {
      new THREE.GLTFLoader().load(url, resolve, undefined, reject);
    });
    const root = gltf.scene;
    // VRF exports in meters, Y-up; the collision mesh (and every solver
    // coordinate) is Hammer units, Z-up, so rotate and rescale the whole
    // hierarchy once at the root rather than touching individual meshes.
    root.rotation.x = Math.PI / 2;
    root.scale.setScalar(1 / 0.0254);

    // Render unlit rather than PBR-lit. CS2's own bake already puts lighting
    // into the textures/vertex tints, and the exporter can't classify
    // roughness/metalness channels for this game build's shader format (a
    // version-support gap in the VRF library) - the combination sent PBR
    // materials to both extremes (pitch black roof undersides, blown-out
    // white walls) depending on viewing angle. A flat, unlit material is
    // both simpler and closer to what the game itself would show here.
    root.traverse(o => {
      if (o.isMesh && o.material) {
        // A few meshes (flags, banners) carry no diffuse texture at all and
        // rely on baked per-vertex color instead; without vertexColors those
        // fall back to material.color, which is white by default and paints
        // them as flat white triangles.
        const hasVertexColor = !!o.geometry.getAttribute("color");
        o.material = new THREE.MeshBasicMaterial({
          map: o.material.map,
          color: o.material.color,
          vertexColors: !o.material.map && hasVertexColor,
        });
      }
    });

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
      const pts = [new THREE.Vector3(l.feet[0], l.feet[1], l.feet[2] + 20),
                   new THREE.Vector3(l.rest[0], l.rest[1], l.rest[2] + 4)];
      const line = new THREE.Line(new THREE.BufferGeometry().setFromPoints(pts), lineMat);
      line.userData.ownedGeometry = true;
      markerGroup.add(line);
    }
  }
}

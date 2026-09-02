// ---------- 3D view (three.js, collision mesh) ----------
// three.js stays a lazily loaded window global (THREE); this module only
// wraps init/sync. Raycast picks route through callbacks that main.js
// registers, so this module never imports the orchestrator.

import { state, filtered, clickClass, lowMemoryDevice, SMOKE_BLOOM_RADIUS, EYE_HEIGHT_BY_TYPE, DEFAULT_EYE_HEIGHT } from "./state.js?v=86";
import { fetchMesh } from "./api.js?v=86";
import { createFlyCamera } from "./flycam.js?v=86";
import { loadScript, ensureTexturedScene, currentTexturedScene, disposeSceneContents, disposeTexturedScene } from "./textured-scene.js?v=86";

const stage3d = state.stage3d;
// Warning tint for phantom blockers (grenade-clips, physics-clips, glass) - a
// magenta that appears nowhere else in the palette, so a smoke-stopping surface
// the textured world hides cannot be mistaken for a real wall or a marker.
const PHANTOM_COLOR = 0xff2fd0;
// On-screen radius (px) held constant for spawn diamonds regardless of camera
// distance, so they stay visible from the opening overview like the 2D overlay.
// A spawn marker is a real thing standing on the floor, so it is drawn at a
// real size: 32u across, the width of a player's hull. It used to be rescaled
// every frame to hold a constant pixel size, which meant that from the opening
// overview each marker covered a large piece of the map.
const SPAWN_WORLD_RADIUS = 26;
// Meshdiff severity colors, matching the 2D overlay (map2d.js): red = render
// has a surface physics lacks (grenades fly through it), orange = physics has
// a surface render lacks (a phantom bounce).
const MESHDIFF_RENDER_ONLY_COLOR = 0xff3b30;
const MESHDIFF_PHYSICS_ONLY_COLOR = 0xff9f1c;

// A soft round sprite for the progress point clouds. GL points render as hard
// squares by default, which read as blocky next to the 2D view's round dots;
// mapping this radial-alpha circle onto the material makes them dots too.
function circleSprite() {
  const c = document.createElement("canvas");
  c.width = c.height = 64;
  const g = c.getContext("2d");
  const grad = g.createRadialGradient(32, 32, 0, 32, 32, 32);
  grad.addColorStop(0, "rgba(255,255,255,1)");
  grad.addColorStop(0.65, "rgba(255,255,255,1)");
  grad.addColorStop(1, "rgba(255,255,255,0)");
  g.fillStyle = grad;
  g.fillRect(0, 0, 64, 64);
  return new THREE.CanvasTexture(c);
}
let three = null;
let threePromise = null; // memoized in-flight init so re-toggles share one
// The meshdiff overlay is one Points cloud built once per map (cells can run
// into the tens of thousands, and it never changes for the lifetime of a
// loaded map), then just shown/hidden - unlike markers/target/progress it is
// NOT rebuilt on every sync3d() call.
let meshdiffCloud = null;

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
    // The window resize handler closes over this scene's renderer/camera;
    // leaving it registered leaks the disposed scene (GC can't reclaim it) and
    // replays setSize() on dead THREE objects on every later resize.
    window.removeEventListener("resize", three.resize3d);
    disposeSceneContents(three.scene);
    three.renderer.dispose();
    three.renderer.domElement.remove();
  }
  three = null;
  threePromise = null;
  meshdiffCloud = null;
  disposeCoverageAssets();
  disposeTexturedScene();
}


async function init3d() {
  // three.js is opt-in, so keep its ~740 KB off the 2D-only load path.
  const gen = state.mapGeneration;
  await loadScript("viewer/lib/three.min.js");
  const buf = await fetchMesh(state.currentMap);
  // The map may have switched while three.js and the mesh downloaded. Bail
  // BEFORE creating a renderer or touching module state: building here would
  // append an orphaned canvas (a leaked WebGL context) and clobber the new
  // map's `three` with the old map's geometry.
  if (state.mapGeneration !== gen) {
    return undefined;
  }
  const dv = new DataView(buf);
  // Magic + format version lead the payload (see MeshPayloadSolid). If this
  // module is a stale cached copy parsing a newer mesh, the header won't match:
  // fail with a visible "reload" hint instead of drawing a scrambled mesh.
  const MESH_MAGIC = 0x44334d53; // "SM3D"
  const MESH_FORMAT = 2;
  if (dv.getUint32(0, true) !== MESH_MAGIC || dv.getUint32(4, true) !== MESH_FORMAT) {
    showStale3dNotice();
    throw new Error("3D mesh format mismatch - stale view3d.js against a newer payload");
  }
  const vCount = dv.getInt32(8, true);
  // Four index groups over one shared vertex buffer: ordinary walls, the
  // "phantom" grenade blockers (clips + glass) the textured world does not
  // show, and doors and breakables kept apart so a world state can take them
  // out of the picture. See MeshPayloadSolid.
  const worldICount = dv.getInt32(12, true);
  const phantomICount = dv.getInt32(16, true);
  const doorICount = dv.getInt32(20, true);
  const breakableICount = dv.getInt32(24, true);
  const HEADER = 28;
  const idxBase = HEADER + vCount * 12;
  const verts = new Float32Array(buf, HEADER, vCount * 3);
  const worldIdx = new Uint32Array(buf, idxBase, worldICount);
  const phantomIdx = new Uint32Array(buf, idxBase + worldICount * 4, phantomICount);
  const doorIdx = new Uint32Array(buf, idxBase + (worldICount + phantomICount) * 4, doorICount);
  const breakableIdx = new Uint32Array(buf, idxBase + (worldICount + phantomICount + doorICount) * 4, breakableICount);

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


  const posAttr = new THREE.BufferAttribute(verts, 3);
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
  const colAttr = new THREE.BufferAttribute(cols, 3);

  // The real walls: the height-tinted collision surface. This is meshObj - what
  // the ground-drop and target picking ray hit, so a target never lands on a
  // phantom clip.
  const geo = new THREE.BufferGeometry();
  geo.setAttribute("position", posAttr);
  geo.setAttribute("color", colAttr);
  geo.setIndex(new THREE.BufferAttribute(worldIdx, 1));
  // Lit shading needs per-vertex normals, and computing them is a synchronous
  // pass over the whole mesh (de_inferno: 2.7M triangles) that freezes the tab
  // plus a normal buffer the GPU then has to hold. On a phone that is the same
  // budget the mobile texture tier exists to protect, so the height tint
  // carries the shape there instead - the same unlit trade the textured scene
  // already makes deliberately.
  if (!lowMemoryDevice) {
    geo.computeVertexNormals();
  }
  const mat = lowMemoryDevice
    ? new THREE.MeshBasicMaterial({ vertexColors: true, side: THREE.DoubleSide })
    : new THREE.MeshLambertMaterial({ vertexColors: true, side: THREE.DoubleSide });
  const collisionVisual = new THREE.Mesh(geo, mat);
  scene.add(collisionVisual);

  // The phantom blockers: grenade-clips, physics-clips, and glass the solver
  // treats as solid but the textured world hides. Drawn in a distinct warning
  // tint over the walls, so an invisible wall that stops a smoke is obvious.
  let phantomVisual = null;
  if (phantomICount > 0) {
    const pgeo = new THREE.BufferGeometry();
    pgeo.setAttribute("position", posAttr);
    pgeo.setIndex(new THREE.BufferAttribute(phantomIdx, 1));
    // Unlit, and therefore identical in both scenes. As a lit material this
    // washed out to near-white over the textured world, because the lights
    // live in the flat scene and the textured one has none - an overlay whose
    // whole job is to be recognisably magenta cannot depend on where it is
    // parented. Normals go with it: nothing shades this.
    const pmat = new THREE.MeshBasicMaterial({
      color: PHANTOM_COLOR, transparent: true, opacity: 0.55,
      side: THREE.DoubleSide, depthWrite: false });
    phantomVisual = new THREE.Mesh(pgeo, pmat);
    phantomVisual.renderOrder = 1;
    // The collision toggle can hide these or overlay them on the textured world
    // (setTextured re-parents it below), so it follows the active scene.
    phantomVisual.visible = state.collisionOn;
  }

  // Doors and breakable glass: their own meshes so a "doors open" or "glass
  // broken" world state can hide exactly that geometry, and the picture agrees
  // with the throw the solver just computed.
  const stateVisuals = {};
  for (const [key, idx, color] of [["doors", doorIdx, 0x9a6b2f], ["glass", breakableIdx, PHANTOM_COLOR]]) {
    if (idx.length === 0) {
      continue;
    }
    const g = new THREE.BufferGeometry();
    g.setAttribute("position", posAttr);
    g.setIndex(new THREE.BufferAttribute(idx, 1));
    const m = new THREE.MeshBasicMaterial({
      color, transparent: true, opacity: 0.6, side: THREE.DoubleSide, depthWrite: false });
    const mesh = new THREE.Mesh(g, m);
    mesh.renderOrder = 1;
    stateVisuals[key] = mesh;
    scene.add(mesh);
  }
  // Once, and only when there is one: this used to run per door/glass group,
  // and phantomVisual is null unless the map has phantom-clip triangles, which
  // three.js answers with a console error rather than a throw.
  if (phantomVisual) {
    scene.add(phantomVisual);
  }
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
  const spawnGroup = new THREE.Group();
  scene.add(spawnGroup);
  const meshdiffGroup = new THREE.Group();
  meshdiffGroup.visible = false;
  scene.add(meshdiffGroup);

  // Spawn markers: a flat ring laid on the floor a player spawns on, in team
  // colors (T gold, CT blue). A ring on the ground reads as a position; the
  // floating diamond this replaced sat 14u up and looked like it hovered at
  // knee height. Depth-tested, so a spawn behind a wall or under a roof is
  // hidden by that geometry instead of drawing through the whole map - seeing
  // every spawn at once was never worth not being able to tell which ones are
  // actually in front of you.
  // A dome, not a disc: half a sphere standing out of the floor reads as an
  // object occupying the spawn from any angle, where a flat disc disappeared
  // whenever the camera got near ground level. Solid, so a raycast anywhere on
  // it hits - the ring it replaced had a hole in the middle of the click
  // target, and a click there went through to the floor behind.
  // Rotated into the world's axes: three.js builds a hemisphere around +Y, so
  // unrotated the dome points sideways and half of it lies buried in the floor
  // edge-on, which reads as a hollow shell with its near face missing.
  const spawnGeo = new THREE.SphereGeometry(SPAWN_WORLD_RADIUS, 20, 12, 0, Math.PI * 2, 0, Math.PI / 2)
    .rotateX(Math.PI / 2);
  const spawnEdgeGeo = new THREE.RingGeometry(SPAWN_WORLD_RADIUS - 3, SPAWN_WORLD_RADIUS, 24);
  // Team colours, solid enough to read as objects: T gold, CT blue, the same
  // pairing the 2D overlay uses.
  // Opaque: a translucent dome sorts against the floor it is sunk into and
  // renders as a hollow shell with its near face missing.
  const spawnMatT = new THREE.MeshLambertMaterial({ color: 0xd9a441 });
  const spawnMatCt = new THREE.MeshLambertMaterial({ color: 0x4a90d9 });
  const spawnEdgeT = new THREE.MeshBasicMaterial({ color: 0xd9a441, side: THREE.DoubleSide, transparent: true, opacity: 0.95, depthWrite: false });
  const spawnEdgeCt = new THREE.MeshBasicMaterial({ color: 0x4a90d9, side: THREE.DoubleSide, transparent: true, opacity: 0.95, depthWrite: false });
  // The spawn currently held as "throw from here", so the click that held it
  // is visible on the map rather than only in a line of status text.
  const spawnHeldMat = new THREE.MeshBasicMaterial({ color: 0x37c46a, side: THREE.DoubleSide, transparent: true, opacity: 0.6, depthWrite: false });

  // Shared marker assets: sync3d re-parents these instead of allocating new
  // GPU buffers per rebuild (three.js never frees them on plain .remove()).
  // Shape carries the movement, colour carries the mouse buttons - the same
  // split the 2D map uses, so a lineup reads the same in both views. In 3D a
  // sphere alone made every throw look identical: a stand throw and a
  // run-jump were the same dot in the same colour.
  const markerGeo = new THREE.SphereGeometry(8, 12, 10);
  const markerGeoByType = {
    // Grounded: a solid ball sitting on the spot.
    Stand: markerGeo,
    // Crouched: the same ball, squashed.
    Crouch: new THREE.SphereGeometry(8, 12, 10).scale(1, 1, 0.55),
    // Airborne: pointed upward, because the throw leaves the ground.
    JumpThrow: new THREE.ConeGeometry(8, 16, 12).rotateX(Math.PI / 2),
    CrouchJumpThrow: new THREE.ConeGeometry(8, 11, 12).rotateX(Math.PI / 2),
    // Running: a wedge, which reads as movement rather than a position.
    RunJumpThrow: new THREE.ConeGeometry(9, 15, 4).rotateX(Math.PI / 2),
  };
  const markerMats = {
    left: new THREE.MeshBasicMaterial({ color: colors["click-left"] }),
    mid: new THREE.MeshBasicMaterial({ color: colors["click-mid"] }),
    right: new THREE.MeshBasicMaterial({ color: colors["click-right"] }),
  };
  // Pro matches are marked in the results list, not on the map. Rings around
  // every matching marker buried the map in overlapping circles wherever a
  // popular throwing area was in view, which is most of them.
  const targetGeo = new THREE.SphereGeometry(10, 16, 12);
  const targetMat = new THREE.MeshBasicMaterial({ color: colors.target });
  const bloomGeo = new THREE.SphereGeometry(1, 24, 16); // unit sphere, scaled to the zone radius
  const bloomMat = new THREE.MeshBasicMaterial({ color: colors.target, transparent: true, opacity: 0.10, depthWrite: false });
  const lineMat = new THREE.LineBasicMaterial({ color: colors.accent });
  // The accuracy ring around a lineup's feet ("Go to"): how far the player can
  // drift before the aim misses. Green like verified heat - it means "safe".
  const slackFillMat = new THREE.MeshBasicMaterial({
    color: colors["heat-ok"], transparent: true, opacity: 0.22, depthWrite: false, side: THREE.DoubleSide });
  const slackLineMat = new THREE.LineBasicMaterial({ color: colors["heat-ok"] });
  // A sweep evaluates tens of thousands of origins and restreams every ~100ms,
  // so the live progress dots are Points clouds (one draw call each) rather
  // than a mesh per origin, which would stall the view exactly when it is
  // meant to be showing that the solver is making progress.
  const dotTex = circleSprite();
  const progressCheckedMat = new THREE.PointsMaterial({
    size: 20, map: dotTex, vertexColors: true, transparent: true, opacity: 0.6, depthWrite: false });
  const progressVerifiedMat = new THREE.PointsMaterial({
    size: 34, map: dotTex, color: colors["heat-ok"], transparent: true, opacity: 0.95, depthWrite: false });
  // Flat patches laid on the surfaces themselves, not sprites. Sprites turned
  // to face the camera, so a mismatch on a floor and one on a wall looked
  // identical and neither sat on the geometry it was reporting. The diff scans
  // straight down each column, so every cell it records is a surface a vertical
  // ray crossed - a floor, a ceiling, a catwalk, a slope - and a horizontal
  // quad is the honest shape for it. Polygon offset keeps the patch in front of
  // the surface it covers without a z-gap that would show at grazing angles.
  // Colour comes from the instance buffer, not vertexColors: three.js reads
  // per-vertex colour and per-instance colour through the same varying, and
  // turning vertexColors on with no colour attribute on the geometry leaves it
  // at zero, which painted every patch solid black.
  const meshdiffMat = new THREE.MeshBasicMaterial({
    transparent: true, opacity: 0.72, side: THREE.DoubleSide,
    depthWrite: false, polygonOffset: true, polygonOffsetFactor: -4, polygonOffsetUnits: -4 });
  // One material per kind for the real-surface overlay: the colour is the
  // whole message, so it belongs to the mesh rather than to per-vertex data.
  const meshdiffSurfaceOpts = {
    transparent: true, opacity: 0.55, side: THREE.DoubleSide,
    depthWrite: false, polygonOffset: true, polygonOffsetFactor: -4, polygonOffsetUnits: -4 };
  const meshdiffRenderMat = new THREE.MeshBasicMaterial({ ...meshdiffSurfaceOpts, color: MESHDIFF_RENDER_ONLY_COLOR });
  const meshdiffPhysicsMat = new THREE.MeshBasicMaterial({ ...meshdiffSurfaceOpts, color: MESHDIFF_PHYSICS_ONLY_COLOR });

  const raycaster = new THREE.Raycaster();
  const meshObj = scene.children.find(o => o.isMesh);

  // Ground height at a horizontal point, by dropping a ray onto the collision
  // mesh. Lets the target dot sit on the surface when it was picked in 2D (which
  // carries no Z) instead of floating at world zero. Always the collision mesh,
  // never the textured GLB, so it reports the true surface the sim uses.
  const dropRay = new THREE.Raycaster();
  const straightDown = new THREE.Vector3(0, 0, -1);
  // `fromZ` matters when the point sits under an arch or overpass: dropping
  // from the sky would land the marker on the roof above it.
  function surfaceZAt(x, y, fromZ = 20000) {
    dropRay.set(new THREE.Vector3(x, y, fromZ), straightDown);
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
      if (state.spawnsOn && spawnGroup.children.length) {
        const shits = raycaster.intersectObjects(spawnGroup.children, false);
        if (shits.length > 0 && shits[0].object.userData.spawn) {
          return {
            spawnOrigin: shits[0].object.userData.spawn,
            spawnTeam: shits[0].object.userData.spawnTeam,
          };
        }
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
      if (hit.spawnOrigin) {
        // Same busy gate as the 2D canvas: a tap during a live solve must not
        // dispatch a second one.
        if (state.busy) {
          return;
        }
        // Without a target there is nothing to solve yet, but the tap still
        // said something - hold the spawn until the target arrives rather than
        // dropping the click, which made the ring look inert.
        if (!state.target) {
          callbacks.onPickOrigin(hit.spawnOrigin, hit.spawnTeam);
        } else if (!state.heatOn) {
          callbacks.onRunQuery({ target: state.target, origin: hit.spawnOrigin });
        }
        return;
      }
      const pnt = hit.point;
      if (state.pickingOrigin) {
        callbacks.onPickThrowSpot([pnt.x, pnt.y, pnt.z]);
      } else if (state.picking || !state.target) {
        callbacks.onSetTarget([pnt.x, pnt.y, pnt.z], `target ${pnt.x.toFixed(0)}, ${pnt.y.toFixed(0)}, ${pnt.z.toFixed(0)} (3D)`);
      } else if (!state.heatOn && !state.busy) {
        callbacks.onPickThrowSpot([pnt.x, pnt.y, pnt.z]);
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
    markerGeo, markerGeoByType, markerMats, targetGeo, targetMat, bloomGeo, bloomMat, lineMat,
    slackFillMat, slackLineMat, spawnGroup, spawnGeo, spawnMatT, spawnMatCt,
    progressCheckedMat, progressVerifiedMat, surfaceZAt, meshdiffGroup, meshdiffMat,
    meshdiffRenderMat, meshdiffPhysicsMat,
    spawnEdgeGeo, spawnEdgeT, spawnEdgeCt, spawnHeldMat,
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
      // ensureTexturedScene returns null if the map switched during the GLB
      // download; there is no textured scene to switch into, so leave the flat
      // mesh active rather than dereferencing null.
      if (!dest) { return; }
      const src = on ? scene : currentTexturedScene();
      for (const g of [markerGroup, targetGroup, progressGroup, spawnGroup, meshdiffGroup, phantomVisual,
                       stateVisuals.doors, stateVisuals.glass].filter(Boolean)) {
        src?.remove(g);
        dest.add(g);
      }
      activeScene = dest;
      dirty = true;
    },
    // Show/hide the magenta collision-box overlay (grenade-clips, glass) - it
    // rides the active scene, so this works over the flat mesh and the textured
    // world alike.
    setCollisionOverlay(on) {
      if (phantomVisual) { phantomVisual.visible = on; dirty = true; }
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
    // Hide the geometry a world state removes, so "doors open" or "glass
    // broken" is something the user can see rather than only a solver setting.
    setWorldState(broken) {
      const gone = new Set((broken || "").split(",").filter(Boolean));
      if (stateVisuals.doors) { stateVisuals.doors.visible = !gone.has("doors"); }
      if (stateVisuals.glass) { stateVisuals.glass.visible = !gone.has("glass"); }
      dirty = true;
    },
    // Dev probe: what is the surface in the middle of the screen made of.
    // Answers "why does this wall look like water" with the material and
    // texture the exporter actually put there, instead of a guess from a
    // screenshot.
    describeCenter() {
      raycaster.setFromCamera(new THREE.Vector2(0, 0), camera);
      const hits = raycaster.intersectObject(activeScene, true);
      return hits.slice(0, 4).map(h => {
        const m = h.object.material;
        const vmat = m?.userData?.vmat || {};
        return {
          dist: Math.round(h.distance),
          point: [h.point.x, h.point.y, h.point.z].map(v => Math.round(v)),
          material: m?.name || "(unnamed)",
          vmat: vmat.Name || "(none)",
          shader: vmat.ShaderName || "(none)",
          texture: m?.map ? (m.map.name || m.map.userData?.uri || "(embedded)") : "NONE",
          color: m?.color ? `#${m.color.getHexString()}` : "n/a",
          transparent: !!m?.transparent,
        };
      });
    },
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

// Shown only when this module is a stale cached copy that cannot parse the
// current mesh payload. A scrambled 3D view is otherwise silent and looks like
// a solver bug, so name the real cause and the one-key fix.
function showStale3dNotice() {
  const el = document.createElement("div");
  el.className = "stage-notice";
  el.innerHTML = "3D view is out of date.<br>Press <b>Ctrl+Shift+R</b> to reload.";
  stage3d.appendChild(el);
}

let crosshairEl = null;
export function ensureCrosshair() {
  if (crosshairEl) {
    return;
  }
  crosshairEl = document.createElement("div");
  crosshairEl.className = "preview-crosshair";
  // Four gapped arms = CS2's default reticle (see .preview-crosshair CSS).
  crosshairEl.innerHTML = '<i class="ch-t"></i><i class="ch-b"></i><i class="ch-l"></i><i class="ch-r"></i>';
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
  three.slackFillMat.color.set(colors["heat-ok"]);
  three.slackLineMat.color.set(colors["heat-ok"]);
  three.progressVerifiedMat.color.set(colors["heat-ok"]);
  three.requestRender();
}

// Children share geometries/materials owned by init3d; only geometries
// flagged as owned (the per-selection trajectory line) need disposal.
function clearGroup(group) {
  while (group.children.length) {
    const child = group.children[0];
    // Traversed, not just checked: the meshdiff overlay is a Group of meshes,
    // and disposing only the top object would leak every surface under it.
    child.traverse(o => {
      if (o.userData.ownedGeometry) { o.geometry.dispose(); }
      // An InstancedMesh owns a GPU instance buffer even when its geometry and
      // material are shared, so it has to be disposed or every rebuild leaks one.
      if (o.userData.ownedInstance) { o.dispose(); }
    });
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

// The physics-vs-render mismatch overlay: built once per map on first toggle
// (or once the data arrives, if the toggle was already on) and then only
// shown/hidden - unlike the marker/target/progress groups, cells never change
// for the life of a loaded map, so there is nothing to gain from rebuilding
// this on every sync3d() call the way those groups are.
export function syncMeshDiff3d() {
  if (!three || stage3d.style.display === "none") {
    return;
  }
  const { meshdiffGroup } = three;
  if (!state.meshdiffOn || !state.meshdiff?.cells.length) {
    meshdiffGroup.visible = false;
    three.requestRender();
    return;
  }
  if (!meshdiffCloud) {
    meshdiffCloud = meshdiffSurfaces(state.meshdiff, {
      renderOnly: three.meshdiffRenderMat,
      physicsOnly: three.meshdiffPhysicsMat,
      patches: three.meshdiffMat,
    });
    meshdiffGroup.add(meshdiffCloud);
  }
  meshdiffGroup.visible = true;
  three.requestRender();
}

// The mismatched surfaces themselves, in the shape they actually have. The
// diff exports every triangle of whichever mesh owns the disagreement - the
// render mesh for surfaces a grenade flies through, the physics mesh for
// phantom bounces - so the overlay traces the missing wall or the invisible
// ledge instead of stamping a square where a column happened to be sampled.
//
// Older diff files carry only cells; those still get the flat-quad overlay so
// a map that has not been re-run keeps working.
function meshdiffSurfaces(diff, materials) {
  const group = new THREE.Group();
  for (const [key, material] of [["renderTris", materials.renderOnly], ["physicsTris", materials.physicsOnly]]) {
    const flat = diff[key];
    if (!flat?.length) {
      continue;
    }
    const geo = new THREE.BufferGeometry();
    geo.setAttribute("position", new THREE.BufferAttribute(Float32Array.from(flat), 3));
    const mesh = new THREE.Mesh(geo, material);
    mesh.userData.ownedGeometry = true;
    group.add(mesh);
  }
  if (group.children.length) {
    return group;
  }
  return meshdiffPatches(diff.cells, diff.step || 16, materials.patches);
}

// The fallback for diff files with no surfaces: one flat quad per mismatched
// cell, the size of the cell the diff scanned. Instanced because a map's diff
// runs to thousands of cells and they never move once built.
function meshdiffPatches(cells, step, material) {
  // Unit plane scaled per instance: three.js builds PlaneGeometry in the XY
  // plane, which is already horizontal in this Z-up world.
  const geo = new THREE.PlaneGeometry(step, step);
  const mesh = new THREE.InstancedMesh(geo, material, cells.length);
  const m = new THREE.Matrix4();
  const color = new THREE.Color();
  cells.forEach((cell, i) => {
    const [x, y, z, kind] = cell;
    m.makeTranslation(x, y, z);
    mesh.setMatrixAt(i, m);
    color.set(kind === 0 ? MESHDIFF_RENDER_ONLY_COLOR : MESHDIFF_PHYSICS_ONLY_COLOR);
    mesh.setColorAt(i, color);
  });
  mesh.instanceMatrix.needsUpdate = true;
  if (mesh.instanceColor) { mesh.instanceColor.needsUpdate = true; }
  mesh.userData.ownedGeometry = true;
  return mesh;
}

// A ground ring per spawn, tagged with the [x, y] origin so a tap solves a
// smoke from that exact spawn (pickAt/onTap). The entity origin sits at the
// player's feet, so the ring only lifts far enough to clear the floor plane it
// would otherwise z-fight with.
function addSpawns3d(pts, material, edgeMaterial, team) {
  const held = state.pendingOrigin;
  for (const [x, y, z] of pts) {
    const isHeld = held && Math.hypot(held[0] - x, held[1] - y) < 1;
    // Sunk to its equator: the dome is the part above the floor, and burying
    // the rest keeps it sitting in the ground rather than balanced on it.
    const m = new THREE.Mesh(three.spawnGeo, isHeld ? three.spawnHeldMat : material);
    m.position.set(x, y, z + 1);
    m.userData.spawn = [x, y];
    m.userData.spawnTeam = team;
    three.spawnGroup.add(m);
    // A ring on the floor around the dome, so the spawn still reads from
    // directly overhead where the dome is at its smallest.
    const edge = new THREE.Mesh(three.spawnEdgeGeo, isHeld ? three.spawnHeldMat : edgeMaterial);
    edge.position.set(x, y, z + 1.5);
    edge.userData.spawn = m.userData.spawn;
    edge.userData.spawnTeam = team;
    three.spawnGroup.add(edge);
  }
}

// The flooded smoke volume as one instanced box per filled cell. Instanced
// because a full bloom is a couple of thousand cells and that many separate
// meshes costs a draw call each.
//
// The geometry and material are built once and shared: sync3d rebuilds the
// target group on every selection, filter and hover change, so allocating a
// BoxGeometry and a material per call would leak a GPU buffer each time. Only
// the InstancedMesh is per-call, and it carries a flag telling clearGroup to
// dispose its instance buffer without touching the shared pair.
let coverageGeo = null;
let coverageMat = null;

function coverageAssets(voxel) {
  if (coverageGeo?.userData?.voxel !== voxel) {
    coverageGeo?.dispose();
    coverageGeo = new THREE.BoxGeometry(voxel, voxel, voxel);
    coverageGeo.userData.voxel = voxel;
  }
  coverageMat ??= new THREE.MeshBasicMaterial({
    color: new THREE.Color(state.colors.target ?? "#7fd4ff"),
    transparent: true,
    // Low per-cell alpha: the cells overlap along the view ray, so anything
    // higher reads as a solid block rather than smoke.
    opacity: 0.1,
    depthWrite: false,
  });
  return { geo: coverageGeo, mat: coverageMat };
}

export function disposeCoverageAssets() {
  coverageGeo?.dispose();
  coverageMat?.dispose();
  coverageGeo = null;
  coverageMat = null;
}

function addCoverage3d(group, cov) {
  const { cells, voxel } = cov;
  const count = cells.length / 3;
  if (!count) {
    return;
  }
  const { geo, mat } = coverageAssets(voxel);
  const mesh = new THREE.InstancedMesh(geo, mat, count);
  const m = new THREE.Matrix4();
  for (let i = 0; i < count; i++) {
    m.makeTranslation(cells[i * 3], cells[i * 3 + 1], cells[i * 3 + 2]);
    mesh.setMatrixAt(i, m);
  }
  mesh.instanceMatrix.needsUpdate = true;
  mesh.renderOrder = 2;
  // Shared geometry and material, per-call instance buffer.
  mesh.userData.ownedInstance = true;
  group.add(mesh);
}

export function sync3d() {
  if (!three || stage3d.style.display === "none") {
    return;
  }
  const { markerGroup, targetGroup, markerGeo, markerGeoByType, markerMats, targetGeo, targetMat, bloomGeo, bloomMat, lineMat } = three;
  clearGroup(markerGroup);
  clearGroup(targetGroup);
  clearGroup(three.spawnGroup);
  if (state.spawnsOn && state.spawns) {
    addSpawns3d(state.spawns.t, three.spawnMatT, three.spawnEdgeT, "T");
    addSpawns3d(state.spawns.ct, three.spawnMatCt, three.spawnEdgeCt, "CT");
  }
  const target = state.target;
  if (target) {
    // A 3D-picked target carries its exact Z; a 2D-picked one does not, so drop
    // it onto the collision surface here rather than pinning it to world zero
    // (which read as a target floating in space above stairs and ledges).
    const tz = target.length > 2 ? target[2] : (three.surfaceZAt(target[0], target[1]) ?? 0);
    const dot = new THREE.Mesh(targetGeo, targetMat);
    dot.position.set(target[0], target[1], tz + 6);
    targetGroup.add(dot);
    // The idealised sphere is only honest in the open. Once the real flooded
    // volume is available it replaces the sphere rather than sitting inside it:
    // two overlapping blobs claiming different coverage is worse than either.
    const cov = state.coverageOn ? state.coverage : null;
    if (cov?.cells?.length) {
      addCoverage3d(targetGroup, cov);
    } else {
      const zoneRadius = state.filters.precision.value ? Number.parseFloat(state.filters.precision.value) : SMOKE_BLOOM_RADIUS;
      const bloom = new THREE.Mesh(bloomGeo, bloomMat);
      bloom.scale.setScalar(zoneRadius);
      bloom.position.copy(dot.position);
      targetGroup.add(bloom);
    }
  }
  if (state.result) {
    for (const l of filtered()) {
      const m = new THREE.Mesh(markerGeoByType[l.type] ?? markerGeo, markerMats[clickClass(l.strength)]);
      // Sit half-out of the floor it stands on (center on the surface) rather
      // than floating above it, so tightly-packed throw spots stay legible.
      m.position.set(l.feet[0], l.feet[1], l.feet[2] + 1);
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
      // The accuracy ring around the throw spot ("Go to" fetches it): a fan
      // draped on the ground, each vertex dropped onto the surface from just
      // above the feet so slopes and steps don't leave it floating.
      if (l._slack) {
        const ring = l._slack.dirs.map(([deg, r]) => {
          const a = deg * Math.PI / 180;
          const x = l.feet[0] + Math.cos(a) * r, y = l.feet[1] + Math.sin(a) * r;
          return new THREE.Vector3(x, y, (three.surfaceZAt(x, y, l.feet[2] + 50) ?? l.feet[2]) + 2);
        });
        const fan = new THREE.BufferGeometry().setFromPoints(
          [new THREE.Vector3(l.feet[0], l.feet[1], l.feet[2] + 2), ...ring]);
        fan.setIndex(ring.flatMap((_, i) => [0, 1 + i, 1 + (i + 1) % ring.length]));
        const disc = new THREE.Mesh(fan, three.slackFillMat);
        disc.userData.ownedGeometry = true;
        markerGroup.add(disc);
        const outline = new THREE.LineLoop(new THREE.BufferGeometry().setFromPoints(ring), three.slackLineMat);
        outline.userData.ownedGeometry = true;
        markerGroup.add(outline);
      }
    }
  }
  three.requestRender();
}

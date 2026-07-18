// The real-textured map scene: a separate GLB per map exported straight from
// the game's VPK, shared by the interactive "Textured" toggle and the lineup
// previews. Loading, axis conversion, and material sanitization live here;
// the interactive view and the preview path only consume the finished scene.

import { state } from "./state.js?v=12";
import { cacheBust } from "./api.js?v=12";

const scriptPromises = {};
export function loadScript(src) {
  scriptPromises[src] ??= new Promise((resolve, reject) => {
    const s = document.createElement("script");
    s.src = src;
    s.onload = resolve;
    s.onerror = () => { delete scriptPromises[src]; s.remove(); reject(new Error(`failed to load ${src}`)); };
    document.head.appendChild(s);
  });
  return scriptPromises[src];
}

export function disposeSceneContents(scene) {
  scene?.traverse(obj => {
    obj.geometry?.dispose();
    for (const m of [obj.material ?? []].flat()) {
      m.map?.dispose();
      m.dispose?.();
    }
  });
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
    // The GLB is 18-46MB; the user can easily switch maps mid-download. Capture
    // the generation and drop this scene if it lands late, so it can't clobber a
    // newer map's textures. teardown3d() -> disposeTexturedScene() resets the
    // memoized promise on switch, so the next map starts a fresh load.
    const gen = state.mapGeneration;
    await loadScript("viewer/lib/GLTFLoader.js");
    await loadScript("viewer/lib/DRACOLoader.js");
    const draco = new THREE.DRACOLoader();
    draco.setDecoderPath("viewer/lib/draco/");
    const loader = new THREE.GLTFLoader();
    loader.setDRACOLoader(draco);
    const gltf = await new Promise((resolve, reject) => {
      loader.load(cacheBust(url), resolve, progress => {
        // The first preview or Textured click starts an 18-46MB one-time
        // download; without numbers it reads as a hang on a slow connection.
        if (progress.lengthComputable) {
          state.statusEl.textContent =
            `loading map textures: ${(progress.loaded / 1e6).toFixed(0)} / ${(progress.total / 1e6).toFixed(0)} MB (one-time per map)`;
        }
      }, reject);
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
        // Map walls are compiled one-sided (backfaces culled for in-engine
        // perf), so from the far side they vanish - a wall you can see through
        // from outside a room but not from inside. This is a fly-around
        // inspector, not the engine, so render opaque surfaces from both sides.
        // Translucent water/glass keep their original side: double-siding a
        // depthWrite:false surface double-blends and sorts wrong.
        side: transparent ? o.material.side : THREE.DoubleSide,
      });
    });
    for (const o of toRemove) {
      o.parent.remove(o);
    }

    const scene = new THREE.Scene();
    scene.background = new THREE.Color(state.colors.surface);
    scene.add(root);

    if (state.mapGeneration !== gen) {
      disposeSceneContents(scene);
      return null;
    }
    texturedScene = scene;
    return scene;
  })();
  return texturedScenePromise;
}

// The resolved scene, or null before the first successful load.
export function currentTexturedScene() {
  return texturedScene;
}

// Full teardown for a map switch: the scene is map-specific GPU state.
export function disposeTexturedScene() {
  disposeSceneContents(texturedScene);
  texturedScene = null;
  texturedScenePromise = null;
}

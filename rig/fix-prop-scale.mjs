// Corrects a specific data bug found in VRF's raw export: some individual
// instances of a repeated static prop carry a wildly wrong node.scale - a
// soda cup or ceiling fan sitting at 30-50x its sibling instances' size,
// while nothing else about the node (parent, mesh, material) differs.
// Confirmed present on mirage, inferno, nuke, and overpass by direct GLB
// inspection. Root cause is in the raw VRF export itself (quantize()/
// draco() never touch node.scale), so this operates directly on the
// already-optimized GLB rather than requiring a fresh VPK export.
//
// Two detection passes:
//
// 1. Sibling-outlier (safe, general): group nodes by mesh index, take the
//    per-axis median across the group as "the real scale" (median is
//    robust to a minority of outliers), and replace any instance that
//    deviates from the median by more than 5x. Legitimate size variety
//    (e.g. a rock formation reusing one mesh at deliberately different
//    scales) shows up as a smooth spread, not a lone value sitting 30-50x
//    from a tight cluster.
//
// 2. Singleton unit-conversion (narrow, manual allowlist only): a prop
//    placed only once (no sibling to compare against, e.g. inferno's
//    ceiling fan blades or dish soap bottle) can't be caught by pass 1.
//    A magnitude-only heuristic (scale implausibly large, but plausible
//    after dividing by 1/0.0254) flags 4500+ nodes on inferno alone -
//    essentially every large architectural mesh on the map (walls, roofs,
//    vehicles, doors, trees), since ordinary world geometry legitimately
//    spans this same magnitude range for unrelated reasons. There is no
//    cheap signal that separates "bugged small prop" from "big object
//    that's supposed to be big" without already knowing which is which,
//    so this pass only ever touches an explicit, human-confirmed allowlist.
//
// Usage: node rig/fix-prop-scale.mjs <input.glb> <output.glb>
import { NodeIO } from "@gltf-transform/core";
import { ALL_EXTENSIONS } from "@gltf-transform/extensions";
import draco3d from "draco3dgltf";

const inPath = process.argv[2];
const outPath = process.argv[3];
const DEVIATION_THRESHOLD = 5;
const UNIT_CONVERSION = 1 / 0.0254;
const PLAUSIBLE_MAX = 3.5;

// Exact node names confirmed as visibly wrong (Nick's direct reports, plus
// cross-map corroboration - see below), each with fewer than 3 total
// instances of its mesh so pass 1's median can't reliably identify them by
// statistics alone. Matched by node name, not material: a shared kit
// material (e.g. ceiling_fan_01.vmat) can cover multiple distinct meshes -
// the fan's blades and its base/housing, say - and only one of them was
// actually broken; matching by material alone corrected the already-fine
// housing right along with the blades.
const CONFIRMED_SINGLETONS = new Set([
  "dish_soap.dish_soap_bg_body_lod0", // inferno's giant soap bottle (Nick's report)
  "ceiling_fan_blades_01.ceiling_fan_blades_01", // inferno's giant ceiling fan (Nick's report)
  // overpass's "giant turbine" (Nick's report): only 2 instances exist
  // (25.408, 0.529) with no third sibling to anchor a median against, but
  // inferno's independently-confirmed ceiling fan blade landed at 0.643 and
  // mirage's at 1.016 after fixing - corroborating that ~0.5 is the
  // plausible value here too, and 25.408 is the outlier.
  "hvac_fanblade_spinning_01.hvac_fanblade_spinning_01_bg_body_lod0",
]);
// Never touch editor/debug-only geometry - it's excluded from rendering
// entirely (see view3d.js's material filter), so its scale is irrelevant,
// and toolsblocklight meshes are exactly what an earlier, unscoped version
// of this pass mistakenly "fixed" before this exclusion was added.
const JUNK_PREFIXES = ["materials/tools/", "materials/dev/", "models/ui/"];

const [decoder, encoder] = await Promise.all([
  draco3d.createDecoderModule(),
  draco3d.createEncoderModule(),
]);
const io = new NodeIO()
  .registerExtensions(ALL_EXTENSIONS)
  .registerDependencies({ "draco3d.decoder": decoder, "draco3d.encoder": encoder });
const doc = await io.read(inPath);

function median(values) {
  const sorted = [...values].sort((a, b) => a - b);
  const mid = Math.floor(sorted.length / 2);
  return sorted.length % 2 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2;
}

function vmatName(node) {
  const material = node.getMesh()?.listPrimitives()[0]?.getMaterial();
  return material?.getExtras()?.vmat?.Name ?? "";
}

const byMesh = new Map();
for (const node of doc.getRoot().listNodes()) {
  const mesh = node.getMesh();
  if (!mesh) {
    continue;
  }
  if (!byMesh.has(mesh)) {
    byMesh.set(mesh, []);
  }
  byMesh.get(mesh).push(node);
}

let fixedCount = 0;
for (const [, nodes] of byMesh) {
  const vn = vmatName(nodes[0]);
  if (JUNK_PREFIXES.some(p => vn.startsWith(p))) {
    continue;
  }
  if (nodes.length >= 3) {
    // A real majority cluster to anchor against - median is robust here.
    const scales = nodes.map(n => n.getScale());
    const med = [0, 1, 2].map(axis => median(scales.map(s => s[axis])));
    const medMag = Math.hypot(...med);
    if (medMag === 0) {
      continue;
    }
    nodes.forEach((node, i) => {
      const s = scales[i];
      const ratio = Math.hypot(...s) / medMag;
      if (ratio > DEVIATION_THRESHOLD || ratio < 1 / DEVIATION_THRESHOLD) {
        console.log(`  [sibling-outlier] ${node.getName()} (${vn}): ${s.map(v => v.toFixed(3))} -> ${med.map(v => v.toFixed(3))}`);
        node.setScale(med);
        fixedCount++;
      }
    });
  } else {
    // 1 or 2 instances: no reliable majority to compute a median against
    // (with exactly 2, "median" degenerates into a plain average of a
    // maybe-right and maybe-wrong value, splitting the difference into a
    // number that matches neither - confirmed on nuke's vent_bombsite prop,
    // where doing that landed both instances on a value that matched
    // nothing). Only touch an explicit, human-confirmed allowlist instead.
    for (const node of nodes) {
      if (!CONFIRMED_SINGLETONS.has(node.getName())) {
        continue;
      }
      const s = node.getScale();
      // A confirmed name can still have a perfectly fine sibling sharing
      // it (overpass's fan blade has one correct instance and one bugged
      // one, both named identically) - only touch the instance that is
      // itself actually implausibly large, not every node with this name.
      if (Math.hypot(...s) <= PLAUSIBLE_MAX) {
        continue;
      }
      const newScale = s.map(v => v / UNIT_CONVERSION);
      console.log(`  [confirmed] ${node.getName()} (${vn}): ${s.map(v => v.toFixed(3))} -> ${newScale.map(v => v.toFixed(3))}`);
      node.setScale(newScale);
      fixedCount++;
    }
  }
}

console.log(`${inPath}: corrected ${fixedCount} outlier node(s) out of ${doc.getRoot().listNodes().length} total nodes`);
await io.write(outPath, doc);

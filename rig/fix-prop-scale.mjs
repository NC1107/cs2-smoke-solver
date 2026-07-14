// Corrects a specific data bug found in VRF's raw export: some individual
// instances of a static prop carry a wildly wrong node.scale - a soda cup,
// ceiling fan, TV or curtain sitting at ~39x its intended size, while nothing
// else about the node (parent, mesh, material) differs. The factor is always
// 1/0.0254: a metres-to-inches conversion applied one time too many. Confirmed
// on every map except dust2. Root cause is in the raw VRF export itself
// (quantize()/draco() never touch node.scale), so this operates on the export
// rather than requiring a fresh VPK dump.
//
// MUST run before optimize-textured-glb.mjs, which flattens node transforms
// into the vertex data - after that the per-instance scale this reads no
// longer exists and the bug is baked in permanently.
//
// Two detection passes:
//
// 1. Sibling-outlier: group nodes by mesh, take the per-axis median across the
//    group as "the real scale" (robust to a minority of outliers), and replace
//    any instance deviating from it by more than 5x. Needs 3+ instances - with
//    2, "median" degenerates into the average of a maybe-right and a
//    maybe-wrong value, landing both on a number that matches neither
//    (confirmed on nuke's vent_bombsite prop).
//
// 2. Implausible size: for a prop with fewer than 3 instances there is no
//    majority to vote against, so it is judged on the only ground truth left -
//    how big it actually renders. node.scale alone says nothing (a mesh
//    authored large legitimately carries a small scale, and vice versa), which
//    is why a magnitude-only heuristic flagged 4500+ nodes on inferno; but
//    mesh extent x scale is a real size in metres, and a prop is not 10 metres
//    across. Measured over all 7 maps, the largest genuine prop in this class
//    is ~6.6m (Ancient's mid-size water cascade, Anubis's hanging cloth) while
//    the smallest bugged one is ~10m (a 10-metre spray paint can) - every bug
//    lands back in the 0.2-6.5m range once divided by the conversion factor.
//    Props with 3+ instances are never judged this way, which is what keeps
//    Overpass's genuinely 22m-long subway train (3 instances, all agreeing)
//    out of it.
//
// Usage: node rig/fix-prop-scale.mjs <input.glb> <output.glb>
import { NodeIO } from "@gltf-transform/core";
import { ALL_EXTENSIONS } from "@gltf-transform/extensions";
import draco3d from "draco3dgltf";

const inPath = process.argv[2];
const outPath = process.argv[3];
const DEVIATION_THRESHOLD = 5;
const UNIT_CONVERSION = 1 / 0.0254;
const MAX_PROP_METRES = 8;

// Ancient's tallest waterfall is a single instance that really is ~11.8m - it
// belongs to a deliberate 3.5m / 6.6m / 11.8m size family of the same effect,
// and at 1/39th the size it would be a 30cm trickle. The only prop found
// across all 7 maps that is both genuinely larger than MAX_PROP_METRES and has
// no siblings to prove it.
const GENUINELY_LARGE = new Set([
  "aztec_water_cascade_03c.aztec_water_cascade_03c_bg_body_lod0",
]);

// Never touch geometry the viewer doesn't render (see view3d.js's material
// filter): editor/debug helpers and particle-card VFX. An earlier, unscoped
// version of this pass "fixed" toolsblocklight meshes, which is what the
// exclusion exists to prevent.
const JUNK_PREFIXES = ["materials/tools/", "materials/dev/", "materials/effects/", "models/ui/"];

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
  return node.getMesh()?.listPrimitives()[0]?.getMaterial()?.getExtras()?.vmat?.Name ?? "";
}

// The GLB is authored in metres (the viewer scales it by 1/0.0254 to reach
// Hammer units), so a mesh's extent times its node scale is a size in metres.
function meshExtent(mesh) {
  const lo = [Infinity, Infinity, Infinity];
  const hi = [-Infinity, -Infinity, -Infinity];
  for (const primitive of mesh.listPrimitives()) {
    const position = primitive.getAttribute("POSITION");
    if (!position) {
      continue;
    }
    const element = [0, 0, 0];
    for (let i = 0; i < position.getCount(); i++) {
      position.getElement(i, element);
      for (let axis = 0; axis < 3; axis++) {
        lo[axis] = Math.min(lo[axis], element[axis]);
        hi[axis] = Math.max(hi[axis], element[axis]);
      }
    }
  }
  return [0, 1, 2].map(axis => Math.max(0, hi[axis] - lo[axis]));
}

function renderedMetres(node, extent) {
  const scale = node.getScale();
  return Math.max(...extent.map((e, axis) => e * Math.abs(scale[axis])));
}

// World geometry, not props: the map's baked terrain chunks and Hammer-authored
// brush meshes. Both legitimately span the whole map and carry scales in the
// hundreds, and neither is a prop instance VRF could have mis-scaled.
function isWorldGeometry(node) {
  const name = node.getName();
  return /^n\d+_lr\d+/.test(name) || name.includes("hammer_mesh");
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
for (const [mesh, nodes] of byMesh) {
  const vn = vmatName(nodes[0]);
  if (JUNK_PREFIXES.some(p => vn.startsWith(p))) {
    continue;
  }
  if (nodes.length >= 3) {
    const scales = nodes.map(n => n.getScale());
    const med = [0, 1, 2].map(axis => median(scales.map(s => s[axis])));
    const medMag = Math.hypot(...med);
    if (medMag === 0) {
      continue;
    }
    nodes.forEach((node, i) => {
      const ratio = Math.hypot(...scales[i]) / medMag;
      if (ratio > DEVIATION_THRESHOLD || ratio < 1 / DEVIATION_THRESHOLD) {
        console.log(`  [sibling-outlier] ${node.getName()} (${vn}): ${scales[i].map(v => v.toFixed(3))} -> ${med.map(v => v.toFixed(3))}`);
        node.setScale(med);
        fixedCount++;
      }
    });
    continue;
  }
  const extent = meshExtent(mesh);
  for (const node of nodes) {
    if (isWorldGeometry(node) || GENUINELY_LARGE.has(node.getName())) {
      continue;
    }
    const metres = renderedMetres(node, extent);
    if (metres <= MAX_PROP_METRES) {
      continue;
    }
    const scale = node.getScale();
    const corrected = scale.map(v => v / UNIT_CONVERSION);
    const correctedMetres = metres / UNIT_CONVERSION;
    // If one conversion doesn't bring it back into prop range, this isn't the
    // bug this script knows how to fix - say so rather than guessing.
    if (correctedMetres > MAX_PROP_METRES) {
      console.log(`  [skipped] ${node.getName()} (${vn}): ${metres.toFixed(1)}m is still ${correctedMetres.toFixed(1)}m after conversion - not the unit-conversion bug`);
      continue;
    }
    console.log(`  [oversized] ${node.getName()} (${vn}): ${metres.toFixed(1)}m -> ${correctedMetres.toFixed(2)}m`);
    node.setScale(corrected);
    fixedCount++;
  }
}

console.log(`${inPath}: corrected ${fixedCount} node(s) out of ${doc.getRoot().listNodes().length} total nodes`);
await io.write(outPath, doc);

// Shared GLB plumbing for the rig's gltf-transform scripts. Every script that
// touches a .glb needs the same Draco-capable NodeIO; this is the one copy of
// that setup, so a Draco version bump or extension change lands in one file
// instead of being hand-applied per script.
import { NodeIO } from "@gltf-transform/core";
import { ALL_EXTENSIONS } from "@gltf-transform/extensions";
import draco3d from "draco3dgltf";

let ioPromise = null;
export function getIO() {
  ioPromise ??= Promise.all([
    draco3d.createDecoderModule(),
    draco3d.createEncoderModule(),
  ]).then(([decoder, encoder]) => new NodeIO()
    .registerExtensions(ALL_EXTENSIONS)
    .registerDependencies({ "draco3d.decoder": decoder, "draco3d.encoder": encoder }));
  return ioPromise;
}

export async function readGlb(path) {
  return (await getIO()).read(path);
}

export async function writeGlb(path, doc) {
  return (await getIO()).write(path, doc);
}

// Fail fast with the script's own usage line instead of an opaque
// gltf-transform stack trace when an argv is missing.
export function requireArgs(usage, ...values) {
  if (values.some(v => v === undefined || v === "")) {
    console.error(`usage: ${usage}`);
    process.exit(1);
  }
}

export function median(values) {
  const sorted = [...values].sort((a, b) => a - b);
  const mid = Math.floor(sorted.length / 2);
  return sorted.length % 2 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2;
}

// The original .vmat path VRF preserves on each material - the most reliable
// way to classify a mesh (tools/debug, effects, junk) in an exported GLB.
export function vmatName(node) {
  return node.getMesh()?.listPrimitives()[0]?.getMaterial()?.getExtras()?.vmat?.Name ?? "";
}

// Axis-aligned extent of a mesh's own vertex data (no node transform).
export function meshExtent(mesh) {
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

export function groupNodesByMesh(doc) {
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
  return byMesh;
}

// Post-processes the raw `exportgltf` output into a small, self-contained
// .glb suitable for serving over a residential connection.
//
// The raw export (data/de_dust2_textured.glb + ~560 loose PNGs alongside it)
// is over 1GB combined: the .glb itself is almost entirely uncompressed mesh
// data, and every material's normal/roughness/metalness/occlusion maps are
// fetched by GLTFLoader as separate HTTP requests even though the viewer
// only ever renders unlit (map + color) - see ensureTexturedScene() in
// viewer/js/view3d.js. None of that is needed for a background reference
// view, so this strips it: drop the unused PBR texture slots and the vertex
// attributes an unlit material never reads, downscale and WebP-compress the
// remaining color textures, quantize vertex precision, merge everything that
// shares a material into one draw call, Draco-compress the geometry, and embed
// it all into one .glb (killing the loose-PNG HTTP requests too). de_dust2 went
// from ~1.1GB to 35MB.
//
// This only ever touches the *visual* mesh. The simulation collides against
// data/{map}.s2geo (Valve's own authored physics hulls, extracted separately by
// MapExtractor.ExtractWorldPhysics) and never reads this .glb, so nothing here
// can move a smoke by a single unit.
//
// Usage: node rig/optimize-textured-glb.mjs [input.glb] [output.glb]
import { prune, textureCompress, quantize, dedup, draco, flatten, join, simplify, weld } from "@gltf-transform/functions";
import { MeshoptSimplifier } from "meshoptimizer";
import { readGlb, writeGlb } from "./glb-lib.mjs";

// --mobile derives a low-memory tier from an already-optimized desktop GLB:
// same geometry, but textures re-capped far smaller. The desktop GLB decodes
// to 0.5-1.4 GB of GPU texture memory (1024-cap color maps x a few hundred
// materials), which blows a phone tab's budget the instant it uploads - the OS
// kills the tab and the browser "reloads after finishing". A 256-cap tier
// drops that ~16x per oversized texture, into a range a phone can hold. Desktop
// keeps the full-resolution GLB untouched; the viewer picks the tier per device.
const MOBILE = process.argv.includes("--mobile");
const positional = process.argv.slice(2).filter(a => !a.startsWith("--"));
const inPath = positional[0] ?? "data/de_dust2_textured.glb";
const outPath = positional[1] ?? "data/de_dust2_textured.optimized.glb";

const doc = await readGlb(inPath);

for (const material of doc.getRoot().listMaterials()) {
  material.setNormalTexture(null);
  material.setOcclusionTexture(null);
  material.setMetallicRoughnessTexture(null);
}

// Some maps export a handful of trivially-skinned props (a door or sign
// rigged to a single hinge joint) - we never play animations, so the rig is
// dead weight, but worse than that it actively breaks rendering: after
// quantize()/draco() touch the joint node hierarchy, three.js's GLTFLoader
// can end up with a SkinnedMesh whose skeleton never resolves, and
// WebGLRenderer throws "Cannot read properties of undefined (reading
// 'frame')" the instant that mesh is rendered - not caught by any try/catch,
// so it silently kills the render loop's next frame forever. Stripping
// skinning outright (converting every SkinnedMesh back to a plain static
// mesh in its bind pose) sidesteps the whole class of bug.
for (const node of doc.getRoot().listNodes()) {
  node.setSkin(null);
}
for (const mesh of doc.getRoot().listMeshes()) {
  for (const primitive of mesh.listPrimitives()) {
    primitive.setAttribute("JOINTS_0", null);
    primitive.setAttribute("WEIGHTS_0", null);
  }
}

// ensureTexturedScene() rebuilds every material as an unlit MeshBasicMaterial,
// which reads only the base color map, the UVs that sample it, and (for the
// handful of untextured meshes) COLOR_0. Everything else on the vertex is
// shipped, decompressed and uploaded to the GPU for nothing: NORMAL and TANGENT
// feed a lighting model that isn't running, and _TEXCOORD_4 is Source 2's
// lightmap UV set, which has no baked lightmap to sample here.
const stripped = { NORMAL: 0, TANGENT: 0, _TEXCOORD_4: 0, COLOR_0: 0 };
for (const mesh of doc.getRoot().listMeshes()) {
  for (const primitive of mesh.listPrimitives()) {
    for (const name of ["NORMAL", "TANGENT", "_TEXCOORD_4"]) {
      if (primitive.getAttribute(name)) {
        primitive.setAttribute(name, null);
        stripped[name]++;
      }
    }
    // COLOR_0 is only the fallback tint for meshes that have no base color
    // texture, so it is dead weight on every mesh that does have one.
    if (primitive.getMaterial()?.getBaseColorTexture() && primitive.getAttribute("COLOR_0")) {
      primitive.setAttribute("COLOR_0", null);
      stripped.COLOR_0++;
    }
  }
}
console.log("stripped unread vertex attributes:", stripped);

// Re-running this on an already-optimized .glb (the raw ~1GB exports are not
// kept) must not put the textures through a second lossy WebP generation, and
// must not quantize already-quantized positions a second time.
const textures = doc.getRoot().listTextures();
const alreadyCompressed = textures.length > 0 && textures.every(t => t.getMimeType() === "image/webp");

if (MOBILE) {
  await MeshoptSimplifier.ready;
}

await doc.transform(
  prune(),
  // Mobile: always re-cap textures to 256 even though the input is already WebP
  // (the whole point is to shrink them further), and skip quantize() - the
  // desktop input this derives from is already quantized. Desktop: the usual
  // 1024-cap first pass, skipped when re-running on already-WebP input.
  ...(MOBILE
    ? [textureCompress({ targetFormat: "webp", resize: [256, 256], quality: 75 })]
    : alreadyCompressed ? [] : [
      textureCompress({ targetFormat: "webp", resize: [1024, 1024], quality: 82 }),
      quantize(),
    ]),
  // The exporter emits one primitive per source mesh instance, so a map arrives
  // as thousands of individually-drawn props that mostly share a few hundred
  // materials - de_mirage was 3,480 draw calls a frame. Baking the node
  // transforms down and merging every primitive that shares a material collapses
  // that to a few hundred. This merges meshes; it does not touch triangles.
  flatten(),
  join({ keepNamed: false }),
  // Mobile also decimates geometry. After the texture re-cap, the heaviest maps
  // are dominated by raw vertex count (inferno is 7.4M verts ~= 180MB of GPU
  // buffers on its own), enough to keep a phone tab near its ceiling. CS maps
  // are mostly large coplanar walls and floors, which collapse almost losslessly
  // - weld() merges the split vertices join() leaves behind so simplify() can
  // actually reach across a merged surface; error is kept tight so silhouettes
  // and the sightlines a reference view exists to show stay put.
  ...(MOBILE ? [
    weld(),
    simplify({ simplifier: MeshoptSimplifier, ratio: 0.5, error: 0.008 }),
  ] : []),
  dedup(),
  draco(),
);

await writeGlb(outPath, doc);
console.log("wrote", outPath);

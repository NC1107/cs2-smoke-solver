// Post-processes the raw `exportgltf` output into a small, self-contained
// .glb suitable for serving over a residential connection.
//
// The raw export (data/de_dust2_textured.glb + ~560 loose PNGs alongside it)
// is over 1GB combined: the .glb itself is almost entirely uncompressed mesh
// data, and every material's normal/roughness/metalness/occlusion maps are
// fetched by GLTFLoader as separate HTTP requests even though the viewer
// only ever renders unlit (map + color) - see ensureTexturedScene() in
// viewer/js/view3d.js. None of that is needed for a background reference
// view, so this strips it: drop the unused PBR texture slots, downscale and
// WebP-compress the remaining color textures, quantize vertex precision,
// Draco-compress the geometry, and embed everything into one .glb (killing
// the loose-PNG HTTP requests too). de_dust2 went from ~1.1GB to 54.6MB.
//
// Usage: node rig/optimize-textured-glb.mjs [input.glb] [output.glb]
import { NodeIO } from "@gltf-transform/core";
import { ALL_EXTENSIONS } from "@gltf-transform/extensions";
import { prune, textureCompress, quantize, dedup, draco } from "@gltf-transform/functions";
import draco3d from "draco3dgltf";

const inPath = process.argv[2] ?? "data/de_dust2_textured.glb";
const outPath = process.argv[3] ?? "data/de_dust2_textured.optimized.glb";

const [decoder, encoder] = await Promise.all([
  draco3d.createDecoderModule(),
  draco3d.createEncoderModule(),
]);
const io = new NodeIO()
  .registerExtensions(ALL_EXTENSIONS)
  .registerDependencies({ "draco3d.decoder": decoder, "draco3d.encoder": encoder });
const doc = await io.read(inPath);

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

await doc.transform(
  prune(),
  textureCompress({ targetFormat: "webp", resize: [1024, 1024], quality: 82 }),
  quantize(),
  draco(),
  dedup(),
);

await io.write(outPath, doc);
console.log("wrote", outPath);

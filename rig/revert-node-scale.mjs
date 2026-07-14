// One-off: revert a specific node's scale back to a known prior value.
// Used to undo an over-eager correction applied before fix-prop-scale.mjs's
// 2-instance handling was tightened (see its comments for why 2-instance
// groups no longer get an automatic median-based correction).
import { NodeIO } from "@gltf-transform/core";
import { ALL_EXTENSIONS } from "@gltf-transform/extensions";
import draco3d from "draco3dgltf";

const [inPath, outPath, nodeName, fromScale, x, y, z] = process.argv.slice(2);
const from = Number(fromScale);

const [decoder, encoder] = await Promise.all([
  draco3d.createDecoderModule(),
  draco3d.createEncoderModule(),
]);
const io = new NodeIO()
  .registerExtensions(ALL_EXTENSIONS)
  .registerDependencies({ "draco3d.decoder": decoder, "draco3d.encoder": encoder });
const doc = await io.read(inPath);

let count = 0;
for (const node of doc.getRoot().listNodes()) {
  // Multiple instances can share the same node name, so also require the
  // current scale to match what we expect to be reverting (within a wide
  // tolerance for float drift) - otherwise every same-named sibling gets
  // clobbered, not just the one that actually needs fixing.
  if (node.getName() === nodeName && Math.abs(node.getScale()[0] - from) < 0.01) {
    console.log(`  reverting ${node.getName()}: ${node.getScale()} -> [${x},${y},${z}]`);
    node.setScale([Number(x), Number(y), Number(z)]);
    count++;
  }
}
console.log(`reverted ${count} node(s)`);
await io.write(outPath, doc);

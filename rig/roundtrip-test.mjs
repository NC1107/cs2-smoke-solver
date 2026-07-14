import { NodeIO } from "@gltf-transform/core";
import { ALL_EXTENSIONS } from "@gltf-transform/extensions";
import draco3d from "draco3dgltf";

const [decoder, encoder] = await Promise.all([
  draco3d.createDecoderModule(),
  draco3d.createEncoderModule(),
]);
const io = new NodeIO()
  .registerExtensions(ALL_EXTENSIONS)
  .registerDependencies({ "draco3d.decoder": decoder, "draco3d.encoder": encoder });
const doc = await io.read(process.argv[2]);
await io.write(process.argv[3], doc);
console.log("done");

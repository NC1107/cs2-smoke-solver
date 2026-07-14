// Builds the little map silhouettes the first-run picker shows, from the radar
// slices viewerdata already emits.
//
// The radar encodes class in R (0 floor, 128 cover, 255 wall) and ground height
// in G, and is recolored to the live theme at runtime - so it cannot be used as
// an <img> directly (raw, it is a red/green mess). The thumbnail instead throws
// away the color entirely and keeps only the shape, as black at varying alpha.
// One asset then works on either theme: it shows through dark on a light card,
// and the viewer inverts it for dark mode.
//
// Usage: node rig/make-map-thumbs.mjs [data-dir]
import sharp from "sharp";
import fs from "fs";
import path from "path";

const dataDir = process.argv[2] ?? "data";
const WIDTH = 220;

// Floor stays lighter than the walls so the shape reads as a map with an outline
// rather than a solid blob, but not so light that it disappears at thumbnail size.
const ALPHA = { floor: 105, cover: 175, wall: 255 };

for (const file of fs.readdirSync(dataDir).filter(f => f.endsWith(".viewer-map.png"))) {
  const map = file.replace(".viewer-map.png", "");
  const src = path.join(dataDir, file);
  const { data, info } = await sharp(src).ensureAlpha().raw().toBuffer({ resolveWithObject: true });

  const out = Buffer.alloc(info.width * info.height * 4);
  for (let i = 0; i < info.width * info.height; i++) {
    const cls = data[i * 4];
    const opaque = data[i * 4 + 3] !== 0;
    out[i * 4 + 3] = !opaque ? 0 : cls === 255 ? ALPHA.wall : cls === 128 ? ALPHA.cover : ALPHA.floor;
  }

  const dest = path.join(dataDir, `${map}.thumb.png`);
  await sharp(out, { raw: { width: info.width, height: info.height, channels: 4 } })
    // The radar slice is a bounding box around the whole world, so the playable
    // shape sits in a sea of transparent margin; without trimming it, the map is
    // a speck in the middle of the thumbnail.
    .trim({ threshold: 0 })
    .resize({ width: WIDTH, fit: "inside", withoutEnlargement: true })
    .png({ compressionLevel: 9, palette: true })
    .toFile(dest);
  console.log(`${dest}  ${(fs.statSync(dest).size / 1024).toFixed(1)}KB`);
}

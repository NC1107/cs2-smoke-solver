// Static checks for the viewer that need no browser: the failures they catch
// are the ones that have shipped before and were only found by clicking.
//
//   - every module parses (a stray token stops the whole viewer, silently on
//     a cached page)
//   - every document.getElementById("...") names an id that exists in
//     index.html (an element renamed in the markup but not in the JS throws
//     on the first call, or worse, null-checks its way to a dead control)
//   - every ?v= cache token agrees (one file left on the old token is an
//     ES-module graph that half-updates behind Cloudflare's cache)
//   - every <div> in index.html closes (a stray closer once swallowed every
//     card after the first, and the DOM simply had one card)
import { readFileSync, readdirSync } from "node:fs";
import { join } from "node:path";
import vm from "node:vm";

const root = new URL("..", import.meta.url).pathname;
const viewer = join(root, "viewer");
const problems = [];

const html = readFileSync(join(viewer, "index.html"), "utf8");
const ids = new Set([...html.matchAll(/\sid="([^"]+)"/g)].map(m => m[1]));

const jsFiles = readdirSync(join(viewer, "js")).filter(f => f.endsWith(".js")).map(f => join("js", f));
for (const rel of jsFiles) {
  const src = readFileSync(join(viewer, rel), "utf8");
  try {
    new vm.SourceTextModule(src, { identifier: rel });
  } catch (e) {
    problems.push(`${rel}: does not parse - ${e.message}`);
    continue;
  }
  // validation.js drives validation.html, which has its own ids.
  if (rel.endsWith("validation.js")) {
    continue;
  }
  // Ids the script creates itself count as existing.
  for (const m of src.matchAll(/\.id = "([^"]+)"/g)) {
    ids.add(m[1]);
  }
  for (const m of src.matchAll(/getElementById\("([^"]+)"\)/g)) {
    if (!ids.has(m[1])) {
      problems.push(`${rel}: getElementById("${m[1]}") but index.html has no such id`);
    }
  }
}

const tokens = new Map();
for (const rel of ["index.html", "validation.html", ...jsFiles]) {
  const src = readFileSync(join(viewer, rel), "utf8");
  for (const m of src.matchAll(/\?v=(\d+)/g)) {
    tokens.set(m[1], [...(tokens.get(m[1]) ?? []), rel]);
  }
}
if (tokens.size > 1) {
  problems.push(`mixed ?v= tokens: ${[...tokens].map(([t, files]) => `${t} in ${[...new Set(files)].join(", ")}`).join("; ")}`);
}

const opens = (html.match(/<div\b/g) ?? []).length;
const closes = (html.match(/<\/div>/g) ?? []).length;
if (opens !== closes) {
  problems.push(`index.html: ${opens} <div> but ${closes} </div>`);
}

if (problems.length) {
  console.error(problems.map(p => "  " + p).join("\n"));
  process.exit(1);
}
console.log(`viewer OK: ${jsFiles.length} modules, ${ids.size} ids, ?v=${[...tokens.keys()][0]}`);

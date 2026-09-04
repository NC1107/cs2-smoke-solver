// Unit checks for the viewer's pure logic, run with node (no browser).
// state.js is import-free by design; it reads document.getElementById once
// at load for the filter controls, which a stub answers with nulls.
//
// The first thing checked is the one most likely to drift silently: the
// viewer's humanError() is a hand-kept copy of HumanError.cs, and the two
// must agree or the list order (server) and the default filter (viewer)
// disagree about the same lineup. The C# side writes fixtures to
// tests/Sim.Tests/fixtures/human-error.json (HumanErrorParityTests); this
// evaluates the same inputs in JS.
import { readFileSync } from "node:fs";
import { join } from "node:path";
import vm from "node:vm";

const root = new URL("..", import.meta.url).pathname;
const src = readFileSync(join(root, "viewer/js/state.js"), "utf8");

const sandbox = {
  document: { getElementById: () => null, querySelector: () => null },
  localStorage: { getItem: () => null, setItem: () => {}, removeItem: () => {} },
  matchMedia: () => ({ matches: false }),
  navigator: { deviceMemory: 8 },
  window: {},
  console,
};
sandbox.window = sandbox;
const context = vm.createContext(sandbox);
const mod = new vm.SourceTextModule(src, { context, identifier: "state.js" });
await mod.link(() => { throw new Error("state.js must stay import-free"); });
await mod.evaluate();
const { humanError, referenceBand, difficultyWords } = mod.namespace;

let failures = 0;
const check = (ok, msg) => { if (!ok) { failures++; console.error("  FAIL " + msg); } };

// 1. Parity with HumanError.cs on the C# side's own fixtures.
const fixtures = JSON.parse(readFileSync(join(root, "tests/Sim.Tests/fixtures/human-error.json"), "utf8"));
for (const f of fixtures) {
  const l = {
    pin: f.pin === 2 ? "corner" : f.pin === 1 ? "wall" : null,
    aimRef: { band: f.band },
    feet: [0, 0, 0],
    rest: [f.distance, 0, 0],
    type: f.type,
    scatter: f.scatter,
    stability: f.stability ?? 1,
  };
  const js = humanError(l);
  check(Math.abs(js - f.expected) < 0.05, `humanError(${JSON.stringify(f)}) = ${js.toFixed(2)} in JS, ${f.expected.toFixed(2)} in C#`);
}

// 2. The band fallbacks for results cached before the field existed.
check(referenceBand({ aimRef: { tier: "sky" } }) === 6, "sky without a band is blind");
check(referenceBand({ aimRef: { tier: "edge" } }) === 3, "an edge without a band is a landmark");
check(referenceBand({ aimRef: { band: 0 } }) === 0, "band passes through");

// 3. Difficulty words follow the estimate and the movement ceiling.
const corner = { pin: "corner", aimRef: { band: 6 }, feet: [0, 0, 0], rest: [100, 0, 0], type: "Stand", scatter: 0 };
check(difficultyWords(corner).word === "Easy", `a corner lob at 100u is Easy, got ${difficultyWords(corner).word}`);
const jump = { ...corner, type: "JumpThrow" };
check(difficultyWords(jump).word !== "Easy", "a jump throw is never Easy");
const open = { pin: null, aimRef: { band: 0 }, feet: [0, 0, 0], rest: [1500, 0, 0], type: "Stand", scatter: 0 };
check(difficultyWords(open).word === "Tricky", `open ground at 1500u is Tricky, got ${difficultyWords(open).word}`);

if (failures) {
  process.exit(1);
}
console.log(`viewer logic OK: ${fixtures.length} parity cases, bands, difficulty`);

// Shared mutable state, replacing the old IIFE closure variables. Keep this
// module dumb: data, shared element refs, and pure derived-data helpers only.
// It imports nothing, so every other module may import it freely.

export const state = {
  currentMap: null,
  // Bumped once per map switch. Any async load (map data, radar, 3D mesh,
  // textured GLB) captures it before its awaits and abandons its result if the
  // value moved on - so a slow load for a map the user already left can never
  // clobber the current one's geometry or leak an orphaned WebGL context.
  mapGeneration: 0,
  mapData: null,
  colors: {},
  picking: false,
  target: null,
  result: null,
  selected: -1,
  busy: false,
  // Live solve progress: { phase, total, candidates, checked: [[x, y, z, hits],
  // ...], verified: [[x, y, z, ok], ...] }. Non-null only while a streamed solve
  // is in flight; both views paint the checked origins and verify verdicts as
  // dots so sweep speed and coverage are visible. The 2D view ignores z; the 3D
  // view needs it to stand each dot on the floor the origin actually sits on.
  progress: null,
  heatOn: false,
  // Second heat view: color evaluated origins by stand-spot quality (corner/
  // wall pin + verified) instead of raw coverage. Only meaningful with heatOn.
  heatSpots: false,
  // 3D center-crosshair preference; main.js loads/persists it in localStorage.
  crosshairOn: true,
  // "off" | "cross" | "smoke" - the single source for which aiming overlay is
  // drawn; crosshairOn/reticleOn below are derived from it.
  reticleMode: "cross",
  // The magenta collision-box overlay (grenade-clips/glass) - on by default so
  // the 3D view keeps showing what really stops a smoke; toggleable.
  collisionOn: true,
  // The full-screen "+" lineup crosshair - a numbered tick ruler overlay for
  // lining grenades up by tick, like CS2's grenade crosshair (off by default).
  reticleOn: false,
  // Player spawn positions for the current map ({ t: [[x,y,z]...], ct: [...] })
  // and whether their markers are shown. Fetched on map load; clicking a shown
  // spawn solves a smoke from that exact spot to the current target.
  spawns: null,
  spawnsOn: false,
  // Pro smoke throw/land points from parsed HLTV demos ({ throws, lands }) and
  // whether the density heatmap of them is shown.
  prosmokes: null,
  // Favourites, persisted per map (see favKey): a lineup you saved survives
  // re-solving, filtering and reloading.
  favorites: new Set(),
  // The throw spot the last one-spot solve used, for copying back out.
  lastOrigin: null,
  // A spawn marker clicked before a target exists: held, not discarded, so the
  // click means "throw from here" rather than "put the target on the spawn".
  pendingOrigin: null,
  // Whether the 3D camera is latched overhead, so the control that put it
  // there can also bring it back.
  topDownOn: false,
  // The map is listening for a click that sets the throw spot, the way
  // `picking` means it is listening for the target.
  pickingOrigin: false,
  // Whether this map has a mesh diff at all, learned from a HEAD before the
  // multi-megabyte payload is worth fetching.
  meshdiffAvailable: false,
  // Detailed rows: the solver's own numbers back on every line. Off by
  // default and remembered per browser.
  expertRows: (() => {
    try { return localStorage.getItem("smoke.expertRows") === "1"; } catch { return false; }
  })(),
  // Whether "pros throw from here" counts toward the ranking. On by default,
  // but it is one signal among several and a map with thousands of demo points
  // can drown out spots that are simply better throws.
  proWeighting: true,
  // A stacked-floor click is unanswered: nothing may search until it is.
  awaitingLevel: false,
  prosmokesOn: false,
  // Which team's smokes the pro heatmap shows: "all", "t" (attacker), "ct" (defender).
  proSide: "all",
  // Dev overlay from the meshdiff CLI command: { map, step, cells: [[x,y,z,kind],
  // ...] }, kind 0 = render has a surface physics lacks (grenades fly through
  // it), kind 1 = physics has a surface render lacks (a phantom bounce). Null
  // on every map until someone runs meshdiff for it - the toggle stays hidden.
  meshdiff: null,
  meshdiffOn: false,
  hovered: -1,
  canvas: document.getElementById("map"),
  stage3d: document.getElementById("stage3d"),
  statusEl: document.getElementById("status"),
  filters: {
    type: document.getElementById("f-type"),
    strength: document.getElementById("f-strength"),
    bounces: document.getElementById("f-bounces"),
    flight: document.getElementById("f-flight"),
    stability: document.getElementById("f-stability"),
    sky: document.getElementById("f-sky"),
    precision: document.getElementById("f-precision"),
    pin: document.getElementById("f-pin"),
  },
};

// How far above the horizon a throw aims, in degrees. Source's pitch is
// negative upwards (setang), so this is the sign flip.
export const skyAngle = l => Math.max(0, -l.pitch);

// Sky-aimed throws are steep by nature (median 54 degrees up, and none of them
// below 20), so any of these settings other than "any" also drops the handful
// with nothing under the reticle anywhere - there is no way to reproduce that
// aim in game, whatever angle it is at.
//
// Only throws that put the crosshair itself on open sky are judged on their aim
// angle at all. A steep throw with a rooftop or wall still under the crosshair
// is lined up against that, not against the horizon, however high it points.
function skyAllowed(l, setting) {
  if (l.aimRef?.tier === "sky") {
    return false;
  }
  if (!(l.aimRef?.sky > 0.95)) {
    return true;
  }
  return setting !== "off" && skyAngle(l) <= Number.parseFloat(setting);
}

// These pure helpers live here (not in a feature module) because map2d,
// view3d, and panel all need them and are not allowed to cross-import.
export function filtered() {
  if (!state.result) {
    return [];
  }
  // A shared single lineup is an explicit pick, not a search result: the
  // filters describe what to surface in a sweep, so applying them here could
  // hide the very throw the link was meant to open (a sky shot under the
  // default sky filter, say).
  if (state.result.single) {
    return state.result.lineups.filter(l => !l._removed);
  }
  const filters = state.filters;
  const t = state.result.target;
  return state.result.lineups.filter(l =>
    !l._removed &&
    (!filters.type.value || l.type === filters.type.value) &&
    (!filters.strength.value || Math.abs(l.strength - Number.parseFloat(filters.strength.value)) < 0.01) &&
    (!filters.bounces.value || l.Bounces <= Number.parseInt(filters.bounces.value)) &&
    (!filters.flight.value || l.flightTime <= Number.parseFloat(filters.flight.value)) &&
    (!filters.stability.value || l.stability >= Number.parseFloat(filters.stability.value)) &&
    (!filters.sky.value || skyAllowed(l, filters.sky.value)) &&
    (!filters.precision.value || Math.hypot(l.rest[0] - t[0], l.rest[1] - t[1]) <= Number.parseFloat(filters.precision.value)) &&
    (!filters.pin.value || (filters.pin.value === "corner" ? l.pin === "corner" : !!l.pin)));
}

// A lineup's stable identity across solves: the throw itself, quantised to
// what a player could reproduce. Feet to the unit and angles to a tenth of a
// degree are finer than anyone can stand or aim, so the same throw found by
// two different solves keys the same.
export function favKey(l) {
  return [
    Math.round(l.feet[0]), Math.round(l.feet[1]), Math.round(l.feet[2]),
    l.type, l.strength.toFixed(2), l.yaw.toFixed(1), l.pitch.toFixed(1),
  ].join("|");
}

function favStorageKey(map) {
  return `smokesolver.favs.${map}`;
}

export function loadFavorites(map) {
  try {
    const raw = localStorage.getItem(favStorageKey(map));
    state.favorites = new Set(raw ? JSON.parse(raw) : []);
  } catch {
    // A private window or blocked storage just means favourites do not persist.
    state.favorites = new Set();
  }
}

export function setFavorite(map, l, on) {
  const key = favKey(l);
  if (on) {
    state.favorites.add(key);
  } else {
    state.favorites.delete(key);
  }
  l._favorite = on;
  try {
    localStorage.setItem(favStorageKey(map), JSON.stringify([...state.favorites]));
  } catch {
    // Not persisting is survivable; the in-session flag above still holds.
  }
}

export function isFavorite(l) {
  return state.favorites.has(favKey(l));
}

// Does a pro throw THIS SMOKE from here? The demo data pairs each throw origin
// with the spot that smoke landed, and both halves have to match - a spot near
// a pro's feet says nothing on its own, because on a busy map almost every
// stand spot is near somewhere a pro once threw something.
//
// Origins recorded before 2026-08-30 carry no height ([x, y, side]), so a spot
// on top of a truck matches a pro who threw from the ground beside it. Where a
// height is present it is required to agree; where it is absent the match is
// area-level and is worth saying so in the badge rather than claiming more
// than the data supports. `rig/parse-demo-smokes.py` now records the height,
// so re-parsed maps get the stricter test automatically.
// The same spot AND the same landing, judged by the bounds the user set. A
// pro's feet being nearby says nothing on its own: on a busy map almost every
// stand spot is near somewhere a pro once threw something, which is how 45 of
// 400 nuke lineups and 72 of 400 mirage lineups collected the badge for
// targets no pro was smoking.
//
// The landing test uses the Precision filter's own value, so "a pro throws
// this" means exactly what the list already means by "this lands where I
// asked". With the default 32u that takes mirage from 72 badges to 19, and the
// arbitrary nuke target from 45 to none - which is the honest answer.
const PRO_ORIGIN_RADIUS = 64;
const PRO_ORIGIN_RISE = 48;
// Used only when the Precision filter is set to "any": a landing bound still
// has to exist, or every pro smoke on the map matches every lineup.
const PRO_LANDING_FALLBACK = 64;
export function proMatched(l) {
  const pro = state.prosmokes;
  if (!pro?.throws || !pro?.lands || !l.rest) {
    return false;
  }
  const precision = Number.parseFloat(state.filters.precision?.value);
  const landingRadius = Number.isFinite(precision) ? precision : PRO_LANDING_FALLBACK;
  const n = Math.min(pro.throws.length, pro.lands.length);
  for (let i = 0; i < n; i++) {
    const t = pro.throws[i];
    if (Math.abs(t[0] - l.feet[0]) > PRO_ORIGIN_RADIUS || Math.abs(t[1] - l.feet[1]) > PRO_ORIGIN_RADIUS) {
      continue;
    }
    // Four entries means the height was recorded (x, y, z, side). Origins
    // parsed before 2026-08-30 carry none, so the spot test stays flat there;
    // re-parsing a map's demos tightens it automatically.
    if (t.length > 3 && Math.abs(t[2] - l.feet[2]) > PRO_ORIGIN_RISE) {
      continue;
    }
    const land = pro.lands[i];
    if (Math.hypot(t[0] - l.feet[0], t[1] - l.feet[1]) <= PRO_ORIGIN_RADIUS &&
        Math.hypot(land[0] - l.rest[0], land[1] - l.rest[1]) <= landingRadius) {
      return true;
    }
  }
  return false;
}

// True when the map's demo origins carry no height, so a pro match is only
// area-level and the badge should not promise a spot.
export function proOriginsAreFlat() {
  const t = state.prosmokes?.throws;
  return !!t?.length && t[0].length <= 3;
}

// Weights and caps come from auditing a real 400-lineup solve rather than
// taste: with the first set, 78% of results scored negative and the spread was
// dominated by distance and flight time, so the number said little about which
// throw to pick. These put the median near 37 and leave 20% negative, which is
// about right for "most results are usable, some are genuinely bad".
//
// Caps matter as much as weights. Without them a far, bouncy throw accumulated
// an unbounded penalty and buried the qualities that decide reproducibility -
// a pin and a real aim reference - under arithmetic.
const SCORE_BASE = 140;
const MISS_PER_UNIT = 1.2, MISS_CAP = 90;
const STABILITY_WEIGHT = 70;
const SCATTER_CAP = 64;
const BOUNCE_PER = 4, BOUNCE_CAP = 32;
const FLIGHT_PER_SECOND = 2, FLIGHT_CAP = 18;

export function scoreBreakdown(l, target) {
  const parts = [];
  const add = (label, delta) => {
    if (Math.abs(delta) >= 0.5) {
      parts.push({ label, delta: Math.round(delta) });
    }
  };

  const missXY = target ? Math.hypot(l.rest[0] - target[0], l.rest[1] - target[1]) : 0;
  add(`lands ${missXY.toFixed(1)}u off`, -Math.min(missXY * MISS_PER_UNIT, MISS_CAP));
  add(l.pin === "corner" ? "corner pin - position is exact"
    : l.pin === "wall" ? "wall pin - walk into it" : "",
    l.pin === "corner" ? 90 : l.pin === "wall" ? 60 : 0);
  add(`${Math.round((l.stability ?? 0) * 100)}% aim tolerance`, -(1 - (l.stability ?? 0)) * STABILITY_WEIGHT);
  // The API calls this `scatter`; scoring read `restScatter` and so never
  // applied it at all, which the score audit caught.
  const scatter = l.scatter ?? 0;
  add(`moves ${Math.round(scatter)}u if your feet shift`, -Math.min(scatter, SCATTER_CAP));
  const tier = l.aimRef?.tier;
  add(tier === "sky" ? "nothing on screen to aim at"
    : tier === "flat" ? "aims at a blank surface"
    : tier === "reticle" ? "aim reference off-centre" : "",
    tier === "sky" ? -70 : tier === "flat" ? -35 : tier === "reticle" ? -10 : 0);
  add("you are visible while throwing", l.exposed ? -50 : 0);
  add(`${l.Bounces} bounce${l.Bounces === 1 ? "" : "s"}`, -Math.min((l.Bounces ?? 0) * BOUNCE_PER, BOUNCE_CAP));
  add(`${(l.flightTime ?? 0).toFixed(1)}s in the air`, -Math.min((l.flightTime ?? 0) * FLIGHT_PER_SECOND, FLIGHT_CAP));
  if (state.proWeighting && proMatched(l)) {
    // Worth less when the recorded origins have no height: the match is then
    // "from around here" rather than "from here".
    add(proOriginsAreFlat() ? "pros smoke this from around here" : "pros throw this smoke from here",
      proOriginsAreFlat() ? 25 : 45);
  }

  const total = parts.reduce((sum, p) => sum + p.delta, SCORE_BASE);
  return { total: Math.round(total), parts };
}

export function lineupScore(l, target) {
  return scoreBreakdown(l, target).total;
}

const typeShort = { Stand: "stand", Crouch: "crouch", JumpThrow: "jump", CrouchJumpThrow: "crouch+jump", RunJumpThrow: "run+jump" };
// Movement keys behind a running jump throw's run direction (server runDeg:
// 0 = W, +90 = A, -90 = D, +-45 = diagonals). Banded, not exact-matched, so
// a float that went through JSON still labels correctly.
export const runKeys = deg =>
  deg > 67.5 ? "A" : deg > 22.5 ? "W+A" : deg < -67.5 ? "D" : deg < -22.5 ? "W+D" : "W";
// The movement label with the run direction folded in, e.g. "run+jump (A)".
export const typeLabel = l =>
  l.type === "RunJumpThrow" ? `run+jump (${runKeys(l.runDeg ?? 0)})` : typeShort[l.type];
export const clickShort = s => s >= 0.99 ? "left click" : s >= 0.49 ? "mid (L+R)" : "right click";
export const clickClass = s => s >= 0.99 ? "left" : s >= 0.49 ? "mid" : "right";

// ---- Plain language -------------------------------------------------------
//
// The same facts as the labels above, said the way every lineup tutorial says
// them. Movement keys ("run+jump (A)") are our own shorthand and read as a
// keybind nobody was told about; every published guide spells the direction
// out instead. These are what the cards show; the shorthand stays for the
// dense expert row and the filter menus.
const runWords = { A: "left", "W+A": "forward-left", D: "right", "W+D": "forward-right", W: "forward" };
const typeWords = {
  Stand: "Standing", Crouch: "Crouching", JumpThrow: "Jump throw",
  CrouchJumpThrow: "Crouch jump", RunJumpThrow: "Run-jump",
};
export const movementWords = l =>
  l.type === "RunJumpThrow"
    ? `Run-jump (${runWords[runKeys(l.runDeg ?? 0)]})`
    : typeWords[l.type];
export const clickWords = s => s >= 0.99 ? "Left click" : s >= 0.49 ? "Both buttons" : "Right click";

// What the player aims at, in words. The tier already encodes the answer; the
// degree figure it was shown with ("1.5 deg") is meaningless without reading
// the solver that produced it.
export const aimWords = l => {
  if (!l.aimRef) {
    return "";
  }
  switch (l.aimRef.tier) {
    case "sky": return "open sky, nothing to line up against";
    case "reticle": return "off to the side, on the reticle line";
    case "flat": return "a blank wall, hard to line up exactly";
    default: return "a wall or corner edge";
  }
};

// One word for "how hard is this to land", from the four things that decide
// it: what your body has to do, whether there is anything on screen to aim at,
// how much the throw forgives a shifted foot or a nudged crosshair, and whether
// geometry places your feet for you.
//
// Movement is a ceiling, not a bonus. A run-jump asks for a direction, a jump
// timed against a moving body, and a release - three things to get right before
// the aim even counts - so it cannot be "Easy" however forgiving the numbers
// say it is. A throw with nothing to aim at cannot be "Reliable" either: the
// stability figure measures how far the crosshair may drift, which says nothing
// about your odds of putting it in the right place to begin with when there is
// no edge or silhouette to put it on.
//
// A word, not the score: the score ranks lineups against each other, and no
// player has a mental model for "219". Difficulty deliberately says nothing
// about danger - being seen while throwing is a separate fact with a separate
// tag, and folding it in here would hide it.
const DIFFICULTY = ["Tricky", "Needs practice", "Reliable", "Easy"];

// The best a throw of this kind can be called, whatever else is in its favour.
const MOVEMENT_CEILING = {
  Stand: 3, Crouch: 3,
  JumpThrow: 2, CrouchJumpThrow: 2,
  RunJumpThrow: 2,
};
// And the best an aim with this much to line up against can be called.
const AIM_CEILING = { edge: 3, reticle: 3, flat: 1, sky: 0 };

export const difficultyWords = l => {
  const pinned = l.pin === "wall" || l.pin === "corner";
  const tier = l.aimRef?.tier ?? "flat";
  const stability = l.stability ?? 0;

  // Start from how forgiving the throw is, then credit a stand spot the
  // geometry places for you.
  let rank = stability >= 0.95 ? 3 : stability >= 0.8 ? 2 : stability >= 0.5 ? 1 : 0;
  if (pinned) {
    rank += 1;
  }
  rank = Math.min(rank, MOVEMENT_CEILING[l.type] ?? 3, AIM_CEILING[tier] ?? 1);
  // A moving throw with nothing under the crosshair is the case this ranking
  // exists to be honest about: nothing pins the feet, the body is in the air,
  // and the only reference is a silhouette off to one side that a reticle arm
  // happens to cross. However forgiving the numbers are, that is something to
  // practise, not something to rely on. An edge at the crosshair, or a wall
  // that places the feet, earns the run-jump its way back up.
  if (l.type === "RunJumpThrow" && !pinned && tier !== "edge") {
    rank = Math.min(rank, 1);
  }
  const word = DIFFICULTY[Math.max(0, Math.min(DIFFICULTY.length - 1, rank))];
  return { word, cls: word === "Easy" ? "easy" : word === "Reliable" ? "reliable"
    : word === "Tricky" ? "tricky" : "practice" };
};

// Phones and other low-memory devices, where the full-resolution textured GLB
// (0.5-1.4 GB of decoded GPU texture + geometry memory) exceeds a browser tab's
// budget: the OS kills the tab and it "reloads after finishing the download".
// One flag for both consumers of that fact - the heavy-preview auto-load gate
// (main.js) and the textured-GLB tier selection (textured-scene.js picks the
// smaller data/{map}_textured.mobile.glb). Coarse pointer catches phones/
// tablets; deviceMemory (Chromium-only, absent elsewhere) catches low-RAM
// desktops.
export const lowMemoryDevice =
  (typeof matchMedia !== "undefined" && matchMedia("(pointer: coarse)").matches) ||
  (typeof navigator !== "undefined" && navigator.deviceMemory > 0 && navigator.deviceMemory < 4);

// Shared physical/UI constants (M44); world units unless noted.
export const SMOKE_BLOOM_RADIUS = 144;
export const PICK_RADIUS_PX = 12;
export const TOUCH_PICK_RADIUS_PX = 22; // finger-sized grab zone (~44px diameter)
export const HEAT_CELL = 24;
// Eye height above feet by throw type - 64.06 standing, 46.04 crouched,
// measured from CS2 telemetry (Valve's 64.093811 eye-above-floor minus the
// 0.03125 feet-above-floor gap). The ONE table for every consumer: a second
// copy in the 3D module once drifted to a plain 64.
export const DEFAULT_EYE_HEIGHT = 64.06;
export const EYE_HEIGHT_BY_TYPE = { Crouch: 46.04, CrouchJumpThrow: 46.04 };

// Minimal HTML escaper for API-derived strings rendered via innerHTML (L20).
export const esc = s => String(s).replace(/[&<>"']/g,
  c => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", "\"": "&quot;", "'": "&#39;" }[c]));

// One tap-vs-drag threshold for every canvas: a pointer that moves further
// than this between down and up is a camera gesture, not a click.
const DRAG_THRESHOLD_PX = 4;
export function isDrag(downX, downY, x, y) {
  return Math.hypot(x - downX, y - downY) > DRAG_THRESHOLD_PX;
}

// Shared mutable state, replacing the old IIFE closure variables. Keep this
// module dumb: data, shared element refs, and pure derived-data helpers only.
// It imports nothing, so every other module may import it freely.

export const state = {
  currentMap: null,
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
    (!filters.precision.value || Math.hypot(l.rest[0] - t[0], l.rest[1] - t[1]) <= Number.parseFloat(filters.precision.value)));
}

export const typeShort = { Stand: "stand", Crouch: "crouch", JumpThrow: "jump", CrouchJumpThrow: "crouch+jump", RunJumpThrow: "run+jump" };
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

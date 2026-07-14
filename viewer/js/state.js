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
  // Live solve progress: { phase, total, candidates, checked: [[x, y, hits],
  // ...], verified: [[x, y, ok], ...] }. Non-null only while a streamed solve
  // is in flight; map2d paints the checked origins and verify verdicts as
  // dots so sweep speed and coverage are visible.
  progress: null,
  heatOn: false,
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
    precision: document.getElementById("f-precision"),
  },
};

// These pure helpers live here (not in a feature module) because map2d,
// view3d, and panel all need them and are not allowed to cross-import.
export function filtered() {
  if (!state.result) {
    return [];
  }
  const filters = state.filters;
  const t = state.result.target;
  return state.result.lineups.filter(l =>
    !l._removed &&
    (!filters.type.value || l.type === filters.type.value) &&
    (!filters.strength.value || Math.abs(l.strength - parseFloat(filters.strength.value)) < 0.01) &&
    (!filters.bounces.value || l.Bounces <= parseInt(filters.bounces.value)) &&
    (!filters.flight.value || l.flightTime <= parseFloat(filters.flight.value)) &&
    (!filters.stability.value || l.stability >= parseFloat(filters.stability.value)) &&
    (!filters.precision.value || Math.hypot(l.rest[0] - t[0], l.rest[1] - t[1]) <= parseFloat(filters.precision.value)));
}

export const typeShort = { Stand: "stand", Crouch: "crouch", JumpThrow: "jump", CrouchJumpThrow: "crouch+jump", RunJumpThrow: "run+jump" };
export const clickShort = s => s >= 0.99 ? "left click" : s >= 0.49 ? "mid (L+R)" : "right click";
export const clickClass = s => s >= 0.99 ? "left" : s >= 0.49 ? "mid" : "right";

// Shared physical/UI constants (M44); world units unless noted.
export const SMOKE_BLOOM_RADIUS = 144;
export const PICK_RADIUS_PX = 12;
export const HEAT_CELL = 24;
export const EYE_HEIGHT = 64; // getpos reports eye position, not feet - subtract this to convert

// Minimal HTML escaper for API-derived strings rendered via innerHTML (L20).
export const esc = s => String(s).replace(/[&<>"']/g,
  c => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", "\"": "&quot;", "'": "&#39;" }[c]));

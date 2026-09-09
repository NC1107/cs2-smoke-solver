// One vocabulary for everything clickable on the map, shared by the 2D radar
// (map2d.js) and the 3D views (view3d.js): what a marker under the pointer
// says about itself, and what a tap on it does. The two renderers hit-test in
// their own geometry (canvas distances, raycasts) and hand the result here, so
// a pin, a spawn and a lineup dot behave the same whichever view you are in.
//
// A hit is { kind, ... } with kind one of:
//   "named"  - a named smoke target pin:      { kind, target }
//   "spawn"  - a T/CT spawn marker:           { kind, origin: [x,y,z], team }
//   "lineup" - a solved lineup's throw spot:  { kind, idx }
//   "ground" - the map itself:                { kind, point: [x,y] or [x,y,z], label? }
// Hit-testing precedence is fixed here too: a named pin is a button and wins
// over anything drawn near it; a spawn beats a lineup dot except while a
// solved list is on screen and no position is being picked (then the answers,
// the lineup dots, come first); the ground is last.
import { state, clickWords, movementWords, clickClass, esc } from "./state.js?v=117";

export const MARKER_PRECEDENCE = ["named", "spawn", "lineup", "ground"];

// Which of two candidate hits should take the tap. While a lineup list is on
// screen and no position is being picked, the dots are the answers and a
// spawn under one is a distraction; every other time the spawn is the point.
export function preferSpawnOverLineup() {
  return state.picking || state.pickingOrigin || !state.target;
}

// The tooltip for a marker, as HTML built only from escaped strings.
export function markerTooltip(hit) {
  switch (hit.kind) {
    case "named": {
      const t = hit.target;
      const name = esc(t.name);
      return t.named
        ? `<b>${name}</b><br>Click to smoke this spot`
        : `<b>near ${name}</b> <span class="cmd2">(provisional name)</span><br>Click to smoke this spot`;
    }
    case "spawn": {
      const what = state.picking ? "your smoke target" : "your throw position";
      return `<b>${esc(hit.team)} spawn</b><br>Click to use it as ${what}`;
    }
    case "lineup": {
      const l = state.result?.lineups?.[hit.idx];
      if (!l) { return ""; }
      return `<b class="${clickClass(l.strength)}">${clickWords(l.strength)}</b> · ${movementWords(l)}` +
        ` · ${l.Bounces} bounce${l.Bounces === 1 ? "" : "s"} · ${l.flightTime.toFixed(1)}s · ${(l.stability * 100).toFixed(0)}%<br>` +
        `${esc(l.how)}<br><span class="cmd2">${esc(l.console)}</span><br>` +
        `<span class="cmd2">rest ${l.rest[0].toFixed(0)}, ${l.rest[1].toFixed(0)} · click marker to pin</span>`;
    }
    default:
      return "";
  }
}

// What a tap on a hit does, in both views:
//   "Set throw position" armed: the tap is the throw spot, whatever it hit,
//   and the arming ends here.
//   A named pin: it becomes the target.
//   A lineup dot: select it.
//   A spawn: throw from it when a target is set, otherwise it becomes the
//   pending origin.
//   The ground: the caller's fallback (set the target, probe a throw spot).
export function resolveTap(hit, callbacks, onGround) {
  if (state.pickingOrigin) {
    const at = hit.kind === "named" ? [...hit.target.pos]
      : hit.kind === "spawn" ? [...hit.origin]
      : hit.kind === "lineup" ? [...state.result.lineups[hit.idx].feet]
      : [...hit.point];
    callbacks.onPickThrowSpot(at);
    return true;
  }
  switch (hit.kind) {
    case "named":
      callbacks.onSetTarget([...hit.target.pos]);
      return true;
    case "lineup":
      callbacks.onSelect(hit.idx);
      return true;
    case "spawn":
      if (state.target) {
        callbacks.onRunQuery({ target: state.target, origin: [hit.origin[0], hit.origin[1]] });
      } else {
        callbacks.onPickOrigin([hit.origin[0], hit.origin[1]], hit.team);
      }
      return true;
    default:
      return onGround ? onGround(hit) : false;
  }
}

// Position the shared tooltip next to the pointer, kept inside the window.
export function showTip(tip, html, clientX, clientY) {
  tip.innerHTML = html;
  tip.style.display = "block";
  // The tip is absolutely positioned inside the stage, so pointer
  // coordinates are taken relative to that box and kept inside it.
  const host = (tip.offsetParent ?? document.body).getBoundingClientRect();
  const pad = 14;
  let x = clientX - host.left + pad;
  let y = clientY - host.top + pad;
  if (x + tip.offsetWidth > host.width - 8) { x = clientX - host.left - tip.offsetWidth - 10; }
  if (y + tip.offsetHeight > host.height - 8) { y = clientY - host.top - tip.offsetHeight - 10; }
  tip.style.left = `${Math.max(8, x)}px`;
  tip.style.top = `${Math.max(8, y)}px`;
}
export function hideTip(tip) {
  tip.style.display = "none";
}

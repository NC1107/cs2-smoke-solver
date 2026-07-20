// The 2D canvas: sizing/dpr, draw/scheduleDraw, radar recolor, pan/zoom/
// hover/pick handlers, tooltip, heatmap, and legend count. Cross-module
// actions (set target, select, run query) go through callbacks that main.js
// registers, so this module never imports the orchestrator.

import { cacheBust } from "./api.js?v=14";
import { isDrag, state, filtered, typeLabel, clickShort, clickClass, esc, SMOKE_BLOOM_RADIUS, PICK_RADIUS_PX, TOUCH_PICK_RADIUS_PX, HEAT_CELL } from "./state.js?v=14";

const canvas = state.canvas;
const ctx = canvas.getContext("2d");
const readout = document.getElementById("readout");

let RX0, RY0, RX1, RY1;
let scale = 1, ox = 0, oy = 0, dpr = 1;

let callbacks = {
  onSetTarget: () => {},
  onSelect: () => {},
  onRunQuery: () => {},
};

export function readColors() {
  const s = getComputedStyle(document.documentElement);
  state.colors = Object.fromEntries(
    ["terrain-lo", "terrain-hi", "accent", "target", "ink", "muted", "surface", "panel",
     "click-left", "click-mid", "click-right", "heat-ok", "heat-none"]
      .map(k => [k, s.getPropertyValue(`--${k}`).trim()]));
}
function hex2rgb(h) {
  const v = h.replace("#", "");
  return [Number.parseInt(v.slice(0, 2), 16), Number.parseInt(v.slice(2, 4), 16), Number.parseInt(v.slice(4, 6), 16)];
}
function lerp(a, b, t) {
  return [0, 1, 2].map(i => Math.round(a[i] + (b[i] - a[i]) * t));
}

// The base map is a radar slice image: R encodes class (0 floor, 128 cover,
// 255 wall), G encodes ground height for a subtle floor tint. Recolor it to
// the active palette once per theme into an offscreen canvas.
let radar = new Image();
const radarCanvas = document.createElement("canvas");
let radarLoadSeq = 0;

// Loads the radar image and caches the map region; rejects on a missing
// image so main.js can show the boot error box and abort. Each call gets a
// fresh Image - the old code mutated one shared Image's onload, so a second
// concurrent load (rapid map switch) overwrote the first's resolver and left
// that promise hanging forever. The sequence guard makes the last-started
// load own the module radar, whichever finishes first.
export async function loadRadar() {
  const img = new Image();
  const mySeq = ++radarLoadSeq;
  const image = state.mapData.image;
  await new Promise((resolve, reject) => {
    img.onload = resolve;
    img.onerror = reject;
    img.src = cacheBust("data/" + image);
  });
  if (mySeq !== radarLoadSeq) { return; } // a newer load has superseded this one
  radar = img;
  radarCanvas.width = radar.width;
  radarCanvas.height = radar.height;
  [RX0, RY0, RX1, RY1] = state.mapData.region;
}

export function recolorRadar() {
  const colors = state.colors;
  // willReadFrequently: recolor does a getImageData readback per theme flip.
  const rc = radarCanvas.getContext("2d", { willReadFrequently: true });
  rc.drawImage(radar, 0, 0);
  const data = rc.getImageData(0, 0, radar.width, radar.height);
  const d = data.data;
  const floorLo = hex2rgb(colors["terrain-lo"]);
  const floorHiRaw = hex2rgb(colors["terrain-hi"]);
  const floorHi = lerp(floorLo, floorHiRaw, 0.22);
  const cover = lerp(floorLo, floorHiRaw, 0.5);
  const wall = floorHiRaw;
  for (let i = 0; i < d.length; i += 4) {
    if (d[i + 3] === 0) {
      continue;
    }
    const cls = d[i];
    const c = cls === 255 ? wall : cls === 128 ? cover : lerp(floorLo, floorHi, d[i + 1] / 255);
    d[i] = c[0]; d[i + 1] = c[1]; d[i + 2] = c[2];
  }
  rc.putImageData(data, 0, 0);
}

function nearestLineup(wx, wy, maxDist) {
  let best = -1, bestD = maxDist;
  for (const l of filtered()) {
    const d = Math.hypot(l.feet[0] - wx, l.feet[1] - wy);
    if (d < bestD) { bestD = d; best = l._idx; }
  }
  return best;
}

export function resetView() {
  const w = canvas.clientWidth, h = canvas.clientHeight;
  scale = Math.min(w / (RX1 - RX0), h / (RY1 - RY0)) * 0.97;
  ox = w / 2 - scale * (RX0 + RX1) / 2;
  oy = h / 2 - scale * (-(RY0 + RY1) / 2);
  draw();
}
const worldOf = (px, py) => [(px - ox) / scale, -((py - oy) / scale)];

// Coalesces bursts of pointer/wheel events into one redraw per frame.
let drawQueued = false;
export function scheduleDraw() {
  if (drawQueued) {
    return;
  }
  drawQueued = true;
  requestAnimationFrame(() => { drawQueued = false; draw(); });
}

export function draw() {
  // The map picker now runs before any map is chosen, so a resize can land here
  // with nothing loaded yet.
  if (!state.mapData) {
    return;
  }
  const colors = state.colors;
  const w = canvas.clientWidth, h = canvas.clientHeight;
  ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
  ctx.fillStyle = colors.surface;
  ctx.fillRect(0, 0, w, h);
  ctx.setTransform(dpr * scale, 0, 0, dpr * scale, dpr * ox, dpr * oy);
  ctx.imageSmoothingEnabled = scale * state.mapData.pixelSize * dpr < 1;
  ctx.drawImage(radarCanvas, RX0, -RY1, RX1 - RX0, RY1 - RY0);
  if (state.prosmokesOn && state.prosmokes) {
    // Landings warm (where pros smoke), throw spots cool (where they throw from).
    drawProHeat(state.prosmokes.lands, "255,120,20", state.proSide);
    drawProHeat(state.prosmokes.throws, "40,150,255", state.proSide);
  }
  if (scale > 0.12) {
    ctx.fillStyle = colors.muted;
    const fh = 10.5 / scale;
    ctx.font = `600 ${fh}px ui-sans-serif, system-ui, sans-serif`;
    ctx.textAlign = "center";
    // Callouts sit at fixed spots and several cluster tight (mirage
    // underpass/backalley, the CT/T spawn corner), so drawing them all merged
    // adjacent labels into unreadable runs. Greedily skip any label whose box
    // overlaps one already placed - zooming in frees the space and reveals it.
    const placed = [];
    for (const [name, x, y] of state.mapData.callouts) {
      const label = String(name).toUpperCase();
      const hw = ctx.measureText(label).width / 2, hh = fh / 2;
      const px = x, py = -y;
      if (placed.some(b => px - hw < b[2] && px + hw > b[0] && py - hh < b[3] && py + hh > b[1])) {
        continue;
      }
      placed.push([px - hw, py - hh, px + hw, py + hh]);
      ctx.fillText(label, px, py);
    }
  }
  if (state.target) {
    // Circle = the landing zone in play: the precision filter radius when
    // set, otherwise the settled smoke bloom radius (visual estimate).
    const zoneRadius = state.filters.precision.value ? Number.parseFloat(state.filters.precision.value) : SMOKE_BLOOM_RADIUS;
    ctx.strokeStyle = colors.target;
    ctx.fillStyle = colors.target;
    ctx.lineWidth = 1.5 / scale;
    ctx.globalAlpha = 0.07;
    ctx.beginPath();
    ctx.arc(state.target[0], -state.target[1], zoneRadius, 0, Math.PI * 2);
    ctx.fill();
    ctx.globalAlpha = 0.85;
    ctx.stroke();
    ctx.globalAlpha = 1;
    ctx.beginPath();
    ctx.arc(state.target[0], -state.target[1], 3 / scale, 0, Math.PI * 2);
    ctx.fill();
  }
  if (state.progress && (state.progress.checked.length || state.progress.verified.length)) {
    // Live sweep: one dot per origin the solver has evaluated so far, blue
    // when at least one throw reached the target, orange when none did. This
    // is the progress indicator - watching it fill shows sweep speed and any
    // standable spots the solver never visited.
    for (const [px, py, , hits] of state.progress.checked) {
      ctx.fillStyle = hits > 0 ? colors["heat-ok"] : colors["heat-none"];
      ctx.globalAlpha = hits > 0 ? 0.55 : 0.3;
      ctx.fillRect(px - HEAT_CELL / 2 + 6, -py - HEAT_CELL / 2 + 6, HEAT_CELL - 12, HEAT_CELL - 12);
    }
    // Verify phase verdicts overlay the sweep dots: candidates the exact sim
    // confirmed grow to a solid block, rejected ones dim to the raw tone the
    // final heatmap will show them in.
    for (const [px, py, , ok] of state.progress.verified) {
      if (ok) {
        ctx.fillStyle = colors["heat-ok"];
        ctx.globalAlpha = 0.9;
        ctx.fillRect(px - HEAT_CELL / 2 + 3, -py - HEAT_CELL / 2 + 3, HEAT_CELL - 6, HEAT_CELL - 6);
      } else {
        ctx.fillStyle = colors.surface || "#000";
        ctx.globalAlpha = 0.55;
        ctx.fillRect(px - HEAT_CELL / 2 + 6, -py - HEAT_CELL / 2 + 6, HEAT_CELL - 12, HEAT_CELL - 12);
      }
    }
    ctx.globalAlpha = 1;
  }
  if (state.heatOn && state.result?.coverage && !state.heatSpots) {
    // Coverage heat map, colorblind-safe (M14): solid blue fill where a
    // verified lineup stands, faint blue fill where only the coarse voxel sim
    // reaches (its candidates failed exact verification), orange outline with
    // no fill where a standable origin was evaluated and nothing reaches it.
    // Outlined cells adjacent to filled regions are candidates for sim gaps.
    ctx.lineWidth = Math.min(1.5 / scale, HEAT_CELL / 6);
    for (const [hx, hy, count, verified] of state.result.coverage) {
      if (count > 0) {
        ctx.fillStyle = colors["heat-ok"];
        ctx.globalAlpha = verified ? 0.55 + 0.4 * Math.min(count, 40) / 40 : 0.18;
        ctx.fillRect(hx - HEAT_CELL / 2, -hy - HEAT_CELL / 2, HEAT_CELL, HEAT_CELL);
      } else {
        ctx.strokeStyle = colors["heat-none"];
        ctx.globalAlpha = 0.7;
        ctx.strokeRect(hx - HEAT_CELL / 2 + 1.5, -hy - HEAT_CELL / 2 + 1.5, HEAT_CELL - 3, HEAT_CELL - 3);
      }
    }
    ctx.globalAlpha = 1;
  }
  if (state.heatOn && state.result?.coverage && state.heatSpots) {
    // Stand-spot quality view: the same evaluated origins, ranked by how
    // reproducible standing there is in a real round. Geometry-pinned spots
    // (walk into the corner/wall and your position error is gone) burn bright;
    // open ground where a verified lineup stands is faint; everything else is
    // nearly invisible so the good spots pop.
    ctx.fillStyle = colors["heat-ok"];
    for (const [hx, hy, count, verified, pin] of state.result.coverage) {
      if (!count) {
        continue;
      }
      ctx.globalAlpha = verified
        ? (pin === 2 ? 0.95 : pin === 1 ? 0.6 : 0.25)
        : 0.07;
      ctx.fillRect(hx - HEAT_CELL / 2, -hy - HEAT_CELL / 2, HEAT_CELL, HEAT_CELL);
    }
    ctx.globalAlpha = 1;
  }
  const shown = state.heatOn ? [] : filtered();
  shown.forEach(l => {
    const idx = l._idx;
    const isSelected = idx === state.selected || idx === state.hovered;
    const clickColor = colors[`click-${clickClass(l.strength)}`] || colors.accent;
    if (isSelected) {
      ctx.strokeStyle = clickColor;
      ctx.lineWidth = 1.5 / scale;
      ctx.beginPath();
      if (l._path) {
        // The grenade's real ground track. Solid, because it is the actual path
        // rather than the "it ends up over there" hint the dashed line was.
        ctx.moveTo(l._path[0][0], -l._path[0][1]);
        for (const p of l._path) {
          ctx.lineTo(p[0], -p[1]);
        }
      } else {
        ctx.setLineDash([8 / scale, 6 / scale]);
        ctx.moveTo(l.feet[0], -l.feet[1]);
        ctx.lineTo(l.rest[0], -l.rest[1]);
      }
      ctx.stroke();
      ctx.setLineDash([]);
      ctx.fillStyle = clickColor;
      ctx.beginPath();
      ctx.arc(l.rest[0], -l.rest[1], 4 / scale, 0, Math.PI * 2);
      ctx.fill();
    }
    // The accuracy ring ("Go to" fetches it): the area the player can stand
    // in and still land within the precision in play.
    if (idx === state.selected && l._slack) {
      ctx.beginPath();
      for (const [deg, r] of l._slack.dirs) {
        const a = deg * Math.PI / 180;
        ctx.lineTo(l.feet[0] + Math.cos(a) * r, -(l.feet[1] + Math.sin(a) * r));
      }
      ctx.closePath();
      ctx.fillStyle = colors["heat-ok"];
      ctx.globalAlpha = 0.15;
      ctx.fill();
      ctx.strokeStyle = colors["heat-ok"];
      ctx.globalAlpha = 0.9;
      ctx.lineWidth = 1.2 / scale;
      ctx.stroke();
      ctx.globalAlpha = 1;
    }
    drawMarker(l, isSelected, clickColor);
  });
  if (state.spawnsOn && state.spawns) {
    drawSpawnSet(state.spawns.t, SPAWN_T_COLOR);
    drawSpawnSet(state.spawns.ct, SPAWN_CT_COLOR);
  }
  document.getElementById("key-count").textContent =
    state.result ? (state.heatOn ? `${state.result.coverage?.length ?? 0} origins` : `${shown.length}/${state.result.lineups.length}`) : "";
}

// Additive-alpha density heat: overlapping points brighten into hotspots. The
// blob radius is in world units so the smoothing tracks the map, not the zoom.
function drawProHeat(pts, rgb, side = "all") {
  const r = 60;
  // Each point's last element is its team (0 = T, 1 = CT); "all" keeps both.
  const wantTeam = side === "t" ? 0 : side === "ct" ? 1 : -1;
  ctx.globalCompositeOperation = "lighter";
  for (const p of pts) {
    if (wantTeam !== -1 && p[p.length - 1] !== wantTeam) {
      continue;
    }
    const x = p[0], y = p[1];
    const g = ctx.createRadialGradient(x, -y, 0, x, -y, r);
    g.addColorStop(0, `rgba(${rgb},0.20)`);
    g.addColorStop(1, `rgba(${rgb},0)`);
    ctx.fillStyle = g;
    ctx.beginPath();
    ctx.arc(x, -y, r, 0, Math.PI * 2);
    ctx.fill();
  }
  ctx.globalCompositeOperation = "source-over";
}

// T gold, CT blue - team colors, distinct from the click-strength marker palette.
const SPAWN_T_COLOR = "#d9a441";
const SPAWN_CT_COLOR = "#4a90d9";

// Spawn points as small diamonds so they never read as lineup dots.
function drawSpawnSet(pts, color) {
  const r = 6 / scale;
  ctx.lineWidth = 1.4 / scale;
  ctx.strokeStyle = state.colors.panel;
  ctx.fillStyle = color;
  for (const [x, y] of pts) {
    const px = x, py = -y;
    ctx.beginPath();
    ctx.moveTo(px, py - r);
    ctx.lineTo(px + r, py);
    ctx.lineTo(px, py + r);
    ctx.lineTo(px - r, py);
    ctx.closePath();
    ctx.fill();
    ctx.stroke();
  }
}

// The shown spawn nearest a click within grab range, or null - so clicking a
// spawn marker solves from that exact spot rather than the approximate pixel.
function nearestSpawn(wx, wy, radius) {
  if (!state.spawnsOn || !state.spawns) {
    return null;
  }
  let best = null, bestD = radius;
  for (const p of [...state.spawns.t, ...state.spawns.ct]) {
    const d = Math.hypot(p[0] - wx, p[1] - wy);
    if (d < bestD) { bestD = d; best = p; }
  }
  return best;
}

// Shape encodes movement (fill = grounded, hollow = jump variant), color
// encodes the mouse buttons.
function drawMarker(l, isSelected, color) {
  const colors = state.colors;
  const x = l.feet[0], y = -l.feet[1];
  const r = (isSelected ? 7 : 4.5) / scale;
  ctx.fillStyle = color;
  ctx.strokeStyle = colors.panel;
  ctx.lineWidth = 1.5 / scale;
  const hollow = l.type === "JumpThrow" || l.type === "CrouchJumpThrow";
  ctx.beginPath();
  if (l.type === "Stand" || l.type === "JumpThrow") {
    ctx.arc(x, y, r, 0, Math.PI * 2);
  } else if (l.type === "Crouch" || l.type === "CrouchJumpThrow") {
    ctx.rect(x - r, y - r, 2 * r, 2 * r);
  } else {
    ctx.moveTo(x, y - r * 1.2);
    ctx.lineTo(x - r * 1.1, y + r * 0.9);
    ctx.lineTo(x + r * 1.1, y + r * 0.9);
    ctx.closePath();
  }
  if (hollow) {
    ctx.strokeStyle = color;
    ctx.lineWidth = 2.2 / scale;
    ctx.stroke();
  } else {
    ctx.fill();
    ctx.stroke();
  }
}

export function resize() {
  dpr = window.devicePixelRatio || 1;
  canvas.width = Math.round(canvas.clientWidth * dpr);
  canvas.height = Math.round(canvas.clientHeight * dpr);
  draw();
}

export function initMap2d(cb) {
  callbacks = cb;

  // Targeting model, shared with the 3D view: the target has its own dedicated
  // input (right-click on desktop, long-press on touch) that works at ANY time,
  // so moving it never collides with selecting a marker or probing an origin.
  // A plain click stays contextual: it bootstraps the first target when none
  // exists (so the intro's "click anywhere" promise is true), then selects
  // markers or probes a throw origin once one does.
  const LONG_PRESS_MS = 450, LONG_PRESS_SLOP_PX = 8;
  let panning = false, lastX = 0, lastY = 0, downX = 0, downY = 0;
  let pressTimer = 0, pressConsumed = false;
  // Two-finger touch: pinch zooms about the finger centroid, centroid drag
  // pans. Without this, touch-action:none hands BOTH fingers to the
  // single-pointer pan above, whose shared lastX/lastY alternates between
  // the two positions and tears the view apart in huge jumps - and there was
  // no way to zoom the radar by touch at all (zoom was wheel-only).
  const touches = new Map();
  let pinching = false, pinchDist = 0, pinchX = 0, pinchY = 0;

  function setTargetAt(clientX, clientY) {
    const rect = canvas.getBoundingClientRect();
    const [wx, wy] = worldOf(clientX - rect.left, clientY - rect.top);
    callbacks.onSetTarget([wx, wy], `target ${wx.toFixed(0)}, ${wy.toFixed(0)}`);
  }
  function cancelLongPress() {
    if (pressTimer) {
      clearTimeout(pressTimer);
      pressTimer = 0;
    }
  }

  canvas.addEventListener("contextmenu", e => e.preventDefault());
  canvas.addEventListener("pointerdown", e => {
    if (e.pointerType === "touch") {
      touches.set(e.pointerId, [e.clientX, e.clientY]);
      canvas.setPointerCapture(e.pointerId);
      if (touches.size === 2) {
        cancelLongPress();
        pinching = true;
        panning = false;
        const [a, b] = [...touches.values()];
        pinchDist = Math.hypot(a[0] - b[0], a[1] - b[1]);
        pinchX = (a[0] + b[0]) / 2;
        pinchY = (a[1] + b[1]) / 2;
        return;
      }
    }
    if (pinching) {
      return;
    }
    panning = true;
    lastX = downX = e.clientX; lastY = downY = e.clientY;
    canvas.classList.add("panning");
    canvas.setPointerCapture(e.pointerId);
    pressConsumed = false;
    if (e.pointerType === "touch" && !state.busy) {
      pressTimer = setTimeout(() => {
        pressTimer = 0;
        pressConsumed = true;
        navigator.vibrate?.(10);
        setTargetAt(downX, downY);
      }, LONG_PRESS_MS);
    }
  });
  canvas.addEventListener("pointerup", e => {
    touches.delete(e.pointerId);
    panning = false;
    canvas.classList.remove("panning");
    cancelLongPress();
    if (pinching) {
      // Neither finger's lift is a click; the gesture ends when both are up.
      if (touches.size === 0) {
        pinching = false;
      }
      return;
    }
    if (pressConsumed || isDrag(downX, downY, e.clientX, e.clientY) || state.busy) {
      return;
    }
    if (e.button === 2) {
      setTargetAt(e.clientX, e.clientY);
      return;
    }
    if (state.picking || !state.target) {
      setTargetAt(e.clientX, e.clientY);
      return;
    }
    if (state.heatOn) {
      return;
    }
    const rect = canvas.getBoundingClientRect();
    const [wx, wy] = worldOf(e.clientX - rect.left, e.clientY - rect.top);
    // Fingers are not mouse pointers: give touch a fatter marker grab zone.
    const pickPx = e.pointerType === "touch" ? TOUCH_PICK_RADIUS_PX : PICK_RADIUS_PX;
    const bestIdx = nearestLineup(wx, wy, pickPx / scale);
    if (bestIdx >= 0) {
      callbacks.onSelect(bestIdx);
      return;
    }
    const spawn = nearestSpawn(wx, wy, pickPx / scale);
    const origin = spawn ? [spawn[0], spawn[1]] : [wx, wy];
    callbacks.onRunQuery({ target: state.target, origin });
  });
  canvas.addEventListener("pointercancel", e => {
    touches.delete(e.pointerId);
    cancelLongPress();
    panning = false;
    canvas.classList.remove("panning");
    if (touches.size === 0) {
      pinching = false;
    }
  });
  canvas.addEventListener("pointermove", e => {
    const rect = canvas.getBoundingClientRect();
    if (e.pointerType === "touch" && touches.has(e.pointerId)) {
      touches.set(e.pointerId, [e.clientX, e.clientY]);
      if (touches.size === 2) {
        const [a, b] = [...touches.values()];
        const dist = Math.hypot(a[0] - b[0], a[1] - b[1]);
        const cx = (a[0] + b[0]) / 2, cy = (a[1] + b[1]) / 2;
        // Same zoom-about-a-point math as the wheel handler, anchored on the
        // finger centroid; centroid drift pans on top.
        const px = cx - rect.left, py = cy - rect.top;
        const next = Math.min(Math.max(scale * (pinchDist > 0 ? dist / pinchDist : 1), 0.03), 12);
        ox = px - (px - ox) * (next / scale);
        oy = py - (py - oy) * (next / scale);
        scale = next;
        ox += cx - pinchX;
        oy += cy - pinchY;
        pinchDist = dist;
        pinchX = cx;
        pinchY = cy;
        scheduleDraw();
        return;
      }
    }
    if (pressTimer && Math.hypot(e.clientX - downX, e.clientY - downY) > LONG_PRESS_SLOP_PX) {
      cancelLongPress();
    }
    if (panning) {
      ox += e.clientX - lastX;
      oy += e.clientY - lastY;
      lastX = e.clientX; lastY = e.clientY;
      scheduleDraw();
    }
    const [wx, wy] = worldOf(e.clientX - rect.left, e.clientY - rect.top);
    readout.textContent = `${wx.toFixed(0)}, ${wy.toFixed(0)}`;

    // Hover details: nearest marker within grab distance shows a tooltip and
    // highlights; the map itself is the lineup list.
    const tip = document.getElementById("tip");
    const best = state.result && !state.heatOn && !panning ? nearestLineup(wx, wy, PICK_RADIUS_PX / scale) : -1;
    if (best !== state.hovered) {
      state.hovered = best;
      scheduleDraw();
    }
    if (best >= 0) {
      const l = state.result.lineups[best];
      tip.innerHTML =
        `<b class="${clickClass(l.strength)}">${clickShort(l.strength)}</b> · ${typeLabel(l)}` +
        ` · ${l.Bounces} bounce${l.Bounces === 1 ? "" : "s"} · ${l.flightTime.toFixed(1)}s · ${(l.stability * 100).toFixed(0)}%<br>` +
        `${esc(l.how)}<br><span class="cmd2">${esc(l.console)}</span><br>` +
        `<span class="cmd2">rest ${l.rest[0].toFixed(0)}, ${l.rest[1].toFixed(0)} · click marker to pin</span>`;
      tip.style.display = "block";
      const stageRect = canvas.parentElement.getBoundingClientRect();
      let tx = e.clientX - stageRect.left + 14;
      let ty = e.clientY - stageRect.top + 14;
      if (tx + tip.offsetWidth > stageRect.width - 8) { tx = e.clientX - stageRect.left - tip.offsetWidth - 10; }
      if (ty + tip.offsetHeight > stageRect.height - 8) { ty = e.clientY - stageRect.top - tip.offsetHeight - 10; }
      tx = Math.max(8, tx);
      ty = Math.max(8, ty);
      tip.style.left = tx + "px";
      tip.style.top = ty + "px";
      canvas.style.cursor = "pointer";
    } else {
      tip.style.display = "none";
      canvas.style.cursor = "";
    }
  });
  canvas.addEventListener("pointerleave", () => {
    readout.textContent = "";
    document.getElementById("tip").style.display = "none";
    if (state.hovered !== -1) { state.hovered = -1; scheduleDraw(); }
  });

  canvas.addEventListener("wheel", e => {
    e.preventDefault();
    const rect = canvas.getBoundingClientRect();
    const px = e.clientX - rect.left, py = e.clientY - rect.top;
    const next = Math.min(Math.max(scale * Math.exp(-e.deltaY * 0.0012), 0.03), 12);
    ox = px - (px - ox) * (next / scale);
    oy = py - (py - oy) * (next / scale);
    scale = next;
    scheduleDraw();
  }, { passive: false });
  canvas.addEventListener("dblclick", resetView);

  new ResizeObserver(resize).observe(canvas);
}

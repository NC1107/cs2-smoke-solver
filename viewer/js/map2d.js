// The 2D canvas: sizing/dpr, draw/scheduleDraw, radar recolor, pan/zoom/
// hover/pick handlers, tooltip, heatmap, and legend count. Cross-module
// actions (set target, select, run query) go through callbacks that main.js
// registers, so this module never imports the orchestrator.

import { state, filtered, typeShort, clickShort, clickClass } from "./state.js";

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
  return [parseInt(v.slice(0, 2), 16), parseInt(v.slice(2, 4), 16), parseInt(v.slice(4, 6), 16)];
}
function lerp(a, b, t) {
  return [0, 1, 2].map(i => Math.round(a[i] + (b[i] - a[i]) * t));
}

// The base map is a radar slice image: R encodes class (0 floor, 128 cover,
// 255 wall), G encodes ground height for a subtle floor tint. Recolor it to
// the active palette once per theme into an offscreen canvas.
const radar = new Image();
const radarCanvas = document.createElement("canvas");

// Loads the radar image and caches the map region; rejects on a missing
// image so main.js can show the boot error box and abort.
export async function loadRadar() {
  await new Promise((resolve, reject) => {
    radar.onload = resolve;
    radar.onerror = reject;
    radar.src = "data/" + state.mapData.image;
  });
  radarCanvas.width = radar.width;
  radarCanvas.height = radar.height;
  [RX0, RY0, RX1, RY1] = state.mapData.region;
}

export function recolorRadar() {
  const colors = state.colors;
  const rc = radarCanvas.getContext("2d");
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

// Nearest visible marker to a world-space point, or -1 if none within maxDist.
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
  const colors = state.colors;
  const w = canvas.clientWidth, h = canvas.clientHeight;
  ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
  ctx.fillStyle = colors.surface;
  ctx.fillRect(0, 0, w, h);
  ctx.setTransform(dpr * scale, 0, 0, dpr * scale, dpr * ox, dpr * oy);
  ctx.imageSmoothingEnabled = scale * state.mapData.pixelSize * dpr < 1;
  ctx.drawImage(radarCanvas, RX0, -RY1, RX1 - RX0, RY1 - RY0);
  if (scale > 0.12) {
    ctx.fillStyle = colors.muted;
    ctx.font = `600 ${10.5 / scale}px ui-sans-serif, system-ui, sans-serif`;
    ctx.textAlign = "center";
    for (const [name, x, y] of state.mapData.callouts) {
      ctx.fillText(String(name).toUpperCase(), x, -y);
    }
  }
  if (state.target) {
    // Circle = the landing zone in play: the precision filter radius when
    // set, otherwise the settled smoke bloom radius (visual estimate).
    const zoneRadius = state.filters.precision.value ? parseFloat(state.filters.precision.value) : 144;
    ctx.strokeStyle = colors.target;
    ctx.fillStyle = colors.target;
    ctx.lineWidth = 2 / scale;
    ctx.globalAlpha = 0.12;
    ctx.beginPath();
    ctx.arc(state.target[0], -state.target[1], zoneRadius, 0, Math.PI * 2);
    ctx.fill();
    ctx.globalAlpha = 1;
    ctx.stroke();
    ctx.beginPath();
    ctx.arc(state.target[0], -state.target[1], 3 / scale, 0, Math.PI * 2);
    ctx.fill();
  }
  if (state.heatOn && state.result && state.result.coverage) {
    // Coverage heat map: green where at least one throw reaches the target,
    // red where a standable origin was evaluated and nothing reaches it.
    // Red cells adjacent to green regions are candidates for sim gaps.
    const CELL = 24;
    for (const [hx, hy, count] of state.result.coverage) {
      ctx.fillStyle = count > 0 ? colors["heat-ok"] : colors["heat-none"];
      ctx.globalAlpha = count > 0 ? 0.25 + 0.5 * Math.min(count, 40) / 40 : 0.45;
      ctx.fillRect(hx - CELL / 2, -hy - CELL / 2, CELL, CELL);
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
      ctx.setLineDash([8 / scale, 6 / scale]);
      ctx.beginPath();
      ctx.moveTo(l.feet[0], -l.feet[1]);
      ctx.lineTo(l.rest[0], -l.rest[1]);
      ctx.stroke();
      ctx.setLineDash([]);
      ctx.fillStyle = clickColor;
      ctx.beginPath();
      ctx.arc(l.rest[0], -l.rest[1], 4 / scale, 0, Math.PI * 2);
      ctx.fill();
    }
    drawMarker(l, isSelected, clickColor);
  });
  document.getElementById("key-count").textContent =
    state.result ? (state.heatOn ? `${state.result.coverage?.length ?? 0} origins` : `${shown.length}/${state.result.lineups.length}`) : "";
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

  let panning = false, lastX = 0, lastY = 0, downX = 0, downY = 0;
  canvas.addEventListener("pointerdown", e => {
    panning = true;
    lastX = downX = e.clientX; lastY = downY = e.clientY;
    canvas.classList.add("panning");
    canvas.setPointerCapture(e.pointerId);
  });
  canvas.addEventListener("pointerup", e => {
    panning = false;
    canvas.classList.remove("panning");
    if (Math.hypot(e.clientX - downX, e.clientY - downY) > 4 || state.busy) {
      return;
    }
    const rect = canvas.getBoundingClientRect();
    const [wx, wy] = worldOf(e.clientX - rect.left, e.clientY - rect.top);
    if (state.picking) {
      callbacks.onSetTarget([wx, wy], `target ${wx.toFixed(0)}, ${wy.toFixed(0)}`);
      return;
    }
    if (!state.target || state.heatOn) {
      return;
    }
    const bestIdx = nearestLineup(wx, wy, 12 / scale);
    if (bestIdx >= 0) {
      callbacks.onSelect(bestIdx);
      return;
    }
    callbacks.onRunQuery({ target: state.target, origin: [wx, wy] });
  });
  canvas.addEventListener("pointermove", e => {
    const rect = canvas.getBoundingClientRect();
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
    const best = state.result && !state.heatOn && !panning ? nearestLineup(wx, wy, 12 / scale) : -1;
    if (best !== state.hovered) {
      state.hovered = best;
      scheduleDraw();
    }
    if (best >= 0) {
      const l = state.result.lineups[best];
      tip.innerHTML =
        `<b class="${clickClass(l.strength)}">${clickShort(l.strength)}</b> · ${typeShort[l.type]}` +
        ` · ${l.Bounces} bounce${l.Bounces === 1 ? "" : "s"} · ${l.flightTime.toFixed(1)}s · ${(l.stability * 100).toFixed(0)}%<br>` +
        `${l.how}<br><span class="cmd2">${l.console}</span><br>` +
        `<span class="cmd2">rest ${l.rest[0].toFixed(0)}, ${l.rest[1].toFixed(0)} · click marker to pin</span>`;
      tip.style.display = "block";
      const stageRect = canvas.parentElement.getBoundingClientRect();
      let tx = e.clientX - stageRect.left + 14;
      let ty = e.clientY - stageRect.top + 14;
      if (tx + tip.offsetWidth > stageRect.width - 8) { tx = e.clientX - stageRect.left - tip.offsetWidth - 10; }
      if (ty + tip.offsetHeight > stageRect.height - 8) { ty = e.clientY - stageRect.top - tip.offsetHeight - 10; }
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
  matchMedia("(prefers-color-scheme: dark)").addEventListener("change", () => { readColors(); recolorRadar(); draw(); });
}

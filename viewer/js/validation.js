// Accuracy dashboard: automated in-game validation runs (real CS2 throws vs
// the solver's predicted landing spot). Deliberately does not import
// js/state.js - that module grabs #map, #status and other main-page elements
// the instant it loads, which do not exist on this page and would throw.

const INDEX_URL = "/data/validation/index.json";
const REPORT_BASE = "/data/validation/";

// Duplicated from js/state.js's esc() rather than imported, for the reason
// above. Every server-derived string that lands in innerHTML goes through
// this first.
function esc(s) {
  return String(s).replace(/[&<>"']/g,
    c => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", "\"": "&quot;", "'": "&#39;" }[c]));
}

const TYPE_LABEL = { Stand: "stand", Crouch: "crouch", JumpThrow: "jump", CrouchJumpThrow: "crouch+jump", RunJumpThrow: "run+jump" };

// Run-direction key behind a running jump throw (server RunDeg: 0 = W, +90 =
// A, -90 = D, +-45 = diagonals). Banded rather than exact-matched so a value
// that has been round-tripped through JSON still labels correctly. Mirrors
// js/state.js's runKeys().
function runKeyFor(deg) {
  return deg > 67.5 ? "A" : deg > 22.5 ? "W+A" : deg < -67.5 ? "D" : deg < -22.5 ? "W+D" : "W";
}

function typeLabelFor(r) {
  return r.Type === "RunJumpThrow"
    ? `${TYPE_LABEL.RunJumpThrow} (${runKeyFor(r.RunDeg ?? 0)})`
    : (TYPE_LABEL[r.Type] ?? r.Type);
}

function clickShort(strength) {
  return strength >= 0.99 ? "left" : strength >= 0.49 ? "mid" : "right";
}

// Errors under 100u carry a decimal (the gap between 1.1u and 1.8u matters);
// triple-digit errors are already far past any lineup being usable, so the
// extra digit is noise.
function fmtErr(n) {
  if (!Number.isFinite(n)) {
    return "-";
  }
  return n >= 100 ? n.toFixed(0) : n.toFixed(1);
}

function fmtPct(fraction) {
  return Number.isFinite(fraction) ? `${(fraction * 100).toFixed(0)}%` : "-";
}

function fmtLocal(iso) {
  const d = new Date(iso);
  return Number.isNaN(d.getTime()) ? String(iso) : d.toLocaleString(undefined, { dateStyle: "medium", timeStyle: "short" });
}

function fmtDateOnly(iso) {
  const d = new Date(iso);
  return Number.isNaN(d.getTime()) ? String(iso) : d.toLocaleDateString(undefined, { dateStyle: "medium" });
}

function fmtMonthDay(iso) {
  const d = new Date(iso);
  return Number.isNaN(d.getTime()) ? String(iso) : d.toLocaleDateString(undefined, { month: "short", day: "numeric" });
}

function fmtVec0(v) {
  return `(${v.map(n => n.toFixed(0)).join(", ")})`;
}

// Linear-interpolation percentile over an ascending-sorted array; matches
// the usual definition closely enough for a dashboard and needs no library.
function percentile(sortedAsc, p) {
  if (!sortedAsc.length) {
    return 0;
  }
  const idx = (p / 100) * (sortedAsc.length - 1);
  const lo = Math.floor(idx);
  const hi = Math.ceil(idx);
  return lo === hi ? sortedAsc[lo] : sortedAsc[lo] + (sortedAsc[hi] - sortedAsc[lo]) * (idx - lo);
}

// The very first rig build serialized result rows in camelCase; every later
// build uses PascalCase and every renderer here assumes it. Uppercasing the
// first letter once at load time is cheaper than teaching each renderer two
// spellings.
function normalizeRows(rows) {
  if (!rows.length || "ErrPredicted" in rows[0] || !("errPredicted" in rows[0])) {
    return rows;
  }
  return rows.map(r => Object.fromEntries(
    Object.entries(r).map(([k, v]) => [k.charAt(0).toUpperCase() + k.slice(1), v])));
}

// Shareable link into the main viewer for one throw. Mirrors main.js's
// permalink(): t is the resolved target, l identifies the lineup by its
// physical parameters (type:strength:fx:fy:fz:pitch:yaw[:runDeg]), with
// runDeg appended only when set so the link shape matches what the solver
// itself produces.
function openLinkFor(map, target, r) {
  const p = new URLSearchParams({ map });
  p.set("t", target.map(v => v.toFixed(1)).join(","));
  const parts = [r.Type, r.Strength, ...r.Feet.map(v => v.toFixed(1)), r.Pitch.toFixed(2), r.Yaw.toFixed(2)];
  const runDeg = r.RunDeg ?? 0;
  if (runDeg) {
    parts.push(runDeg.toFixed(0));
  }
  p.set("l", parts.join(":"));
  return `/?${p}`;
}

// Every segment is one independent slice of the full result set (all Stand
// throws, or all bounces-0-4 throws, or all stability-100% throws) - not a
// cross product of the three dimensions. Matches the rig's own report.md.
const SEGMENT_DEFS = [
  ["Stand", r => r.Type === "Stand"],
  ["Crouch", r => r.Type === "Crouch"],
  ["JumpThrow", r => r.Type === "JumpThrow"],
  ["CrouchJumpThrow", r => r.Type === "CrouchJumpThrow"],
  ["RunJumpThrow", r => r.Type === "RunJumpThrow"],
  ["bounces 0-4", r => r.PredictedBounces <= 4],
  ["bounces 5-30", r => r.PredictedBounces > 4 && r.PredictedBounces <= 30],
  ["bounces >30", r => r.PredictedBounces > 30],
  ["stability 100%", r => r.Stability >= 1],
  ["stability <100%", r => r.Stability < 1],
];

const DIVERGENCE_CLASSES = ["MISSED-BOUNCE", "PHANTOM-BOUNCE", "BOUNCE-MISMATCH", "DRIFT", "REST-MISMATCH", "TRACKED"];

const CLASS_COLOR = {
  TRACKED: "var(--muted)",
  DRIFT: "var(--accent)",
  "MISSED-BOUNCE": "var(--heat-none)",
  "PHANTOM-BOUNCE": "var(--click-mid)",
  "BOUNCE-MISMATCH": "var(--click-right)",
  "REST-MISMATCH": "var(--target)",
};

// Per-map line colors, cycling if more maps than colors ever show up.
const PALETTE = ["var(--accent)", "var(--heat-ok)", "var(--heat-none)", "var(--click-left)", "var(--click-mid)", "var(--click-right)"];

const state = {
  runs: [], // index.json entries, newest first
};

function runLabel(run) {
  return `${run.map}${run.name ? " - " + run.name : ""} - ${fmtLocal(run.timestamp)}`;
}

// Batch label when the rig tagged one; otherwise the calendar date the run
// happened on, so unrelated one-off runs on the same day still land together.
function groupKeyFor(run) {
  return run.batch ? `batch:${run.batch}` : `date:${fmtDateOnly(run.timestamp)}`;
}
function groupLabelFor(run) {
  return run.batch || fmtDateOnly(run.timestamp);
}

function within8Fraction(summary) {
  return summary?.matched ? summary.within8 / summary.matched : 0;
}

// ---- SVG chart helpers ----------------------------------------------------
// All charts share the same construction: a viewBox-only <svg> that scales
// with its container, axis lines in --line, tick text in --muted (via the
// .acc-chart CSS rule), and log scales whose endpoints sit on the same
// power-of-two chain that produces the tick labels.

const OT = { W: 700, H: 190, L: 38, R: 12, T: 12, B: 24 }; // over-time charts
const RC = { W: 460, H: 220, L: 40, R: 12, T: 14, B: 26 }; // per-run charts

// Halve lo below the base tick until it covers the data, then double up to
// hi: both ends land on the tick chain, so scale and gridlines agree.
function logDomain(dataLo, dataHi, baseTick) {
  let lo = baseTick;
  while (lo > dataLo) {
    lo /= 2;
  }
  let hi = lo * 2;
  while (hi < dataHi) {
    hi *= 2;
  }
  return [lo, hi];
}

function doublingTicks(lo, hi) {
  const out = [];
  for (let t = lo; t <= hi * 1.0001; t *= 2) {
    out.push(t);
  }
  return out;
}

// Keep every other tick until the labels stop crowding; filtering from the
// bottom anchor preserves the small round values readers scan for.
function thinTicks(ticks, maxCount) {
  let t = ticks;
  while (t.length > maxCount) {
    t = t.filter((_, i) => i % 2 === 0);
  }
  return t;
}

function fmtTickNum(v) {
  return v >= 1 ? String(Math.round(v)) : String(v);
}

function makeLogY(g, lo, hi) {
  const h = g.H - g.T - g.B;
  const ll = Math.log(lo);
  const span = Math.log(hi) - ll;
  return v => g.T + h - ((Math.log(Math.max(v, lo)) - ll) / span) * h;
}

function makeLinY(g, lo, hi) {
  const h = g.H - g.T - g.B;
  return v => g.T + h - ((v - lo) / (hi - lo)) * h;
}

function makeLogX(g, lo, hi) {
  const w = g.W - g.L - g.R;
  const ll = Math.log(lo);
  const span = Math.log(hi) - ll;
  return v => g.L + ((Math.log(Math.min(Math.max(v, lo), hi)) - ll) / span) * w;
}

function makeIndexX(g, n) {
  const w = g.W - g.L - g.R;
  return i => n <= 1 ? g.L + w / 2 : g.L + (i / (n - 1)) * w;
}

function axisLines(g) {
  const yBot = g.H - g.B;
  return `<line x1="${g.L}" y1="${g.T}" x2="${g.L}" y2="${yBot}" stroke="var(--line)"/>` +
    `<line x1="${g.L}" y1="${yBot}" x2="${g.W - g.R}" y2="${yBot}" stroke="var(--line)"/>`;
}

function yTicksSvg(g, yFor, ticks, fmt) {
  return ticks.map(t => {
    const y = yFor(t);
    return `<line x1="${g.L - 4}" y1="${y.toFixed(1)}" x2="${g.L}" y2="${y.toFixed(1)}" stroke="var(--line)"/>` +
      `<text x="${g.L - 7}" y="${(y + 3).toFixed(1)}" text-anchor="end">${esc(fmt(t))}</text>`;
  }).join("");
}

function xTicksSvg(g, ticks) {
  const y = g.H - g.B;
  return ticks.map(t =>
    `<line x1="${t.x.toFixed(1)}" y1="${y}" x2="${t.x.toFixed(1)}" y2="${y + 4}" stroke="var(--line)"/>` +
    `<text x="${t.x.toFixed(1)}" y="${y + 14}" text-anchor="${t.anchor ?? "middle"}">${esc(t.label)}</text>`).join("");
}

function hRefLine(g, y, label) {
  return `<line x1="${g.L}" y1="${y.toFixed(1)}" x2="${g.W - g.R}" y2="${y.toFixed(1)}" stroke="var(--muted)" stroke-dasharray="4 3" opacity="0.55"/>` +
    `<text x="${g.W - g.R - 2}" y="${(y - 3).toFixed(1)}" text-anchor="end">${esc(label)}</text>`;
}

// row staggers adjacent labels vertically: 1u and 2u sit close together in
// log space, and their labels would overlap on wide-range axes otherwise.
function vRefLine(g, x, label, row) {
  return `<line x1="${x.toFixed(1)}" y1="${g.T}" x2="${x.toFixed(1)}" y2="${g.H - g.B}" stroke="var(--muted)" stroke-dasharray="4 3" opacity="0.55"/>` +
    `<text x="${(x + 3).toFixed(1)}" y="${g.T + 9 + (row ?? 0) * 11}">${esc(label)}</text>`;
}

function chartShell(g, inner, aria) {
  return `<svg viewBox="0 0 ${g.W} ${g.H}" class="acc-chart" role="img" aria-label="${esc(aria)}">${inner}</svg>`;
}

function chartBlock(title, hint, svg, legend) {
  return `<div class="chart-block"><div class="chart-title">${esc(title)}` +
    (hint ? ` <span class="muted">(${esc(hint)})</span>` : "") + `</div>${svg}${legend ?? ""}</div>`;
}

function legendChips(items) {
  return `<div class="chart-legend">` +
    items.map(([label, color]) => `<span><i style="background:${color}"></i>${esc(label)}</span>`).join("") +
    `</div>`;
}

// Split a point series at nulls so a run whose summary lacks a field leaves
// a visible gap in the line instead of plotting as zero.
function gapSegments(points) {
  const segs = [];
  let cur = [];
  for (const p of points) {
    if (p) {
      cur.push(p);
    } else if (cur.length) {
      segs.push(cur);
      cur = [];
    }
  }
  if (cur.length) {
    segs.push(cur);
  }
  return segs;
}

function polyline(pts, color) {
  return `<polyline points="${pts.map(p => `${p.x.toFixed(1)},${p.y.toFixed(1)}`).join(" ")}" fill="none" stroke="${color}" stroke-width="1.5"/>`;
}

function dotsWithTitles(pts, color) {
  return pts.map(p =>
    `<circle cx="${p.x.toFixed(1)}" cy="${p.y.toFixed(1)}" r="3" fill="${color}">` +
    `<title>${esc(p.title)}</title></circle>`).join("");
}

// ---- over-time charts -------------------------------------------------------

// Runs are evenly spaced by index rather than by wall-clock time: 25 of the
// current 30 runs landed inside two nights, and a true time axis would fuse
// them into one unreadable blob.
function timeTicks(runs, xFor) {
  const n = runs.length;
  const lastX = xFor(n - 1);
  const ticks = [{ x: xFor(0), label: fmtMonthDay(runs[0].timestamp), anchor: "start" }];
  let prevX = ticks[0].x;
  for (let i = 1; i < n - 1; i++) {
    const label = fmtMonthDay(runs[i].timestamp);
    if (label === fmtMonthDay(runs[i - 1].timestamp)) {
      continue;
    }
    const x = xFor(i);
    // 60px clears a "Jul 10" label at this font size on both sides.
    if (x - prevX >= 60 && lastX - x >= 60) {
      ticks.push({ x, label });
      prevX = x;
    }
  }
  ticks.push({ x: lastX, label: fmtMonthDay(runs[n - 1].timestamp), anchor: "end" });
  return ticks;
}

function chartMedianByMap(runs, xFor, xTicks) {
  const medians = runs.map(r => r.summary?.errMedian);
  const finite = medians.filter(Number.isFinite);
  if (!finite.length) {
    return "";
  }
  // The 1u/2u quality bars must always render, so the domain includes them
  // even when every run sits outside that band.
  const [lo, hi] = logDomain(Math.min(...finite, 1), Math.max(...finite, 2), 0.5);
  const yFor = makeLogY(OT, lo, hi);
  const yTicks = thinTicks(doublingTicks(lo, hi), 8);
  const maps = [...new Set(runs.map(r => r.map))];
  const colorFor = m => PALETTE[maps.indexOf(m) % PALETTE.length];
  let body = "";
  for (const m of maps) {
    const pts = [];
    runs.forEach((r, i) => {
      if (r.map === m && Number.isFinite(medians[i])) {
        pts.push({ x: xFor(i), y: yFor(medians[i]), title: `${runLabel(r)} - median ${fmtErr(medians[i])}u` });
      }
    });
    body += polyline(pts, colorFor(m)) + dotsWithTitles(pts, colorFor(m));
  }
  const refs = [1, 2].map(v => hRefLine(OT, yFor(v), `${v}u`)).join("");
  const svg = chartShell(OT,
    axisLines(OT) + yTicksSvg(OT, yFor, yTicks, fmtTickNum) + xTicksSvg(OT, xTicks) + refs + body,
    "Median predicted-vs-real error per run, one line per map, log scale");
  return chartBlock("median error by map", "log scale, u",
    svg, legendChips(maps.map(m => [m, colorFor(m)])));
}

function chartWithinShare(runs, xFor, xTicks) {
  const series = [
    { label: "within 2u", color: "var(--accent)", of: s => s?.matched && Number.isFinite(s.within2) ? (s.within2 / s.matched) * 100 : null },
    { label: "within 8u", color: "var(--heat-ok)", of: s => s?.matched && Number.isFinite(s.within8) ? (s.within8 / s.matched) * 100 : null },
  ];
  const yFor = makeLinY(OT, 0, 100);
  let body = "";
  for (const ser of series) {
    const pts = runs.map((r, i) => {
      const v = ser.of(r.summary);
      return v === null ? null : { x: xFor(i), y: yFor(v), title: `${runLabel(r)} - ${v.toFixed(0)}% ${ser.label}` };
    });
    for (const seg of gapSegments(pts)) {
      body += polyline(seg, ser.color);
    }
    body += dotsWithTitles(pts.filter(Boolean), ser.color);
  }
  const svg = chartShell(OT,
    axisLines(OT) + yTicksSvg(OT, yFor, [0, 25, 50, 75, 100], v => `${v}%`) + xTicksSvg(OT, xTicks) + body,
    "Share of throws landing within 2 and 8 units per run");
  return chartBlock("share of throws within the bar", null,
    svg, legendChips(series.map(s => [s.label, s.color])));
}

function renderOverTime(runsNewestFirst) {
  const wrap = document.getElementById("overtime-charts");
  if (runsNewestFirst.length < 2) {
    wrap.innerHTML = `<p class="muted">need at least two runs to plot a trend.</p>`;
    return;
  }
  const runs = runsNewestFirst.slice().reverse();
  const xFor = makeIndexX(OT, runs.length);
  const xTicks = timeTicks(runs, xFor);
  wrap.innerHTML = chartMedianByMap(runs, xFor, xTicks) + chartWithinShare(runs, xFor, xTicks);
}

// ---- per-run charts ---------------------------------------------------------

function chartCdf(results) {
  const errs = results.map(r => r.ErrPredicted).filter(Number.isFinite).sort((a, b) => a - b);
  if (!errs.length) {
    return "";
  }
  const n = errs.length;
  const xLo = 0.2;
  let xHi = 0.5;
  while (xHi < Math.max(16, errs[n - 1])) {
    xHi *= 2;
  }
  const xFor = makeLogX(RC, xLo, xHi);
  const yFor = makeLinY(RC, 0, 100);
  const xTicks = thinTicks(doublingTicks(0.5, xHi), 9)
    .map(v => ({ x: xFor(v), label: fmtTickNum(v) }));
  const pctBelow = v => (errs.filter(e => e <= v).length / n) * 100;
  // Anchored at the left edge so the curve rises from the axis instead of
  // materializing mid-plot.
  const pts = [{ x: xFor(xLo), y: yFor(pctBelow(xLo)) }]
    .concat(errs.map((e, i) => ({ x: xFor(e), y: yFor(((i + 1) / n) * 100) })));
  const refs = [1, 2, 8].map((v, k) =>
    vRefLine(RC, xFor(v), `${v}u - ${pctBelow(v).toFixed(0)}%`, k % 2)).join("");
  const svg = chartShell(RC,
    axisLines(RC) + yTicksSvg(RC, yFor, [0, 25, 50, 75, 100], v => `${v}%`) + xTicksSvg(RC, xTicks) +
    refs + polyline(pts, "var(--accent)"),
    "Cumulative share of throws by predicted-vs-real error, log x axis");
  return chartBlock("error CDF", "% of throws with error at most x; log x, u", svg);
}

function chartScatter(report) {
  const rows = (report.results ?? []).filter(r =>
    Number.isFinite(r.ErrPredicted) && Number.isFinite(r.PredictedBounces));
  if (!rows.length) {
    return "";
  }
  const target = report.target ?? [0, 0, 0];
  const errFloor = 0.1;
  const maxB = Math.max(...rows.map(r => r.PredictedBounces), 1);
  const step = maxB > 40 ? 10 : maxB > 16 ? 5 : maxB > 8 ? 2 : 1;
  const xMax = Math.ceil(maxB / step) * step;
  const plotW = RC.W - RC.L - RC.R;
  const xFor = v => RC.L + (v / xMax) * plotW;
  let yHi = 0.5;
  while (yHi < Math.max(...rows.map(r => r.ErrPredicted), 1)) {
    yHi *= 2;
  }
  const yFor = makeLogY(RC, errFloor, yHi);
  const yTicks = thinTicks([errFloor, ...doublingTicks(0.5, yHi)], 9);
  const xTicks = [];
  for (let b = 0; b <= xMax; b += step) {
    xTicks.push({ x: xFor(b), label: String(b) });
  }
  const classOf = r => r.DivergenceClass ?? "unclassified";
  const colorOf = cls => CLASS_COLOR[cls] ?? "var(--muted)";
  const dots = rows.map((r, i) => {
    const cls = classOf(r);
    // Small deterministic horizontal jitter: bounce counts are integers, so
    // without it hundreds of dots stack on identical x positions.
    const jitter = (((r.Index ?? i) % 9) - 4) * 0.05;
    const bx = Math.min(xMax, Math.max(0, r.PredictedBounces + jitter));
    const title = `${typeLabelFor(r)} (${clickShort(r.Strength)}) - ` +
      `${r.PredictedBounces}${Number.isFinite(r.RealBounces) ? "/" + r.RealBounces : ""}b - ` +
      `${fmtErr(r.ErrPredicted)}u - ${cls}`;
    return `<a href="${esc(openLinkFor(report.map, target, r))}" target="_blank" rel="noopener">` +
      `<circle cx="${xFor(bx).toFixed(1)}" cy="${yFor(Math.max(r.ErrPredicted, errFloor)).toFixed(1)}" r="3" fill="${colorOf(cls)}" fill-opacity="0.75">` +
      `<title>${esc(title)}</title></circle></a>`;
  }).join("");
  const refs = [2, 8].map(v => hRefLine(RC, yFor(v), `${v}u`)).join("");
  const present = [...DIVERGENCE_CLASSES, "unclassified"].filter(cls => rows.some(r => classOf(r) === cls));
  const svg = chartShell(RC,
    axisLines(RC) + yTicksSvg(RC, yFor, yTicks, fmtTickNum) + xTicksSvg(RC, xTicks) + refs + dots,
    "Predicted bounces versus predicted-vs-real error, log y axis, one dot per throw");
  return chartBlock("bounces vs error", "log y, u; click a dot to open the lineup",
    svg, legendChips(present.map(cls => [cls, colorOf(cls)])));
}

function renderRunCharts(report) {
  const wrap = document.getElementById("run-charts");
  const html = chartCdf(report.results ?? []) + chartScatter(report);
  wrap.innerHTML = html || `<p class="muted">no results in this report</p>`;
}

// ---- run picker and report sections -----------------------------------------

function runEntryEl(run) {
  const btn = document.createElement("button");
  btn.type = "button";
  btn.className = "run-entry";
  btn.dataset.file = run.file;
  const nameBit = run.name ? ` <span class="run-name">${esc(run.name)}</span>` : "";
  btn.innerHTML =
    `<div class="run-entry-top"><b>${esc(run.map)}</b>${nameBit}</div>` +
    `<div class="run-entry-meta">${esc(fmtLocal(run.timestamp))}</div>` +
    `<div class="run-entry-stats">` +
    `<span>${run.summary?.lineups ?? "?"} throws</span>` +
    `<span>med ${esc(fmtErr(run.summary?.errMedian ?? NaN))}u</span>` +
    `<span>${esc(fmtPct(within8Fraction(run.summary)))} &le;8u</span>` +
    `</div>`;
  btn.addEventListener("click", () => selectRun(run.file));
  return btn;
}

function markSelected(file) {
  for (const el of document.querySelectorAll(".run-entry")) {
    el.classList.toggle("selected", el.dataset.file === file);
  }
}

function renderRunList(runs) {
  const wrap = document.getElementById("run-list");
  wrap.innerHTML = "";
  document.getElementById("run-count").textContent = `(${runs.length})`;
  let lastKey = null;
  for (const run of runs) {
    const key = groupKeyFor(run);
    if (key !== lastKey) {
      const h = document.createElement("div");
      h.className = "run-group-label";
      h.textContent = groupLabelFor(run);
      wrap.appendChild(h);
      lastKey = key;
    }
    wrap.appendChild(runEntryEl(run));
  }
}

function renderEmptyIndex(message) {
  document.getElementById("run-count").textContent = "";
  document.getElementById("run-list").innerHTML =
    `<p class="muted">No validation runs yet. These are produced by the rig's accuracy pipeline: it throws every lineup the solver finds on a real CS2 server and compares the landing spot against the prediction. Check back once it has run.</p>`;
  document.getElementById("report-title").textContent = "";
  clearReportSections();
  document.getElementById("overtime-charts").innerHTML = "";
  if (message) {
    const err = document.getElementById("report-error");
    err.textContent = message;
    err.hidden = false;
  }
}

function clearReportSections() {
  document.getElementById("summary-cards").innerHTML = "";
  document.getElementById("run-charts").innerHTML = "";
  document.getElementById("segments-body").innerHTML = "";
  document.getElementById("divergence-chips").innerHTML = "";
  document.getElementById("worst-body").innerHTML = "";
}

function reportErrorLine(message) {
  const err = document.getElementById("report-error");
  err.textContent = message;
  err.hidden = false;
}

async function selectRun(file) {
  markSelected(file);
  const err = document.getElementById("report-error");
  err.hidden = true;
  err.textContent = "";
  try {
    const res = await fetch(REPORT_BASE + file, { cache: "no-cache" });
    if (!res.ok) {
      throw new Error(`HTTP ${res.status}`);
    }
    const report = await res.json();
    report.results = normalizeRows(report.results ?? []);
    renderReport(file, report);
  } catch (e) {
    clearReportSections();
    document.getElementById("report-title").textContent = "";
    reportErrorLine(`could not load ${file}: ${e.message}`);
  }
}

function renderReport(file, report) {
  document.getElementById("report-title").textContent =
    `${report.map ?? file}${report.name ? " - " + report.name : ""} - ${fmtLocal(report.timestamp)}`;
  renderSummaryCards(report.summary ?? {}, report.results);
  renderRunCharts(report);
  renderSegments(report.results);
  renderDivergence(report.results);
  renderWorst(report);
}

function renderSummaryCards(s, results) {
  const within8Pct = within8Fraction(s);
  const withinPassPct = s.matched ? s.withinPass / s.matched : 0;
  // Recomputed from the rows rather than read from the summary: the field
  // was backfilled recently, and the rows are the source of truth anyway.
  const within2Pct = results.length ? results.filter(r => r.ErrPredicted <= 2).length / results.length : 0;
  const cards = [
    ["median error", `${fmtErr(s.errMedian)}u`, ""],
    ["p90 error", `${fmtErr(s.errP90)}u`, ""],
    ["max error", `${fmtErr(s.errMax)}u`, ""],
    ["within 8u", fmtPct(within8Pct), within8Pct >= 0.85 ? "good" : within8Pct < 0.7 ? "bad" : ""],
    ["within 2u", fmtPct(within2Pct), within2Pct >= 0.8 ? "good" : within2Pct < 0.5 ? "bad" : ""],
    [`within ${s.passRadius ?? "?"}u`, fmtPct(withinPassPct), ""],
    ["failed to detonate", `${s.notDetonated ?? 0}`, ""],
    ["matched / submitted", `${s.matched ?? 0} / ${s.submitted ?? 0}`, ""],
  ];
  document.getElementById("summary-cards").innerHTML = cards.map(([label, value, cls]) =>
    `<div class="metric-card"><div class="metric-label">${esc(label)}</div>` +
    `<div class="metric-value${cls ? " " + cls : ""}">${esc(value)}</div></div>`).join("");
}

function renderSegments(results) {
  const rows = [];
  for (const [label, pred] of SEGMENT_DEFS) {
    const errs = results.filter(pred).map(r => r.ErrPredicted).sort((a, b) => a - b);
    if (!errs.length) {
      continue;
    }
    const within8 = errs.filter(e => e <= 8).length / errs.length;
    rows.push(`<tr><td>${esc(label)}</td><td>${errs.length}</td>` +
      `<td>${esc(fmtErr(percentile(errs, 50)))}u</td>` +
      `<td>${esc(fmtErr(percentile(errs, 90)))}u</td>` +
      `<td>${esc(fmtErr(errs[errs.length - 1]))}u</td>` +
      `<td>${esc(fmtPct(within8))}</td></tr>`);
  }
  document.getElementById("segments-body").innerHTML = rows.length
    ? rows.join("")
    : `<tr><td colspan="6" class="muted">no results in this report</td></tr>`;
}

function renderDivergence(results) {
  const misses = results.filter(r => r.ErrPredicted > 8);
  const counts = {};
  for (const r of misses) {
    if (r.DivergenceClass) {
      counts[r.DivergenceClass] = (counts[r.DivergenceClass] ?? 0) + 1;
    }
  }
  const chips = DIVERGENCE_CLASSES.filter(cls => counts[cls])
    .map(cls => `<span class="chip">${esc(cls)} <b>${counts[cls]}</b></span>`);
  const note = !misses.length ? `<span class="muted">no misses over 8u</span>`
    : !chips.length ? `<span class="muted">this report predates divergence classification</span>`
    : "";
  document.getElementById("divergence-chips").innerHTML = chips.join("") + note;
}

function renderWorst(report) {
  const target = report.target ?? [0, 0, 0];
  const rows = (report.results ?? []).slice()
    .sort((a, b) => b.ErrPredicted - a.ErrPredicted)
    .slice(0, 25);
  const body = document.getElementById("worst-body");
  if (!rows.length) {
    body.innerHTML = `<tr><td colspan="9" class="muted">no results in this report</td></tr>`;
    return;
  }
  body.innerHTML = rows.map(r => {
    const click = clickShort(r.Strength);
    return `<tr>` +
      `<td>${r.ErrPredicted.toFixed(0)}u</td>` +
      `<td>${r.DivergenceClass ? esc(`${r.DivergenceClass}@${r.DivergenceTick ?? "?"}`) : "-"}</td>` +
      `<td>${esc(typeLabelFor(r))}</td>` +
      `<td class="acc-click-${click}">${click}</td>` +
      `<td>${r.PredictedBounces}/${Number.isFinite(r.RealBounces) ? r.RealBounces : "-"}</td>` +
      `<td>${Number.isFinite(r.Stability) ? (r.Stability * 100).toFixed(0) + "%" : "-"}</td>` +
      `<td>${esc(fmtVec0(r.PredictedRest))}</td>` +
      `<td>${esc(fmtVec0(r.RealRest))}</td>` +
      `<td><a class="btn" target="_blank" rel="noopener" href="${esc(openLinkFor(report.map, target, r))}">open</a></td>` +
      `</tr>`;
  }).join("");
}

async function loadIndex() {
  try {
    const res = await fetch(INDEX_URL, { cache: "no-cache" });
    if (!res.ok) {
      renderEmptyIndex();
      return;
    }
    const data = await res.json();
    const runs = (data.runs ?? []).slice().sort((a, b) => new Date(b.timestamp) - new Date(a.timestamp));
    state.runs = runs;
    if (!runs.length) {
      renderEmptyIndex();
      return;
    }
    renderRunList(runs);
    renderOverTime(runs);
    await selectRun(runs[0].file);
  } catch (e) {
    renderEmptyIndex(`could not load validation index: ${e.message}`);
  }
}

loadIndex();

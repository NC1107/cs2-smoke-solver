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
  filterMap: null, // sidebar shows only this map's runs when set
  report: null, // the report currently on screen
  showOrigins: false, // landing map also draws where each throw stood
};

// A report is only comparable to its mesh when the game server that threw
// it ran the same build the mesh was extracted from. Reports before
// 2026-09-04 never recorded the server build, so they are "unknown", not
// wrong: the rig was on a stale server for seven weeks and nothing flagged
// it, which is why this is now visible on every run.
function buildStatus(run) {
  if (!run?.build || !run?.serverBuild) {
    return "unknown";
  }
  return run.build === run.serverBuild ? "match" : "mismatch";
}
const BUILD_LABEL = { match: "builds match", mismatch: "server and mesh builds differ", unknown: "server build not recorded" };

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

// The within-N counters (within1/2/8, withinPass) count base throws only, so
// the denominator is the base-throw count `graded`, never `matched` - matched
// includes the perturbation probes and would understate every rate ~5x. Old
// reports predate `graded` but also predate perturbation, so matched == graded
// there and the fallback is exact.
function gradedCount(summary) {
  return summary?.graded ?? summary?.matched ?? 0;
}
function withinFraction(summary, key) {
  const n = gradedCount(summary);
  return n && Number.isFinite(summary?.[key]) ? summary[key] / n : null;
}
function within8Fraction(summary) {
  return withinFraction(summary, "within8") ?? 0;
}
// The bar the loop is judged on. Older reports graded against a different
// radius, so their withinPass count is not a 3u figure and reads as unknown.
function within3Fraction(summary) {
  return summary?.passRadius === 3 ? withinFraction(summary, "withinPass") : null;
}
const ERR_BAND = { ok: "var(--heat-ok)", warn: "var(--target)", bad: "var(--heat-none)" };
function errBand(e) {
  return e <= 3 ? "ok" : e <= 8 ? "warn" : "bad";
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
  const points = pts.map(p => `${p.x.toFixed(1)},${p.y.toFixed(1)}`).join(" ");
  return `<polyline points="${points}" fill="none" stroke="${color}" stroke-width="1.5"/>`;
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
    { label: "within 2u", color: "var(--accent)", of: s => { const f = withinFraction(s, "within2"); return f === null ? null : f * 100; } },
    { label: "within 8u", color: "var(--heat-ok)", of: s => { const f = withinFraction(s, "within8"); return f === null ? null : f * 100; } },
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
  wrap.innerHTML = chartMissesByBatch(runsNewestFirst) + chartMedianByMap(runs, xFor, xTicks) + chartWithinShare(runs, xFor, xTicks);
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
  const html = chartCdf(report.results ?? []) + chartScatter(report) + chartMissByBounces(report.results ?? []);
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
    `<span>med ${esc(fmtErr(run.summary?.errMedian ?? Number.NaN))}u</span>` +
    `<span>${esc(fmtPct(within3Fraction(run.summary)))} &le;3u</span>` +
    `<span>${esc(fmtPct(within8Fraction(run.summary)))} &le;8u</span>` +
    `</div>`;
  const status = buildStatus(run);
  if (status !== "match") {
    btn.classList.add(`build-${status}`);
    btn.title = BUILD_LABEL[status];
  }
  btn.addEventListener("click", () => selectRun(run.file));
  return btn;
}

function markSelected(file) {
  for (const el of document.querySelectorAll(".run-entry")) {
    el.classList.toggle("selected", el.dataset.file === file);
  }
}

function renderRunFilter() {
  const el = document.getElementById("run-filter");
  el.hidden = !state.filterMap;
  el.innerHTML = state.filterMap
    ? `<span>showing <b>${esc(state.filterMap)}</b></span><button type="button" class="btn" id="run-filter-clear">all maps</button>`
    : "";
  el.querySelector("#run-filter-clear")?.addEventListener("click", () => selectMap(null));
}

function renderRunList(allRuns) {
  const runs = state.filterMap ? allRuns.filter(r => r.map === state.filterMap) : allRuns;
  renderRunFilter();
  const wrap = document.getElementById("run-list");
  wrap.innerHTML = "";
  document.getElementById("run-count").textContent = state.filterMap ? `(${runs.length} of ${allRuns.length})` : `(${runs.length})`;
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
  document.getElementById("scoreboard").innerHTML = "";
  document.getElementById("scoreboard-total").innerHTML = "";
  if (message) {
    const err = document.getElementById("report-error");
    err.textContent = message;
    err.hidden = false;
  }
}

function clearReportSections() {
  document.getElementById("report-meta").innerHTML = "";
  document.getElementById("landing").innerHTML = "";
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
  state.report = report;
  document.getElementById("report-title").textContent =
    `${report.map ?? file}${report.name ? " - " + report.name : ""} - ${fmtLocal(report.timestamp)}`;
  renderReportMeta(report);
  renderSummaryCards(report.summary ?? {});
  renderLanding(report);
  renderRunCharts(report);
  renderSegments(report.results);
  renderDivergence(report.results);
  renderWorst(report);
}

function renderSummaryCards(s) {
  const within8Pct = within8Fraction(s);
  const withinPassPct = withinFraction(s, "withinPass") ?? 0;
  const within2Pct = withinFraction(s, "within2") ?? 0;
  const over8 = gradedCount(s) - (s.within8 ?? 0);
  const cards = [
    [`within ${s.passRadius ?? "?"}u`, fmtPct(withinPassPct), s.passRadius === 3 ? (withinPassPct >= 0.9 ? "good" : withinPassPct < 0.8 ? "bad" : "") : ""],
    ["over 8u", `${over8}`, over8 === 0 ? "good" : over8 > gradedCount(s) * 0.05 ? "bad" : ""],
    ["median error", `${fmtErr(s.errMedian)}u`, ""],
    ["p90 error", `${fmtErr(s.errP90)}u`, ""],
    ["max error", `${fmtErr(s.errMax)}u`, ""],
    ["within 2u", fmtPct(within2Pct), within2Pct >= 0.8 ? "good" : within2Pct < 0.5 ? "bad" : ""],
    ["within 8u", fmtPct(within8Pct), within8Pct >= 0.85 ? "good" : within8Pct < 0.7 ? "bad" : ""],
    ["failed to detonate", `${s.notDetonated ?? 0}`, ""],
    ["matched / submitted", `${s.matched ?? 0} / ${s.submitted ?? 0}`, ""],
  ];
  document.getElementById("summary-cards").innerHTML = cards.map(([label, value, cls]) =>
    `<div class="metric-card"><div class="metric-label">${esc(label)}</div>` +
    `<div class="metric-value${cls ? " " + cls : ""}">${esc(value)}</div></div>`).join("");
}

function segmentRow(label, subset) {
  const errs = subset.map(r => r.ErrPredicted).filter(Number.isFinite).sort((a, b) => a - b);
  if (!errs.length) {
    return "";
  }
  const within3 = errs.filter(e => e <= 3).length / errs.length;
  const within8 = errs.filter(e => e <= 8).length / errs.length;
  return `<tr><td>${esc(label)}</td><td>${errs.length}</td>` +
    `<td>${esc(fmtErr(percentile(errs, 50)))}u</td>` +
    `<td>${esc(fmtErr(percentile(errs, 90)))}u</td>` +
    `<td>${esc(fmtErr(errs[errs.length - 1]))}u</td>` +
    `<td>${esc(fmtPct(within3))}</td>` +
    `<td>${esc(fmtPct(within8))}</td></tr>`;
}

function renderSegments(results) {
  // Base throws only, matching the markdown report: a perturbation probe shares
  // its base lineup's type/bounces/stability, so folding probes into these
  // breakdowns would weight every base lineup by its four probes and drag each
  // segment toward the probe distribution. The probes get their own row.
  const base = results.filter(r => !(r.PerturbU > 0));
  const probes = results.filter(r => r.PerturbU > 0);
  const rows = SEGMENT_DEFS.map(([label, pred]) => segmentRow(label, base.filter(pred)));
  if (probes.length) {
    rows.push(segmentRow(`one-tick probes ±${fmtErr(probes[0].PerturbU)}u`, probes));
  }
  const filled = rows.filter(Boolean);
  document.getElementById("segments-body").innerHTML = filled.length
    ? filled.join("")
    : `<tr><td colspan="7" class="muted">no results in this report</td></tr>`;
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
    // Glass state is what the throw was graded against: a pane an earlier
    // throw in the run had already broken is "gone".
    const glass = r.GlassState ? ` <span class="chip">glass ${esc(r.GlassState)}</span>` : "";
    const divergence = (r.DivergenceClass ? esc(`${r.DivergenceClass}@${r.DivergenceTick ?? "?"}`) : "-") + glass;
    return `<tr>` +
      `<td>${r.ErrPredicted.toFixed(0)}u</td>` +
      `<td>${divergence}</td>` +
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


// ---- report meta ------------------------------------------------------------

function renderReportMeta(report) {
  const status = buildStatus(report);
  const bits = [
    `<span class="badge build-${status}">${esc(BUILD_LABEL[status])}</span>`,
    `<span>mesh build ${esc(report.build ?? "?")}</span>`,
    `<span>server build ${esc(report.serverBuild ?? "unknown")}</span>`,
  ];
  if (report.batch) {
    bits.push(`<span>batch ${esc(report.batch)}</span>`);
  }
  if (Number.isFinite(report.tolerance)) {
    bits.push(`<span>target tolerance ${report.tolerance}u</span>`);
  }
  document.getElementById("report-meta").innerHTML = bits.join("");
}

// ---- scoreboard -------------------------------------------------------------

// Every map's runs, oldest group first: one group is one rig campaign (a
// batch tag, or a calendar day for untagged runs).
function groupsByMap(runsNewestFirst) {
  const byMap = new Map();
  for (const run of runsNewestFirst.slice().reverse()) {
    if (!byMap.has(run.map)) {
      byMap.set(run.map, []);
    }
    const groups = byMap.get(run.map);
    const key = groupKeyFor(run);
    let group = groups.find(g => g.key === key);
    if (!group) {
      group = { key, label: groupLabelFor(run), runs: [] };
      groups.push(group);
    }
    group.runs.push(run);
  }
  return byMap;
}

function poolRuns(runs) {
  let graded = 0;
  let within3 = 0;
  let graded3 = 0;
  let within8 = 0;
  const medians = [];
  let status = "match";
  for (const run of runs) {
    const s = run.summary ?? {};
    const n = gradedCount(s);
    graded += n;
    within8 += s.within8 ?? 0;
    if (s.passRadius === 3) {
      within3 += s.withinPass ?? 0;
      graded3 += n;
    }
    if (Number.isFinite(s.errMedian)) {
      medians.push(s.errMedian);
    }
    const st = buildStatus(run);
    if (st === "mismatch" || (st === "unknown" && status === "match")) {
      status = st;
    }
  }
  return {
    graded,
    over8: graded - within8,
    within3Frac: graded3 ? within3 / graded3 : null,
    medLo: medians.length ? Math.min(...medians) : Number.NaN,
    medHi: medians.length ? Math.max(...medians) : Number.NaN,
    when: runs.map(r => r.timestamp).sort().at(-1),
    status,
    runs,
  };
}

// The pass that stands for a map: its newest campaign whose throws are
// comparable to the mesh. A map that has only mismatched passes shows the
// newest one anyway, marked, rather than vanishing from the board.
function latestPassFor(groups) {
  const pooled = groups.map(g => ({ ...poolRuns(g.runs), label: g.label }));
  return pooled.slice().reverse().find(p => p.status !== "mismatch") ?? pooled.at(-1);
}

const SPARK = { W: 132, H: 30, PAD: 3 };

function sparkline(passes) {
  const pts = passes.map(p => p.within3Frac).map(f => (f === null ? null : f * 100));
  const x = i => SPARK.PAD + (passes.length <= 1 ? (SPARK.W - 2 * SPARK.PAD) / 2 : (i / (passes.length - 1)) * (SPARK.W - 2 * SPARK.PAD));
  const y = v => SPARK.PAD + (1 - Math.max(0, v - 50) / 50) * (SPARK.H - 2 * SPARK.PAD);
  const series = pts.map((v, i) => (v === null ? null : { x: x(i), y: y(v), title: `${passes[i].label} - ${v.toFixed(0)}% within 3u, ${passes[i].graded} throws` }));
  let body = `<line x1="${SPARK.PAD}" y1="${y(90).toFixed(1)}" x2="${SPARK.W - SPARK.PAD}" y2="${y(90).toFixed(1)}" stroke="var(--muted)" stroke-dasharray="3 3" opacity="0.6"/>`;
  for (const seg of gapSegments(series)) {
    body += `<polyline points="${seg.map(p => `${p.x.toFixed(1)},${p.y.toFixed(1)}`).join(" ")}" fill="none" stroke="var(--accent)" stroke-width="1.5"/>`;
  }
  series.forEach((p, i) => {
    if (!p) {
      return;
    }
    const last = i === series.length - 1;
    const hollow = passes[i].status === "mismatch";
    body += `<circle cx="${p.x.toFixed(1)}" cy="${p.y.toFixed(1)}" r="${last ? 3.2 : 2}" ` +
      `fill="${hollow ? "var(--panel)" : "var(--accent)"}" stroke="var(--accent)" stroke-width="1.2"><title>${esc(p.title)}</title></circle>`;
  });
  return `<svg class="spark" viewBox="0 0 ${SPARK.W} ${SPARK.H}" role="img" aria-label="within 3u share per campaign, 50 to 100 percent">${body}</svg>`;
}

function scoreCard(map, groups) {
  const passes = groups.map(g => ({ ...poolRuns(g.runs), label: g.label }));
  const latest = latestPassFor(groups);
  const pct = latest.within3Frac;
  const tone = latest.status === "mismatch" ? "stale" : pct === null ? "" : pct >= 0.9 ? "good" : "bad";
  const pill = latest.status === "mismatch" ? "build mismatch"
    : latest.status === "unknown" ? "build unverified"
    : pct === null ? "no 3u grade" : pct >= 0.9 ? "on target" : "below 90%";
  const med = Number.isFinite(latest.medLo)
    ? (Math.abs(latest.medHi - latest.medLo) < 0.05 ? `${fmtErr(latest.medLo)}u` : `${fmtErr(latest.medLo)}-${fmtErr(latest.medHi)}u`)
    : "-";
  return `<button type="button" class="score-card ${tone}${state.filterMap === map ? " selected" : ""}" data-map="${esc(map)}">` +
    `<div class="score-head"><b>${esc(map)}</b><span class="pill ${tone}">${esc(pill)}</span></div>` +
    `<div class="score-main"><span class="score-pct">${esc(fmtPct(pct))}</span><span class="score-pct-label">within 3u</span></div>` +
    `<div class="score-stats">` +
    `<span title="throws graded in this pass">${latest.graded} throws</span>` +
    `<span title="throws that landed more than 8u from the prediction" class="${latest.over8 ? "warn" : ""}">${latest.over8} over 8u</span>` +
    `<span title="median error across the runs of this pass">med ${esc(med)}</span>` +
    `</div>` +
    sparkline(passes) +
    `<div class="score-foot">${esc(latest.label)} - ${passes.length} campaign${passes.length === 1 ? "" : "s"}</div>` +
    `</button>`;
}

function renderScoreboard(runs) {
  const byMap = groupsByMap(runs);
  const entries = [...byMap.entries()].map(([map, groups]) => ({ map, groups, latest: latestPassFor(groups) }));
  // Worst first: the board exists to point at what still needs work.
  entries.sort((a, b) => (a.latest.within3Frac ?? 2) - (b.latest.within3Frac ?? 2) || a.map.localeCompare(b.map));
  const comparable = entries.filter(e => e.latest.status !== "mismatch");
  const total = poolRuns(comparable.flatMap(e => e.latest.runs));
  const onTarget = comparable.filter(e => e.latest.within3Frac !== null && e.latest.within3Frac >= 0.9);
  const below = comparable.filter(e => e.latest.within3Frac !== null && e.latest.within3Frac < 0.9).map(e => e.map);
  const cards = [
    ["maps validated", `${entries.length}`, ""],
    ["throws in latest passes", `${total.graded}`, ""],
    ["within 3u", fmtPct(total.within3Frac), total.within3Frac >= 0.9 ? "good" : ""],
    ["over 8u", `${total.over8}`, total.over8 < 200 ? "good" : "bad"],
    ["maps at 90% or better", `${onTarget.length} / ${comparable.length}`, onTarget.length === comparable.length ? "good" : ""],
  ];
  document.getElementById("scoreboard-total").innerHTML = cards.map(([label, value, cls]) =>
    `<div class="metric-card"><div class="metric-label">${esc(label)}</div>` +
    `<div class="metric-value${cls ? " " + cls : ""}">${esc(value)}</div></div>`).join("") +
    `<div class="metric-card metric-note"><div class="metric-label">target</div>` +
    `<div class="metric-text">fewer than 200 over 8u and every map at 90% within 3u${below.length ? `; still below: ${esc(below.join(", "))}` : ""}</div></div>`;
  const board = document.getElementById("scoreboard");
  board.innerHTML = entries.map(e => scoreCard(e.map, e.groups)).join("");
  for (const el of board.querySelectorAll(".score-card")) {
    el.addEventListener("click", () => selectMap(el.dataset.map === state.filterMap ? null : el.dataset.map));
  }
}

function selectMap(map) {
  state.filterMap = map;
  renderRunList(state.runs);
  for (const el of document.querySelectorAll(".score-card")) {
    el.classList.toggle("selected", el.dataset.map === map);
  }
  if (map) {
    const latest = latestPassFor(groupsByMap(state.runs).get(map) ?? []);
    const newest = latest?.runs.slice().sort((a, b) => new Date(b.timestamp) - new Date(a.timestamp))[0];
    if (newest) {
      selectRun(newest.file);
      document.getElementById("report-title").scrollIntoView({ behavior: "smooth", block: "start" });
    }
  }
}

// ---- landing map ------------------------------------------------------------

// The radar PNG is a class mask, not a picture: R = 255 wall, 128 cover,
// otherwise floor with G as height. Same recolor the main viewer applies, so
// the two pages show the same map.
const mapAssets = new Map();
function loadMapAssets(map) {
  if (!mapAssets.has(map)) {
    mapAssets.set(map, (async () => {
      const res = await fetch(`/data/${encodeURIComponent(map)}.viewer-map.json`);
      if (!res.ok) {
        throw new Error(`HTTP ${res.status}`);
      }
      const meta = await res.json();
      const img = new Image();
      img.src = `/data/${encodeURIComponent(meta.image)}`;
      await img.decode();
      return { meta, img };
    })());
  }
  return mapAssets.get(map);
}

function hex2rgb(h) {
  const v = h.trim().replace("#", "");
  return [0, 2, 4].map(i => Number.parseInt(v.slice(i, i + 2), 16));
}
function lerpRgb(a, b, t) {
  return [0, 1, 2].map(i => Math.round(a[i] + (b[i] - a[i]) * t));
}

function paintRadarCrop(canvas, img, crop) {
  canvas.width = crop.w;
  canvas.height = crop.h;
  const ctx = canvas.getContext("2d", { willReadFrequently: true });
  ctx.drawImage(img, crop.x, crop.y, crop.w, crop.h, 0, 0, crop.w, crop.h);
  const styles = getComputedStyle(document.documentElement);
  const lo = hex2rgb(styles.getPropertyValue("--terrain-lo"));
  const hiRaw = hex2rgb(styles.getPropertyValue("--terrain-hi"));
  const hi = lerpRgb(lo, hiRaw, 0.22);
  const cover = lerpRgb(lo, hiRaw, 0.5);
  const data = ctx.getImageData(0, 0, crop.w, crop.h);
  const d = data.data;
  for (let i = 0; i < d.length; i += 4) {
    if (d[i + 3] === 0) {
      continue;
    }
    const c = d[i] === 255 ? hiRaw : d[i] === 128 ? cover : lerpRgb(lo, hi, d[i + 1] / 255);
    d[i] = c[0];
    d[i + 1] = c[1];
    d[i + 2] = c[2];
  }
  ctx.putImageData(data, 0, 0);
}

function landingRows(report) {
  return (report.results ?? []).filter(r =>
    r.Detonated !== false && Number.isFinite(r.ErrPredicted) &&
    Array.isArray(r.RealRest) && Array.isArray(r.PredictedRest));
}

// Crop in radar pixels around everything the view has to show, padded so
// the target ring never touches the edge; clamped to the image.
function landingCrop(meta, report, rows) {
  const [x0, , x1, y1] = meta.region;
  const ps = meta.pixelSize || 1;
  const imgW = Math.round((x1 - x0) / ps);
  const imgH = Math.round((y1 - meta.region[1]) / ps);
  const px = wx => (wx - x0) / ps;
  const py = wy => (y1 - wy) / ps;
  const pts = [];
  for (const r of rows) {
    pts.push([px(r.RealRest[0]), py(r.RealRest[1])], [px(r.PredictedRest[0]), py(r.PredictedRest[1])]);
    if (state.showOrigins && Array.isArray(r.Feet)) {
      pts.push([px(r.Feet[0]), py(r.Feet[1])]);
    }
  }
  const tol = (report.tolerance ?? 0) / ps;
  if (Array.isArray(report.target)) {
    const [tx, ty] = [px(report.target[0]), py(report.target[1])];
    pts.push([tx - tol, ty - tol], [tx + tol, ty + tol]);
  }
  let minX = Math.min(...pts.map(p => p[0]));
  let maxX = Math.max(...pts.map(p => p[0]));
  let minY = Math.min(...pts.map(p => p[1]));
  let maxY = Math.max(...pts.map(p => p[1]));
  const pad = 40;
  const minSide = 260;
  const growX = Math.max(0, minSide - (maxX - minX)) / 2;
  const growY = Math.max(0, minSide - (maxY - minY)) / 2;
  minX = Math.max(0, Math.floor(minX - pad - growX));
  minY = Math.max(0, Math.floor(minY - pad - growY));
  maxX = Math.min(imgW, Math.ceil(maxX + pad + growX));
  maxY = Math.min(imgH, Math.ceil(maxY + pad + growY));
  return { x: minX, y: minY, w: Math.max(1, maxX - minX), h: Math.max(1, maxY - minY), px, py, tol, ps };
}

function landingOverlay(report, rows, crop) {
  const target = report.target ?? [0, 0, 0];
  const dotR = Math.max(1.2, crop.w / 240);
  const sorted = rows.slice().sort((a, b) => a.ErrPredicted - b.ErrPredicted);
  let body = "";
  if (state.showOrigins) {
    for (const r of rows) {
      if (!Array.isArray(r.Feet)) {
        continue;
      }
      body += `<circle cx="${crop.px(r.Feet[0]).toFixed(1)}" cy="${crop.py(r.Feet[1]).toFixed(1)}" r="${(dotR * 0.9).toFixed(2)}" fill="var(--muted)" fill-opacity="0.55">` +
        `<title>${esc(`stood here: ${typeLabelFor(r)} (${clickShort(r.Strength)}) - ${fmtErr(r.ErrPredicted)}u`)}</title></circle>`;
    }
  }
  if (Array.isArray(report.target)) {
    const tx = crop.px(target[0]).toFixed(1);
    const ty = crop.py(target[1]).toFixed(1);
    body += `<circle cx="${tx}" cy="${ty}" r="${crop.tol.toFixed(1)}" fill="var(--target)" fill-opacity="0.08" stroke="var(--target)" stroke-dasharray="4 3" vector-effect="non-scaling-stroke"/>` +
      `<circle cx="${tx}" cy="${ty}" r="${(dotR * 1.2).toFixed(2)}" fill="var(--target)"><title>target ${esc(fmtVec0(target))}</title></circle>`;
  }
  for (const r of sorted) {
    const color = ERR_BAND[errBand(r.ErrPredicted)];
    const x1 = crop.px(r.PredictedRest[0]).toFixed(1);
    const y1 = crop.py(r.PredictedRest[1]).toFixed(1);
    const x2 = crop.px(r.RealRest[0]).toFixed(1);
    const y2 = crop.py(r.RealRest[1]).toFixed(1);
    const title = `${typeLabelFor(r)} (${clickShort(r.Strength)}) - ${fmtErr(r.ErrPredicted)}u off - ` +
      `${r.PredictedBounces}${Number.isFinite(r.RealBounces) ? "/" + r.RealBounces : ""} bounces` +
      (r.DivergenceClass ? ` - ${r.DivergenceClass}` : "") +
      (r.GlassState ? ` - glass ${r.GlassState}` : "");
    body += `<a href="${esc(openLinkFor(report.map, target, r))}" target="_blank" rel="noopener">` +
      `<line x1="${x1}" y1="${y1}" x2="${x2}" y2="${y2}" stroke="${color}" stroke-width="1.5" vector-effect="non-scaling-stroke" stroke-opacity="0.9"/>` +
      `<circle cx="${x1}" cy="${y1}" r="${dotR.toFixed(2)}" fill="none" stroke="${color}" stroke-width="1" vector-effect="non-scaling-stroke"/>` +
      `<circle cx="${x2}" cy="${y2}" r="${dotR.toFixed(2)}" fill="${color}"/>` +
      `<title>${esc(title)}</title></a>`;
  }
  return `<svg class="landing-overlay" viewBox="${crop.x} ${crop.y} ${crop.w} ${crop.h}" preserveAspectRatio="none" role="img" ` +
    `aria-label="Predicted and real landing spots for every throw of this run on the map">${body}</svg>`;
}

async function renderLanding(report) {
  const wrap = document.getElementById("landing");
  const rows = landingRows(report);
  if (!rows.length || !report.map) {
    wrap.innerHTML = `<p class="muted">no landed throws in this report</p>`;
    return;
  }
  let assets;
  try {
    assets = await loadMapAssets(report.map);
  } catch (e) {
    wrap.innerHTML = `<p class="muted">no radar image for ${esc(report.map)} (${esc(e.message)})</p>`;
    return;
  }
  if (state.report !== report) {
    return; // another run was picked while the radar loaded
  }
  const crop = landingCrop(assets.meta, report, rows);
  const counts = { ok: 0, warn: 0, bad: 0 };
  for (const r of rows) {
    counts[errBand(r.ErrPredicted)]++;
  }
  wrap.innerHTML =
    // Two screen pixels per radar pixel at most (one per game unit): a tight
    // cluster would otherwise stretch to the column width and blur.
    `<div class="landing-frame" style="aspect-ratio:${crop.w} / ${crop.h};max-width:${Math.max(480, crop.w * 2)}px"><canvas class="landing-radar"></canvas>${landingOverlay(report, rows, crop)}</div>` +
    `<div class="landing-tools">` +
    legendChips([
      [`within 3u (${counts.ok})`, ERR_BAND.ok],
      [`3 to 8u (${counts.warn})`, ERR_BAND.warn],
      [`over 8u (${counts.bad})`, ERR_BAND.bad],
      ["target and tolerance", "var(--target)"],
    ]) +
    `<label class="landing-toggle"><input type="checkbox" id="landing-origins"${state.showOrigins ? " checked" : ""}> show where each throw stood</label>` +
    `<span class="muted">hollow ring = predicted rest, dot = real rest; ${(crop.w * crop.ps).toFixed(0)}u across</span>` +
    `</div>`;
  paintRadarCrop(wrap.querySelector(".landing-radar"), assets.img, crop);
  wrap.querySelector("#landing-origins").addEventListener("change", ev => {
    state.showOrigins = ev.target.checked;
    renderLanding(report);
  });
}

// ---- bounce risk ------------------------------------------------------------

// Predicted bounce count is the one thing the solver knows before a throw
// that predicts a miss (audit 2026-09-03), so the miss rate per bucket is the
// chart to read before trusting a high-bounce lineup.
const BOUNCE_BUCKETS = [
  ["0", b => b === 0], ["1", b => b === 1], ["2", b => b === 2], ["3", b => b === 3],
  ["4", b => b === 4], ["5", b => b === 5], ["6-8", b => b >= 6 && b <= 8], ["9+", b => b >= 9],
];

function chartMissByBounces(results) {
  const base = results.filter(r => !(r.PerturbU > 0) && Number.isFinite(r.ErrPredicted) && Number.isFinite(r.PredictedBounces));
  if (!base.length) {
    return "";
  }
  const buckets = BOUNCE_BUCKETS.map(([label, pred]) => {
    const rows = base.filter(r => pred(r.PredictedBounces));
    const n = rows.length;
    return { label, n, miss: n ? rows.filter(r => r.ErrPredicted > 8).length / n * 100 : 0, soft: n ? rows.filter(r => r.ErrPredicted > 3).length / n * 100 : 0 };
  });
  const yMax = Math.max(20, Math.ceil(Math.max(...buckets.map(b => b.soft)) / 10) * 10);
  const yFor = makeLinY(RC, 0, yMax);
  const plotW = RC.W - RC.L - RC.R;
  const slot = plotW / buckets.length;
  const bw = slot * 0.62;
  let body = "";
  const xTicks = [];
  buckets.forEach((b, i) => {
    const cx = RC.L + slot * (i + 0.5);
    xTicks.push({ x: cx, label: b.label });
    if (!b.n) {
      body += `<text x="${cx.toFixed(1)}" y="${(yFor(0) - 4).toFixed(1)}" text-anchor="middle" opacity="0.6">-</text>`;
      return;
    }
    const ySoft = yFor(b.soft);
    const yMiss = yFor(b.miss);
    body += `<rect x="${(cx - bw / 2).toFixed(1)}" y="${ySoft.toFixed(1)}" width="${bw.toFixed(1)}" height="${(yFor(0) - ySoft).toFixed(1)}" fill="${ERR_BAND.warn}" fill-opacity="0.45"><title>${esc(`${b.label} bounces: ${b.soft.toFixed(0)}% over 3u (${b.n} throws)`)}</title></rect>` +
      `<rect x="${(cx - bw / 2).toFixed(1)}" y="${yMiss.toFixed(1)}" width="${bw.toFixed(1)}" height="${(yFor(0) - yMiss).toFixed(1)}" fill="${ERR_BAND.bad}"><title>${esc(`${b.label} bounces: ${b.miss.toFixed(0)}% over 8u (${b.n} throws)`)}</title></rect>` +
      `<text x="${cx.toFixed(1)}" y="${(Math.min(ySoft, yMiss) - 4).toFixed(1)}" text-anchor="middle">n=${b.n}</text>`;
  });
  const ticks = [];
  for (let v = 0; v <= yMax; v += yMax > 50 ? 25 : 10) {
    ticks.push(v);
  }
  const svg = chartShell(RC,
    axisLines(RC) + yTicksSvg(RC, yFor, ticks, v => `${v}%`) + xTicksSvg(RC, xTicks) + body,
    "Share of throws missing by 3 and by 8 units per predicted bounce count");
  return chartBlock("miss rate by predicted bounces", "base throws; predicted bounce count on x",
    svg, legendChips([["over 3u", ERR_BAND.warn], ["over 8u", ERR_BAND.bad]]));
}

// ---- misses per campaign ------------------------------------------------------

function chartMissesByBatch(runsNewestFirst) {
  const groups = [];
  for (const run of runsNewestFirst.slice().reverse()) {
    const key = groupKeyFor(run);
    let g = groups.at(-1)?.key === key ? groups.at(-1) : groups.find(x => x.key === key);
    if (!g) {
      g = { key, label: groupLabelFor(run), when: run.timestamp, byMap: new Map(), status: "match" };
      groups.push(g);
    }
    const over8 = gradedCount(run.summary) - (run.summary?.within8 ?? 0);
    g.byMap.set(run.map, (g.byMap.get(run.map) ?? 0) + over8);
    const st = buildStatus(run);
    if (st === "mismatch" || (st === "unknown" && g.status === "match")) {
      g.status = st;
    }
  }
  if (groups.length < 2) {
    return "";
  }
  const maps = [...new Set(runsNewestFirst.map(r => r.map))].sort();
  const colorFor = m => PALETTE[maps.indexOf(m) % PALETTE.length];
  const totals = groups.map(g => [...g.byMap.values()].reduce((a, b) => a + b, 0));
  const rawMax = Math.max(1, ...totals);
  const step = rawMax > 200 ? 100 : rawMax > 80 ? 50 : rawMax > 40 ? 20 : rawMax > 16 ? 10 : 5;
  const yMax = Math.ceil(rawMax / step) * step;
  const yFor = makeLinY(OT, 0, yMax);
  const plotW = OT.W - OT.L - OT.R;
  const slot = plotW / groups.length;
  const bw = Math.min(28, slot * 0.7);
  let body = "";
  const xTicks = [];
  const labelEvery = Math.ceil(groups.length / Math.floor(plotW / 58));
  groups.forEach((g, i) => {
    const cx = OT.L + slot * (i + 0.5);
    if (i % labelEvery === 0 || i === groups.length - 1) {
      xTicks.push({ x: cx, label: fmtMonthDay(g.when) });
    }
    let yTop = yFor(0);
    for (const m of maps) {
      const v = g.byMap.get(m) ?? 0;
      if (!v) {
        continue;
      }
      const h = yFor(0) - yFor(v);
      yTop -= h;
      body += `<rect x="${(cx - bw / 2).toFixed(1)}" y="${yTop.toFixed(1)}" width="${bw.toFixed(1)}" height="${h.toFixed(1)}" fill="${colorFor(m)}"${g.status === "mismatch" ? ' fill-opacity="0.35"' : ""}>` +
        `<title>${esc(`${g.label} - ${m}: ${v} over 8u${g.status === "mismatch" ? " (server build differed from the mesh)" : ""}`)}</title></rect>`;
    }
    body += `<text x="${cx.toFixed(1)}" y="${(yTop - 3).toFixed(1)}" text-anchor="middle">${totals[i]}</text>`;
  });
  const ticks = [];
  for (let v = 0; v <= yMax; v += step) {
    ticks.push(v);
  }
  const svg = chartShell(OT,
    axisLines(OT) + yTicksSvg(OT, yFor, ticks, String) + xTicksSvg(OT, xTicks) + body,
    "Throws over 8 units per campaign, stacked by map");
  return chartBlock("misses over 8u per campaign", "stacked by map; faded bars were thrown on a server build that differed from the mesh",
    svg, legendChips(maps.map(m => [m, colorFor(m)])));
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
    renderScoreboard(runs);
    renderRunList(runs);
    renderOverTime(runs);
    matchMedia("(prefers-color-scheme: dark)").addEventListener("change", () => {
      if (state.report) {
        renderLanding(state.report);
      }
    });
    await selectRun(runs[0].file);
  } catch (e) {
    renderEmptyIndex(`could not load validation index: ${e.message}`);
  }
}

loadIndex();

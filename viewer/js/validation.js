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

// Shareable link into the main viewer for one worst-offender row. Mirrors
// main.js's permalink(): t is the resolved target, l identifies the lineup
// by its physical parameters (type:strength:fx:fy:fz:pitch:yaw[:runDeg]),
// with runDeg appended only when set so the link shape matches what the
// solver itself produces.
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
  document.getElementById("trend-wrap").innerHTML = "";
  if (message) {
    const err = document.getElementById("report-error");
    err.textContent = message;
    err.hidden = false;
  }
}

function clearReportSections() {
  document.getElementById("summary-cards").innerHTML = "";
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
  renderSummaryCards(report.summary ?? {});
  renderSegments(report.results ?? []);
  renderDivergence(report.results ?? []);
  renderWorst(report);
}

function renderSummaryCards(s) {
  const within8Pct = within8Fraction(s);
  const withinPassPct = s.matched ? s.withinPass / s.matched : 0;
  const cards = [
    ["median error", `${fmtErr(s.errMedian)}u`, ""],
    ["p90 error", `${fmtErr(s.errP90)}u`, ""],
    ["max error", `${fmtErr(s.errMax)}u`, ""],
    ["within 8u", fmtPct(within8Pct), within8Pct >= 0.85 ? "good" : within8Pct < 0.7 ? "bad" : ""],
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
    const cls = r.DivergenceClass ?? "TRACKED";
    counts[cls] = (counts[cls] ?? 0) + 1;
  }
  const chips = DIVERGENCE_CLASSES.filter(cls => counts[cls])
    .map(cls => `<span class="chip">${esc(cls)} <b>${counts[cls]}</b></span>`);
  document.getElementById("divergence-chips").innerHTML = chips.length
    ? chips.join("")
    : `<span class="muted">no misses over 8u</span>`;
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
      `<td>${esc(r.DivergenceClass ?? "?")}@${r.DivergenceTick ?? "?"}</td>` +
      `<td>${esc(typeLabelFor(r))}</td>` +
      `<td class="acc-click-${click}">${click}</td>` +
      `<td>${r.PredictedBounces}/${r.RealBounces}</td>` +
      `<td>${(r.Stability * 100).toFixed(0)}%</td>` +
      `<td>${esc(fmtVec0(r.PredictedRest))}</td>` +
      `<td>${esc(fmtVec0(r.RealRest))}</td>` +
      `<td><a class="btn" target="_blank" rel="noopener" href="${esc(openLinkFor(report.map, target, r))}">open</a></td>` +
      `</tr>`;
  }).join("");
}

// Across every run in index.json (not the currently-selected one), oldest
// to newest, so the two lines read left-to-right as "getting better over
// time" the way a reader expects a trend to run.
function renderTrend(runsNewestFirst) {
  const wrap = document.getElementById("trend-wrap");
  if (runsNewestFirst.length < 2) {
    wrap.innerHTML = `<p class="muted">need at least two runs to plot a trend.</p>`;
    return;
  }
  const runs = runsNewestFirst.slice().reverse();
  const W = 700;
  const H = 160;
  const padL = 30;
  const padR = 8;
  const padT = 10;
  const padB = 8;
  const plotW = W - padL - padR;
  const plotH = H - padT - padB;
  const medians = runs.map(r => r.summary?.errMedian ?? 0);
  const pcts = runs.map(r => within8Fraction(r.summary) * 100);
  const maxMedian = Math.max(1, ...medians);
  const xFor = i => padL + (runs.length === 1 ? 0 : (i / (runs.length - 1)) * plotW);
  const yForMedian = v => padT + plotH - (v / maxMedian) * plotH;
  const yForPct = v => padT + plotH - (v / 100) * plotH;

  const medianLine = medians.map((v, i) => `${xFor(i).toFixed(1)},${yForMedian(v).toFixed(1)}`).join(" ");
  const pctLine = pcts.map((v, i) => `${xFor(i).toFixed(1)},${yForPct(v).toFixed(1)}`).join(" ");

  const medianDots = medians.map((v, i) =>
    `<circle cx="${xFor(i).toFixed(1)}" cy="${yForMedian(v).toFixed(1)}" r="3" fill="var(--accent)">` +
    `<title>${esc(runLabel(runs[i]))} - median ${fmtErr(v)}u</title></circle>`).join("");
  const pctDots = pcts.map((v, i) =>
    `<circle cx="${xFor(i).toFixed(1)}" cy="${yForPct(v).toFixed(1)}" r="3" fill="var(--heat-ok)">` +
    `<title>${esc(runLabel(runs[i]))} - ${v.toFixed(0)}% within 8u</title></circle>`).join("");

  wrap.innerHTML =
    `<svg viewBox="0 0 ${W} ${H}" class="trend-svg" role="img" aria-label="Median error and percent within 8 units across validation runs">` +
    `<polyline points="${medianLine}" fill="none" stroke="var(--accent)" stroke-width="1.5" />` +
    `<polyline points="${pctLine}" fill="none" stroke="var(--heat-ok)" stroke-width="1.5" />` +
    medianDots + pctDots +
    `</svg>` +
    `<div class="trend-legend">` +
    `<span><i style="background:var(--accent)"></i>median error</span>` +
    `<span><i style="background:var(--heat-ok)"></i>% within 8u</span>` +
    `</div>`;
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
    renderTrend(runs);
    await selectRun(runs[0].file);
  } catch (e) {
    renderEmptyIndex(`could not load validation index: ${e.message}`);
  }
}

loadIndex();

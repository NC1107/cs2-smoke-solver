// Lineup card rendering, the keyboard-navigable result list, the
// paste-getpos box, and copy buttons (including the practice-setup card
// wired via wireCopyButtons on document.body). Setting a target and
// selecting a lineup route through the callbacks main.js registers.

import { state, filtered, clickShort, clickClass, esc, skyAngle, proMatched, scoreBreakdown, referenceBand, referenceFallback,
  movementWords, clickWords, aimWords, difficultyWords, TARGET_SNAP_RADIUS } from "./state.js?v=99";

const statusEl = state.statusEl;
const PAGE_SIZE = 50;

// The results header: how many, how sorted, best-few or all, and where you
// are in the list. One row, wired once, updated per render.
function updateHead(shownCount, page, pageCount) {
  document.getElementById("head-count").textContent =
    `${shownCount} result${shownCount === 1 ? "" : "s"}`;
  // Paging appears only when the list does not fit on one page.
  const pager = document.getElementById("list-pager");
  pager.hidden = pageCount <= 1;
  document.getElementById("pager-label").textContent = `${page + 1}/${pageCount}`;
  document.getElementById("pager-prev").disabled = page <= 0;
  document.getElementById("pager-next").disabled = page >= pageCount - 1;
}

// Sorting the full list. Score is the default because it is the ranking the
// top picks use; the rest are the single measures people ask results by.
function sortForList(list, target) {
  const by = document.getElementById("sort-by")?.value ?? "score";
  const copy = [...list];
  const missOf = l => Math.hypot(l.rest[0] - target[0], l.rest[1] - target[1]);
  // An execute's rows are grouped by smoke first, whatever the sort. Sorting
  // purely by score interleaves the smokes, and the headings then repeat every
  // time the list crosses back into one - four headings for two smokes.
  // Ordering WITHIN each smoke still honours the chosen sort.
  if (state.result?.execute) {
    const rank = ranker(by, target, missOf);
    return copy.sort((a, b) => (a._smoke ?? 0) - (b._smoke ?? 0) || rank(a, b));
  }
  switch (by) {
    case "precision": return copy.sort((a, b) => missOf(a) - missOf(b));
    case "stability": return copy.sort((a, b) => (b.stability ?? 0) - (a.stability ?? 0));
    case "bounces": return copy.sort((a, b) => (a.Bounces ?? 0) - (b.Bounces ?? 0));
    case "flight": return copy.sort((a, b) => (a.flightTime ?? 0) - (b.flightTime ?? 0));
    case "community": return copy.sort(ranker(by, target, missOf));
    default: return copy.sort((a, b) => scoreBreakdown(b, target).total - scoreBreakdown(a, target).total);
  }
}

// The comparator behind each sort option, so the execute path can reuse it as a
// tiebreaker instead of restating every case.
function ranker(by, target, missOf) {
  switch (by) {
    case "precision": return (a, b) => missOf(a) - missOf(b);
    case "stability": return (a, b) => (b.stability ?? 0) - (a.stability ?? 0);
    case "bounces": return (a, b) => (a.Bounces ?? 0) - (b.Bounces ?? 0);
    case "flight": return (a, b) => (a.flightTime ?? 0) - (b.flightTime ?? 0);
    // Votes first, and the solver's score only to order the unvoted tail -
    // a separate mode rather than a term in the score, because a measurement
    // and an opinion do not add.
    case "community": return (a, b) =>
      ((state.votes?.tallies?.[b.id]?.score ?? 0) - (state.votes?.tallies?.[a.id]?.score ?? 0)) ||
      (scoreBreakdown(b, target).total - scoreBreakdown(a, target).total);
    default: return (a, b) => scoreBreakdown(b, target).total - scoreBreakdown(a, target).total;
  }
}

let page = 0;
let pagedResultRef = null;
let lastRenderedSelection = -2;

let callbacks = {
  onSetTarget: () => {},
  onSelect: () => {},
  onPreview: () => {},
  onGoTo: () => {},
  onFavorite: () => {},
  onRemove: () => {},
  onShare: () => {},
  onVote: () => {},
  onOpenSaved: () => {},
  onForgetSaved: () => {},
};

// One result, said the way a lineup guide says it: which button, how you are
// standing, how hard it is to land, and what you point at. Every published
// resource answers those four and stops; this row used to answer thirteen
// things at once, in solver units, and a new player could not tell which of
// them was the one that mattered.
//
// The rest of the telemetry (bounces, flight time, exact miss distance,
// stability percent, aim degrees, ranking score) is real and still reachable:
// Detailed mode puts it back on the row, and the opened card shows the full
// score working either way.
// `starTag` is for the plain rows, which have no star button of their own;
// the selected card carries one in its corner and would show the star twice.
function lineupSummaryHtml(l, { starTag = true } = {}) {
  const detailed = state.expertRows;
  const diff = difficultyWords(l);
  // The whole execution - how you stand, what you press, what you point at -
  // hangs off the one part of the row that names the throw, and only appears
  // when asked for. A player scanning a list is choosing between throws, not
  // performing one; the instructions matter after the choice, not during it.
  const how = [esc(l.how), l.aimRef ? `Aim at ${esc(aimWords(l))}.` : ""]
    .filter(Boolean).join(" · ");
  // The number the list is ordered by, on the row rather than two clicks deep
  // inside an opened card. A ranking whose score you cannot see while comparing
  // rows is asking to be taken on trust; the full working stays in the card.
  const target = state.result?.target;
  const score = target ? scoreBreakdown(l, target).total : null;
  const scoreChip = score === null ? "" :
    `<span class="lu-score" title="Match score: how this lineup ranks against the others for this target. Open the row for the full working - what each part added or took away">${score}</span>`;
  // The community's opinion, beside the score and never inside it. Absent
  // until someone has voted, so the list does not fill with zeros.
  const tally = state.votes?.tallies?.[l.id];
  const mine = state.votes?.mine?.[l.id] ?? 0;
  const voteChip = !tally && !state.account ? "" :
    `<span class="lu-votes ${mine > 0 ? "up" : mine < 0 ? "down" : ""}" title="Community votes: ${tally?.up ?? 0} up, ${tally?.down ?? 0} down. ${state.account ? "Click to vote on whether this lineup works for you." : "Sign in with Steam to vote."}">` +
    // Spans, not buttons: the row is itself a button and a button inside one
    // is invalid HTML the parser breaks apart. The click is routed below.
    `<span role="button" tabindex="0" class="vote-btn" data-vote="1" aria-label="Vote up" aria-pressed="${mine > 0}">&#9650;</span>` +
    `<b>${tally?.score ?? 0}</b>` +
    `<span role="button" tabindex="0" class="vote-btn" data-vote="-1" aria-label="Vote down" aria-pressed="${mine < 0}">&#9660;</span></span>`;
  const head =
    `<span class="lu-head" title="${how}">` +
    `<b class="${clickClass(l.strength)}">${clickWords(l.strength)}</b>` +
    `<span class="lu-move">${movementWords(l)}</span>` +
    `<span class="diff ${diff.cls}" title="how forgiving this throw is of your feet and your crosshair">${diff.word}</span>` +
    scoreChip +
    voteChip +
    `</span>`;

  // Where the feet go, and how exactly. This is the position half of "can you
// reproduce it", and it used to be a yes/no: a spot wedged in a corner and one
// standing a shoulder's width off the same wall both showed nothing but their
// coordinates, so there was no way to tell whether you should walk into the
// wall or judge a gap. Walking into geometry costs nothing and is exact;
// judging a gap in a round is not something people can do.
function stanceTag(l) {
  if (l.pin === "corner") {
    return `<span class="ref pin" title="Corner: wedged into one - walk into both walls and your feet are exactly right every time, with nothing to measure and nothing to remember">Corner</span>`;
  }
  if (l.pin === "wall") {
    return `<span class="ref pin" title="Wall: flush against it - walk into it and your feet are exactly right every time, with nothing to measure">Wall</span>`;
  }
  const gap = l.wallGap;
  if (typeof gap !== "number") {
    // Open ground is the common case - badging it would put a tag on nearly
    // every row and say nothing. No stance tag already means "nothing places
    // your feet for you".
    return "";
  }
  // Close enough that people will read the marker as "against that wall" and
  // be wrong about it - which is exactly the case worth naming.
  return `<span class="ref nearwall" title="Near a wall but NOT touching it - your shoulder sits about ${gap.toFixed(0)} units short. Walking into the wall puts you in the wrong place; this spot has to come from the pasted position, not from the wall">${gap.toFixed(0)}u off</span>`;
}

// At most one line of tags, and only facts a player would change their pick
  // over: pros use this spot, or you can be seen throwing it. "exposed" was
  // our word for the second one and had to be learned.
  const tags = [
    // The strongest "you can reproduce this" signal there is: geometry places
    // the feet, so only the aim is left to get right. It was folded into the
    // hover with the rest of the execution detail, and a whole class of lineup
    // people hunt for became invisible.
    stanceTag(l),
    proMatched(l) ? `<span class="ref pro" title="Pro: pros throw this exact smoke from this spot in real matches - same spot, and it lands where this one lands">Pro</span>` : "",
    l.exposed ? `<span class="ref exposed" title="Seen while throwing: a clear line of sight from this spot to where the smoke lands, so anyone holding that area sees you throw it">Exposed</span>` : "",
    // The other half of ranking by reproducibility: when a throw survives to
    // the list with a weak reference (or none), say so on the row instead of
    // letting it borrow the visual weight of a lineup you can actually copy.
    referenceBand(l) >= 6 ? `<span class="ref nolandmark" title="Blind: nothing under the crosshair or the reticle arms to line this up against - the angle can only be set in practice mode, not eyeballed in a round">Blind</span>`
      : referenceBand(l) >= 4 ? `<span class="ref weakref" title="Rough: the only thing to line up against sits far out on the reticle arm. CS2's grenade-crosshair ticks are 10° apart, so this is over a tick off centre and hard to judge under pressure">Rough</span>`
      : referenceBand(l) === 0 ? `<span class="ref tightref" title="Pinpoint: a silhouette sits within 1° of the crosshair - put the crosshair on it and the aim is set, with nothing to estimate">Pinpoint</span>` : "",
    detailed && l._spawn ? `<span class="ref spawn" title="Spawn: throwable from where the round starts you">Spawn</span>` : "",
    starTag && l._favorite ? `<span class="ref fav" title="saved">★</span>` : "",
  ].filter(Boolean).join("");

  // Detailed mode keeps the aim line and the raw numbers on the row; simple
  // mode keeps both behind the hover above.
  const aimExtra = !l.aimRef ? ""
    : l.aimRef.tier === "sky" ? ` (${skyAngle(l).toFixed(0)}° up)`
    : l.aimRef.tier === "reticle" ? ` (${l.aimRef.reticleDeg.toFixed(0)}° out)`
    : l.aimRef.tier === "edge" ? ` (${l.aimRef.edgeDeg.toFixed(1)}° off crosshair)`
    : "";
  const t = state.result?.target;
  const miss = t && l.rest ? Math.hypot(l.rest[0] - t[0], l.rest[1] - t[1]) : null;
  const nums = !detailed ? "" :
    `<span class="lu-aim">Aim: ${esc(aimWords(l))}${aimExtra}</span>` +
    `<span class="lu-nums">${l.Bounces} bounces · ${l.flightTime.toFixed(1)}s` +
    (miss === null ? "" : ` · lands ${miss.toFixed(1)}u off`) +
    ` · ${(l.stability * 100).toFixed(0)}% forgiving</span>`;

  return head + (tags ? `<span class="lu-tags">${tags}</span>` : "") + nums;
}

// Roving tabindex: one list button is tabbable; arrows/Home/End move focus.
function onListKeydown(e) {
  if (!["ArrowDown", "ArrowUp", "Home", "End"].includes(e.key)) {
    return;
  }
  e.preventDefault();
  const btns = [...e.currentTarget.querySelectorAll(".lineup-option")];
  const cur = btns.indexOf(document.activeElement);
  const next = e.key === "Home" ? 0
    : e.key === "End" ? btns.length - 1
    : Math.min(Math.max(cur + (e.key === "ArrowDown" ? 1 : -1), 0), btns.length - 1);
  if (next === cur || !btns[next]) {
    return;
  }
  if (cur >= 0) {
    btns[cur].tabIndex = -1;
  }
  btns[next].tabIndex = 0;
  btns[next].focus();
}

// Detail card for the selected lineup plus a capped, keyboard-navigable list
// of the filtered results (H19). The canvas stays the visual index; the
// list drives the exact same select path as clicking a marker.
// One copy of the results status line; main.js's heat toggle prints it too.
// Naming the hidden count matters: the sky filter ships pre-set, so a user's
// very first solve can silently drop results with no visible cause beyond a
// small count on a collapsed accordion.
export function resultStatusText(shown) {
  const total = state.result ? state.result.lineups.filter(l => !l._removed).length : shown;
  const hidden = total - shown;
  // A scoped solve never computed the other types/clicks; without naming
  // that, relaxing a filter afterwards looks like results went missing.
  const scope = state.solveScope
    ? ` · solved for ${[state.solveScope.types?.[0], state.solveScope.strengths?.map(clickShort)?.[0]].filter(Boolean).join(" + ")} only`
    : "";
  // The reference filter is the one that ships pre-set to something opinionated,
  // so when it would have emptied the list and we showed everything anyway, say
  // that plainly - otherwise the badges look like the filter simply failed.
  const fallback = referenceFallback.active
    ? ` · nothing here has ${referenceFallback.dropped.join(" or ")}, so those are shown too`
    : "";
  return `${shown} lineups - click a marker or use the list` +
    (hidden > 0 ? ` · ${hidden} hidden by filters` : "") + fallback + scope;
}

export function renderLineups() {
  const saved = state.panelMode === "saved";
  // The panel earns its screen space only once there is something to show:
  // results, or the saved list. Empty it reads as a stray bar of chrome
  // (worst on phones, where it anchors to the bottom edge like a footer).
  document.getElementById("panel").hidden = !(state.result || saved);
  for (const b of document.querySelectorAll("#panel-mode .tile")) {
    const on = b.dataset.mode === state.panelMode;
    b.classList.toggle("active", on);
    b.setAttribute("aria-pressed", String(on));
  }
  // The sort and the detailed toggle describe a result list; they have no
  // meaning over the saved one.
  document.querySelector(".panel-head .head-tools").hidden = saved;
  const list = document.getElementById("lineup-list");
  const focusIdx = list.contains(document.activeElement)
    ? document.activeElement.dataset.idx : undefined;
  list.innerHTML = "";
  // The header row lives outside the list; keep the pager hidden until a
  // render shows it for a non-empty result, so it never lingers over an
  // empty or cleared panel.
  document.getElementById("list-pager").hidden = true;
  if (saved) {
    // The render belongs to a selected result, not to the saved list.
    document.getElementById("preview-pane").hidden = true;
    renderSaved(list);
    return;
  }
  if (!state.result) {
    return;
  }
  const shown = filtered();
  statusEl.textContent = resultStatusText(shown.length);
  // An execute's rows belong to a numbered smoke, and without saying so the
  // list is just every throw for several different targets run together.
  const exec = state.result.execute ?? null;

  // The preview lives in the panel's fixed header, outside the scrolling list,
  // so hunting down the results never scrolls the render you are comparing
  // against off screen.
  const pane = document.getElementById("preview-pane");
  pane.hidden = state.selected < 0;
  if (state.selected >= 0) {
    callbacks.onPreview(state.result.lineups[state.selected], document.getElementById("preview-thumb"));
  }

  if (shown.length === 0) {
    return;
  }

  // A fresh solve starts at page 1; a filter change keeps the page but the
  // clamp below drops it back into range if the result count shrank.
  if (state.result !== pagedResultRef) {
    page = 0;
    pagedResultRef = state.result;
    // Re-open a collapsed panel: a new search means you want to see its results.
    const panel = document.getElementById("panel");
    if (panel.classList.contains("collapsed")) {
      panel.classList.remove("collapsed");
      const cb = document.getElementById("panel-collapse");
      cb.setAttribute("aria-expanded", "true");
      cb.title = "Hide results";
      cb.setAttribute("aria-label", "Hide results");
    }
  }
  const pageCount = Math.ceil(shown.length / PAGE_SIZE);
  // Ordered first, because the page a lineup lives on is its position in the
  // list being displayed. Looking the selection up in the unsorted set sent
  // the panel to whatever page that lineup happened to occupy before sorting,
  // so clicking the top result jumped to page 7 of 8.
  const ordered = sortForList(shown, state.result.target);
  const selPos = state.selected >= 0 ? ordered.findIndex(l => l._idx === state.selected) : -1;
  // Follow the selection onto its page only when it actually changed - a manual
  // page turn leaves a selection on another page put, so browsing still works.
  if (selPos >= 0 && state.selected !== lastRenderedSelection) {
    page = Math.floor(selPos / PAGE_SIZE);
  }
  lastRenderedSelection = state.selected;
  page = Math.min(Math.max(page, 0), pageCount - 1);
  const start = page * PAGE_SIZE;

  // No separate "best five" mode: the list is sorted by the same ranking, so
  // the best five are simply the top of page one. A mode that showed the same
  // rows behind an extra switch was one more thing to understand.
  const pageItems = ordered.slice(start, start + PAGE_SIZE);

  updateHead(shown.length, page, pageCount);

  const box = document.createElement("div");
  box.className = "lineup-options";
  box.setAttribute("role", "group");
  box.setAttribute("aria-label", `lineup results, page ${page + 1} of ${pageCount}, ${shown.length} total`);
  let lastSmoke = -1;
  for (const l of pageItems) {
    // An execute's rows belong to a numbered smoke; without a heading the list
    // is every throw for several different targets run together, in an order
    // that looks arbitrary.
    if (exec && l._smoke !== undefined && l._smoke !== lastSmoke) {
      lastSmoke = l._smoke;
      const smoke = exec.smokes[l._smoke];
      const head = document.createElement("div");
      head.className = "exec-head";
      const t = smoke?.target ?? l._smokeTarget ?? [];
      // "best 8 of 45" rather than implying 8 was all there was.
      const kept = exec.smokes[l._smoke]
        ? state.result.lineups.filter(x => x._smoke === l._smoke).length
        : 0;
      const more = smoke && smoke.found > kept ? ` \u00b7 best ${kept} of ${smoke.found}` : "";
      head.textContent = `Smoke ${l._smoke + 1} \u00b7 ${smokeName(t)}` + more;
      head.title = t.length ? `${t[0].toFixed(0)}, ${t[1].toFixed(0)}` : "";
      box.appendChild(head);
    }
    // The selected lineup expands where it sits. Rendering its detail card at
    // the top of the panel instead meant that picking the 40th result put the
    // preview image somewhere far above the scroll position, out of sight.
    box.appendChild(l._idx === state.selected ? detailCard(l) : optionButton(l));
  }
  // Smokes that found nothing still need a line, or an execute quietly looks
  // like it has fewer parts than it has.
  if (exec) {
    exec.smokes.forEach((smoke, i) => {
      if (smoke.found === 0) {
        const head = document.createElement("div");
        head.className = "exec-head exec-empty";
        head.textContent = `Smoke ${i + 1} \u00b7 ${smokeName(smoke.target ?? [])} \u00b7 nothing from this spot`;
        head.title = smoke.emptyReason ?? "";
        box.appendChild(head);
      }
    });
  }
  box.addEventListener("keydown", onListKeydown);
  const home = box.querySelector(".lineup-option") ?? box.firstElementChild;
  if (home?.classList.contains("lineup-option")) {
    home.tabIndex = 0;
  }
  list.appendChild(box);

  // Selecting re-renders the list; keep keyboard focus on the same lineup.
  if (focusIdx !== undefined) {
    const again = box.querySelector(`.lineup-option[data-idx="${focusIdx}"]`);
    if (again && home) {
      home.tabIndex = -1;
      again.tabIndex = 0;
      again.focus();
    }
  }
}

// Updates the fixed header row (`<- page N of M ->   results`) that sits above
// the scrolling list. Turning a page re-renders but deliberately does not touch
// the selection, so a lineup selected on another page stays selected. Prev/next
// are wired once in initPanel; this only refreshes the labels and disabled state.
// A smoke in an execute is called by the named target it sits on, as the
// sidebar list calls it, so the two stay attributable to each other.
function smokeName(t) {
  if (t.length < 2) {
    return "";
  }
  let best = null;
  let bestD = TARGET_SNAP_RADIUS;
  for (const nt of state.targets) {
    const d = Math.hypot(nt.pos[0] - t[0], nt.pos[1] - t[1]);
    if (d < bestD) { bestD = d; best = nt; }
  }
  return best ? best.name : `${t[0].toFixed(0)}, ${t[1].toFixed(0)}`;
}

// Your saved lineups, across every map: the "My Lineups" view. Each row is the
// throw as it was saved - reopened through the single-lineup path, so nothing
// here needs the solve that found it. Grouped by map, the current map first,
// because that is the one you can act on right now.
function renderSaved(list) {
  const all = [...state.saved].sort((a, b) =>
    (a.map === state.currentMap ? 0 : 1) - (b.map === state.currentMap ? 0 : 1) ||
    a.map.localeCompare(b.map) ||
    (b.savedAt ?? 0) - (a.savedAt ?? 0));
  document.getElementById("head-count").textContent =
    all.length === 0 ? "nothing saved yet" : `${all.length} saved`;
  if (all.length === 0) {
    const empty = document.createElement("p");
    empty.className = "saved-empty";
    empty.innerHTML = state.account
      ? "Star a lineup in the results and it will be kept here, on your account."
      : "Star a lineup in the results to keep it here. <a href=\"/auth/steam\">Sign in with Steam</a> and your saved lineups follow you to any browser.";
    list.appendChild(empty);
    return;
  }
  const box = document.createElement("div");
  box.className = "lineup-options";
  box.setAttribute("role", "group");
  box.setAttribute("aria-label", `${all.length} saved lineups`);
  let lastMap = null;
  for (const spec of all) {
    if (spec.map !== lastMap) {
      lastMap = spec.map;
      const head = document.createElement("div");
      head.className = "exec-head";
      head.textContent = spec.map + (spec.map === state.currentMap ? " \u00b7 this map" : "");
      box.appendChild(head);
    }
    box.appendChild(savedRow(spec));
  }
  list.appendChild(box);
}

function savedRow(spec) {
  const b = document.createElement("button");
  b.type = "button";
  b.className = "lineup-option saved-row";
  const fake = { type: spec.type, strength: spec.strength, runDeg: spec.runDeg, aimRef: null, pin: null };
  // Named from this map's targets when it is this map (names can change under
  // a saved lineup); the name recorded at save time stands in for other maps.
  const target = !spec.target ? ""
    : spec.map === state.currentMap ? smokeName(spec.target)
    : spec.targetName ?? `${spec.target[0].toFixed(0)}, ${spec.target[1].toFixed(0)}`;
  b.innerHTML =
    `<span class="lu-head">` +
    `<b class="${clickClass(spec.strength)}">${clickWords(spec.strength)}</b>` +
    `<span class="lu-move">${movementWords(fake)}</span>` +
    (target ? `<span class="lu-nums">\u2192 ${esc(target)}</span>` : "") +
    `<span role="button" tabindex="0" class="saved-drop" data-drop="1" aria-label="Remove from saved" title="Remove from saved">\u00d7</span>` +
    `</span>`;
  b.title = spec.map === state.currentMap
    ? "Open this lineup on the map"
    : `Open this lineup - switches to ${spec.map}`;
  b.addEventListener("click", e => {
    if (e.target.closest("[data-drop]")) {
      e.stopPropagation();
      callbacks.onForgetSaved(spec);
      return;
    }
    callbacks.onOpenSaved(spec);
  });
  return b;
}

function optionButton(l) {
  const b = document.createElement("button");
  b.type = "button";
  b.className = "lineup-option";
  b.dataset.idx = l._idx;
  b.setAttribute("aria-pressed", "false");
  b.tabIndex = -1;
  b.innerHTML = lineupSummaryHtml(l);
  b.addEventListener("click", e => {
    // A vote is on the row but is not a selection of it.
    const v = e.target.closest("[data-vote]");
    if (v) {
      e.stopPropagation();
      // Clicking the arrow you already chose withdraws it.
      const chosen = Number(v.dataset.vote);
      const mine = state.votes?.mine?.[l.id] ?? 0;
      callbacks.onVote(l, mine === chosen ? 0 : chosen);
      return;
    }
    callbacks.onSelect(l._idx);
  });
  b.addEventListener("keydown", e => {
    const v = e.target.closest("[data-vote]");
    if (v && (e.key === "Enter" || e.key === " ")) {
      e.preventDefault();
      e.stopPropagation();
      const chosen = Number(v.dataset.vote);
      const mine = state.votes?.mine?.[l.id] ?? 0;
      callbacks.onVote(l, mine === chosen ? 0 : chosen);
    }
  });
  return b;
}

// Why this lineup ranks where it does: the same numbers the sort used, in the
// order they were applied. A ranking that cannot show its work is just an
// opinion with a number on it. It hangs off the score chip - the thing whose
// meaning is in question - rather than a separate row asking to be found.
function scoreRowsHtml(l) {
  const target = state.result?.target;
  if (!target) {
    return "";
  }
  const { parts } = scoreBreakdown(l, target);
  const rows = parts
    .map(p => `<div class="score-row"><span>${esc(p.label)}</span>` +
      `<b class="${p.delta >= 0 ? "up" : "down"}">${p.delta >= 0 ? "+" : ""}${p.delta}</b></div>`)
    .join("");
  const total = scoreBreakdown(l, target).total;
  return `<div class="score-rows"><div class="score-row"><span>base</span><b>140</b></div>${rows}` +
    `<div class="score-row score-total"><span>Match score</span><b>${total}</b></div></div>`;
}

// The disclosure that holds the score working. Absent when there is no target
// to score against (a spot probe with no picked landing point).
function detailsToggleHtml(l) {
  const rows = scoreRowsHtml(l);
  return !rows ? "" :
    `<button type="button" class="details-toggle" aria-expanded="false">Show details</button>${rows}`;
}

function detailCard(l) {
  const i = l._idx;
  const el = document.createElement("div");
  el.className = "lineup selected";
  // Keep and discard are single icons in the corners, where they stay out of
  // the way of reading the lineup; the score hides behind its own summary; and
  // the remaining actions are one compact row. The card used to spend four
  // full-width buttons and an always-open score table on a lineup you were
  // trying to read.
  // Keep on the left, discard on the right, and the console command behind a
  // clipboard button beside it - the command itself was a whole row of
  // monospace nobody reads, in a card that is meant to be glanced at.
  el.innerHTML =
    `<button type="button" class="card-fav fav-btn" title="${l._favorite ? "Remove from favourites" : "Save this lineup"}" aria-label="${l._favorite ? "Remove from favourites" : "Save this lineup"}" aria-pressed="${l._favorite}">${l._favorite ? "★" : "☆"}</button>` +
    `<button type="button" class="card-copy" data-copy-text="${esc(l.console)}" title="Copy the throw position and angles (setpos)" aria-label="Copy position command">` +
    `<svg viewBox="0 0 20 20" aria-hidden="true"><rect x="7" y="7" width="9" height="9" rx="1.5"/><path d="M13 7V5.5A1.5 1.5 0 0 0 11.5 4H5.5A1.5 1.5 0 0 0 4 5.5v6A1.5 1.5 0 0 0 5.5 13H7"/></svg></button>` +
    `<button type="button" class="card-remove remove-btn" title="Remove this lineup from the list" aria-label="Remove this lineup">×</button>` +
    `<div class="row1">${lineupSummaryHtml(l, { starTag: false })}</div>` +
    // No how-text here: the same sentence is the hover on the row above it,
    // in the card as well as in the list, and printing it twice made the
    // opened lineup taller without saying anything new.
    detailsToggleHtml(l) +
    `<div class="lineup-actions">` +
    `<button type="button" class="btn share-btn" title="copy a link that opens this exact lineup">Share</button>` +
    `<button type="button" class="btn goto-btn" title="move the free 3D camera into this lineup's exact throw spot">Go to</button>` +
    `</div>`;
  // Only the summary row collapses the card. It used to be the whole card, so
  // opening the score breakdown, or clicking anywhere near the console command,
  // closed the lineup instead of doing the thing that was clicked.
  el.querySelector(".row1")?.addEventListener("click", e => {
    // The vote arrows live on this row too, and a vote is not a request to
    // close the card - which is what every vote on a selected lineup did.
    const v = e.target.closest("[data-vote]");
    if (v) {
      e.stopPropagation();
      const chosen = Number(v.dataset.vote);
      const mine = state.votes?.mine?.[l.id] ?? 0;
      callbacks.onVote(l, mine === chosen ? 0 : chosen);
      return;
    }
    callbacks.onSelect(i);
  });
  el.querySelector(".row1")?.addEventListener("keydown", e => {
    const v = e.target.closest("[data-vote]");
    if (v && (e.key === "Enter" || e.key === " ")) {
      e.preventDefault();
      e.stopPropagation();
      const chosen = Number(v.dataset.vote);
      const mine = state.votes?.mine?.[l.id] ?? 0;
      callbacks.onVote(l, mine === chosen ? 0 : chosen);
    }
  });
  // The numbers behind the ranking, one click away rather than on the row:
  // whoever opens this has asked for them, which is the only time they help.
  el.querySelector(".details-toggle")?.addEventListener("click", e => {
    e.stopPropagation();
    const open = !el.classList.contains("show-score");
    el.classList.toggle("show-score", open);
    e.currentTarget.setAttribute("aria-expanded", String(open));
    e.currentTarget.textContent = open ? "Hide details" : "Show details";
  });
  el.querySelector(".card-copy")?.addEventListener("click", e => {
    e.stopPropagation();
    const btn = e.currentTarget;
    navigator.clipboard.writeText(btn.dataset.copyText).then(() => {
      btn.classList.add("copied");
      setTimeout(() => btn.classList.remove("copied"), 1200);
    }).catch(() => {});
  });
  wireCopyButtons(el);

  for (const [selector, action] of [
    [".share-btn", callbacks.onShare],
    [".goto-btn", callbacks.onGoTo],
    [".fav-btn", callbacks.onFavorite],
    [".remove-btn", callbacks.onRemove],
  ]) {
    el.querySelector(selector).addEventListener("click", e => {
      e.stopPropagation();
      action(l);
    });
  }
  return el;
}

// Selecting from the map (rather than the list) can expand a card that is
// scrolled out of view; bring it in without yanking the panel around.
export function revealSelected() {
  document.querySelector("#lineup-list .lineup.selected")
    ?.scrollIntoView({ block: "nearest", behavior: "smooth" });
}

function wireCopyButtons(container) {
  for (const btn of container.querySelectorAll("[data-copy]")) {
    btn.addEventListener("click", e => {
      e.stopPropagation();
      const node = document.getElementById(btn.dataset.copy).cloneNode(true);
      node.querySelector("button").remove();
      // Copy is how a lineup gets from the browser into the game console, so a
      // silent failure (clipboard denied, document unfocused) has to say so
      // rather than look like the button did nothing.
      navigator.clipboard.writeText(node.textContent.trim()).then(() => {
        btn.textContent = "copied";
        setTimeout(() => { btn.textContent = "copy"; }, 1200);
      }, () => {
        btn.textContent = "copy failed";
        setTimeout(() => { btn.textContent = "copy"; }, 1600);
      });
    });
  }
}

export function initPanel(cb) {
  callbacks = cb;
  document.getElementById("pager-prev").addEventListener("click", () => { page -= 1; renderLineups(); });
  document.getElementById("pager-next").addEventListener("click", () => { page += 1; renderLineups(); });
  initPanelResize();
  wireCopyButtons(document.body);
}

// Drag the panel's left edge to widen/narrow it; the chosen width persists so a
// player who needs to see the full command lines keeps them. Width rides a CSS
// custom property (--panel-w) rather than an inline width so the mobile
// full-width bottom-sheet rule still wins. Arrow keys resize for keyboard users;
// double-click resets to the default.
function initPanelResize() {
  const panel = document.getElementById("panel");
  const handle = document.getElementById("panel-resize");
  const MIN = 300, MAX = 640, STEP = 24, KEY = "smokesolver.panelWidth";
  const setW = w => {
    w = Math.min(MAX, Math.max(MIN, Math.round(w)));
    panel.style.setProperty("--panel-w", w + "px");
    return w;
  };
  const saved = Number.parseInt(localStorage.getItem(KEY), 10);
  if (saved) { setW(saved); }
  let startX = 0, startW = 0;
  const onMove = e => setW(startW + (startX - e.clientX));
  const onUp = () => {
    handle.classList.remove("dragging");
    window.removeEventListener("pointermove", onMove);
    window.removeEventListener("pointerup", onUp);
    // pointercancel (touch cancelled by the OS, a system dialog stealing the
    // gesture) also ends the drag; without it the move listener leaked and the
    // panel kept resizing on any later pointer motion.
    window.removeEventListener("pointercancel", onUp);
    localStorage.setItem(KEY, String(panel.getBoundingClientRect().width | 0));
  };
  handle.addEventListener("pointerdown", e => {
    e.preventDefault();
    startX = e.clientX;
    startW = panel.getBoundingClientRect().width;
    handle.classList.add("dragging");
    handle.setPointerCapture?.(e.pointerId);
    window.addEventListener("pointermove", onMove);
    window.addEventListener("pointerup", onUp);
    window.addEventListener("pointercancel", onUp);
  });
  // Keyboard resize: the handle advertises role="separator", so honor it. Left
  // widens the panel (its edge moves left), Right narrows it, by one STEP.
  handle.addEventListener("keydown", e => {
    const dir = e.key === "ArrowLeft" ? 1 : e.key === "ArrowRight" ? -1 : 0;
    if (dir === 0) { return; }
    e.preventDefault();
    const w = setW(panel.getBoundingClientRect().width + dir * STEP);
    localStorage.setItem(KEY, String(w));
  });
  handle.addEventListener("dblclick", () => {
    panel.style.removeProperty("--panel-w");
    localStorage.removeItem(KEY);
  });
}

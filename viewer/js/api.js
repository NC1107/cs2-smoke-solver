// Fetch wrappers. No DOM access here; callers own status text and overlays.

import { state } from "./state.js?v=112";

// Cache-bust a data URL with the map build: re-processed radars/GLBs change
// content without changing name, and the query string gets a fresh copy past
// both the browser cache and any CDN edge in front of the origin.
export function cacheBust(url) {
  return state.mapData?.build ? `${url}?v=${state.mapData.build}` : url;
}

// Every extracted map leaves data/{map}.viewer-map.json/.png behind (see
// ViewerDataCommand); this lists them so the viewer's map picker never has
// to hardcode a map list.
export async function loadMapList() {
  const res = await fetch("/api/maps");
  if (!res.ok) {
    throw new Error(`HTTP ${res.status}`);
  }
  return res.json();
}

export async function loadMapData(map) {
  const res = await fetch(`data/${encodeURIComponent(map)}.viewer-map.json`);
  if (!res.ok) {
    throw new Error(`HTTP ${res.status}`);
  }
  return res.json();
}

// T/CT spawn positions from the map's entity lump: { t: [[x,y,z]...], ct: [...] }.
export async function fetchSpawns(map) {
  const res = await fetch(`/api/spawns?map=${encodeURIComponent(map)}`);
  if (!res.ok) {
    throw new Error(`HTTP ${res.status}`);
  }
  return res.json();
}

// The map's named smoke targets: [{ name, named, pos: [x,y,z], landings, spread }].
// Seeded from pro landings and hand-named; `named` false means the label is a
// guess from the nearest callout and should be shown as one.
export async function fetchTargets(map) {
  const res = await fetch(`/api/targets?map=${encodeURIComponent(map)}`);
  if (!res.ok) {
    throw new Error(`HTTP ${res.status}`);
  }
  return res.json();
}

// Pro smoke throw origins and landings parsed from HLTV demos (rig/parse-demo-
// smokes.py): { map, demos, throws: [[x,y]...], lands: [[x,y]...] }. Returns null
// when a map has no parsed demos, so the toggle stays hidden.
export async function fetchProSmokes(map) {
  const res = await fetch(cacheBust(`data/${encodeURIComponent(map)}.prosmokes.json`));
  return res.ok ? res.json() : null;
}

// Physics-vs-render geometry mismatches from the meshdiff CLI command:
// { map, step, cells: [[x, y, z, kind]...], renderTris, physicsTris }. Most
// maps have never had meshdiff run against them, so a missing file is the
// expected default (null keeps the toggle hidden), not an error.
//
// A HEAD first, because the payload carries the mismatched surfaces themselves
// and runs to megabytes: every map load would pay for a dev overlay almost
// nobody switches on. The body is fetched when the overlay is.
export async function meshDiffExists(map) {
  const res = await fetch(cacheBust(`data/${encodeURIComponent(map)}.meshdiff.json`), { method: "HEAD" });
  return res.ok;
}

export async function fetchMeshDiff(map) {
  const res = await fetch(cacheBust(`data/${encodeURIComponent(map)}.meshdiff.json`));
  return res.ok ? res.json() : null;
}


// Which walkable levels are stacked over a 2D point. A top-down click on a
// map like de_nuke can mean the roof, the bombsite under it, or the site below
// that, and the solver has to pick one - this is what lets the viewer ask.
// Returns [] when the map has no nav data or the request fails: an ambiguous
// click is worth a question, a broken request is not.
export async function fetchLevels(map, x, y) {
  try {
    const res = await fetch(`/api/levels?map=${encodeURIComponent(map)}&x=${x}&y=${y}`);
    return res.ok ? (await res.json()).levels ?? [] : [];
  } catch {
    return [];
  }
}

// The /api/lineup POST. The server streams NDJSON progress lines (phase
// markers and batches of checked origins) before the final result line;
// each progress line is handed to `onProgress` so the map can paint the
// sweep live. HTTP failures resolve to `{ error }` carrying the exact
// user-facing status line (503 means the serve command lacks data flags);
// network and JSON-parse failures reject so the caller's catch shows
// `error: <message>` as before. Aborting `signal` rejects with AbortError.
export async function runQuery(body, signal, onProgress) {
  const res = await fetch("/api/lineup", {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify(body),
    signal,
  });
  if (!res.ok) {
    // The server writes a specific {error} body for every rejection ("target
    // is outside the map bounds", ...); show that, not just the status code.
    let message = null;
    try {
      message = (await res.json()).error;
    } catch {
      // Not JSON (a proxy error page, a cut connection) - fall through.
    }
    if (!message) {
      message = res.status === 503 ? "no API: serve needs --geo/--nav/--attrs" : `error ${res.status}`;
    }
    return { error: message };
  }
  const reader = res.body.getReader();
  const decoder = new TextDecoder();
  let buf = "";
  let result = null;
  for (;;) {
    const { done, value } = await reader.read();
    if (done) {
      break;
    }
    buf += decoder.decode(value, { stream: true });
    let nl;
    while ((nl = buf.indexOf("\n")) >= 0) {
      const line = buf.slice(0, nl);
      buf = buf.slice(nl + 1);
      if (!line.trim()) {
        continue;
      }
      const msg = JSON.parse(line);
      if (msg.result) {
        result = msg.result;
      } else if (msg.error) {
        return { error: msg.error };
      } else {
        onProgress?.(msg);
      }
    }
  }
  if (!result) {
    return { error: "stream ended without a result" };
  }
  return { data: result };
}

// The grenade's real flight path, simulated server-side by the same exact
// integrator that verified the lineup. Cached on the lineup by the caller, since
// a throw's arc is fixed for a given map build.
export async function fetchTrajectory(map, l, broken) {
  const q = new URLSearchParams({
    map, x: l.feet[0], y: l.feet[1], z: l.feet[2],
    type: l.type, pitch: l.pitch, yaw: l.yaw, strength: l.strength,
    // The run direction changes the carried velocity, so a lateral run-jump's
    // arc is a different curve than the same angles thrown while holding W.
    runDeg: l.runDeg ?? 0,
  });
  // World state the lineup was solved under (broken glass / open doors): the
  // drawn arc must fly the same world or it shows bounces the throw lacks.
  if (broken) { q.set("broken", broken); }
  const res = await fetch(`/api/trajectory?${q}`);
  if (!res.ok) {
    throw new Error(`trajectory HTTP ${res.status}`);
  }
  return res.json();
}

// One fully-analyzed lineup from its physical spec alone: the same shape a map
// sweep returns per lineup, plus its flight path inline. Opening a shared link
// renders just that throw with this, instead of sweeping the whole map.
export async function fetchLineupOne(map, target, l, broken) {
  const q = new URLSearchParams({
    map, x: l.feet[0], y: l.feet[1], z: l.feet[2],
    type: l.type, pitch: l.pitch, yaw: l.yaw, strength: l.strength,
    runDeg: l.runDeg ?? 0,
    tx: target[0], ty: target[1], tz: target[2] ?? 0,
  });
  if (broken) { q.set("broken", broken); }
  const res = await fetch(`/api/lineup-one?${q}`);
  if (!res.ok) {
    throw new Error(`lineup-one HTTP ${res.status}`);
  }
  return res.json();
}

// The positional slack ring: per world direction, how far the feet can drift
// from the lineup's exact spot before the same aim misses `within` units of
// the target. Cached on the lineup by the caller (keyed by `within`).
export async function fetchSlack(map, l, target, within, broken) {
  const q = new URLSearchParams({
    map, x: l.feet[0], y: l.feet[1], z: l.feet[2],
    type: l.type, pitch: l.pitch, yaw: l.yaw, strength: l.strength,
    runDeg: l.runDeg ?? 0,
    tx: target[0], ty: target[1], tz: target[2] ?? 0, within,
  });
  if (broken) { q.set("broken", broken); }
  const res = await fetch(`/api/slack?${q}`);
  if (!res.ok) {
    throw new Error(`slack HTTP ${res.status}`);
  }
  return res.json();
}

export async function fetchMesh(map) {
  // Revalidate rather than trust a browser copy cached under a since-changed
  // policy: a browser that stored this under the old week-long max-age would
  // otherwise keep showing the old geometry (e.g. Retake tape that has since
  // been removed) for the rest of that week. no-cache still returns a 304 when
  // the mesh is unchanged, so this costs nothing in the common case.
  const res = await fetch(`/api/mesh?map=${encodeURIComponent(map)}`, { cache: "no-cache" });
  if (!res.ok) {
    throw new Error(`mesh HTTP ${res.status}`);
  }
  return res.arrayBuffer();
}

// The volume a smoke placed at `at` would actually fill, from the server's own
// flood fill - the same model the solver uses, so the overlay cannot disagree
// with the answer. Geometry-aware on purpose: a circle would promise coverage
// through walls, which is the one thing this is meant to check.
export async function fetchSmokeCoverage(map, at, full = false) {
  const q = new URLSearchParams({ map, x: at[0], y: at[1], z: at[2] ?? 0, full: full ? "true" : "false" });
  const res = await fetch(`/api/smoke?${q}`);
  if (!res.ok) {
    throw new Error(`smoke coverage HTTP ${res.status}`);
  }
  return res.json();
}

// An execute: every smoke in the list, solved from one throw position.
export async function runExecute(map, origin, targets) {
  const res = await fetch("/api/execute", {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ map, origin, targets }),
  });
  return executeReply(res);
}

// An execute's answer is one JSON document, but it arrives late: the server
// keeps the connection alive with blank lines while the solves run, and a
// failure after that can only be reported in the body, so a 200 may still
// carry an error.
async function executeReply(res) {
  if (!res.ok) {
    return { error: (await res.json().catch(() => ({}))).error ?? `error ${res.status}` };
  }
  const data = await res.json();
  return data.error ? { error: data.error } : { data };
}

// The other half: where can one player stand and throw all of them. Each target
// costs a full map-wide solve the first time, so this can take minutes cold and
// is nearly instant once they are cached.
export async function findExecuteSpots(map, targets) {
  const res = await fetch("/api/execute/spots", {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ map, targets }),
  });
  return executeReply(res);
}

// Who is signed in, or null. Never throws: an anonymous visitor is the normal
// case, not an error.
export async function fetchMe() {
  const res = await fetch("/auth/me");
  return res.ok ? res.json() : null;
}

export async function signOut() {
  await fetch("/auth/logout", { method: "POST" });
}

// The account's saved lineups: { lineups: [...] }.
export async function fetchSavedLineups() {
  const res = await fetch("/api/me/lineups");
  if (!res.ok) {
    throw new Error(`saved lineups HTTP ${res.status}`);
  }
  return res.json();
}

export async function putSavedLineups(lineups) {
  const res = await fetch("/api/me/lineups", {
    method: "PUT",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ lineups }),
  });
  if (!res.ok) {
    throw new Error(`save HTTP ${res.status}`);
  }
}

// Community votes at a spot: { target, tallies: { lineupId: {up,down,score} }, mine: { lineupId: vote } }.
export async function fetchVotes(map, target) {
  const q = new URLSearchParams({ map, x: target[0], y: target[1], z: target[2] ?? 0 });
  const res = await fetch(`/api/votes?${q}`);
  if (!res.ok) {
    throw new Error(`votes HTTP ${res.status}`);
  }
  return res.json();
}

export async function castVote(map, target, lineupId, vote) {
  const res = await fetch("/api/vote", {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ map, target, lineupId, vote }),
  });
  if (!res.ok) {
    return { error: (await res.json().catch(() => ({}))).error ?? `error ${res.status}` };
  }
  return { data: await res.json() };
}

// Replace a map's named targets (admin only): the server keeps ids, mints
// them for new entries, and answers with the list as it was written.
export async function putTargets(map, targets) {
  const res = await fetch(`/api/targets?map=${encodeURIComponent(map)}`, {
    method: "PUT",
    headers: { "Content-Type": "application/json" },
    // serverName is the file's name; `name` may carry a display-only number.
    body: JSON.stringify(targets.map(t => ({ id: t.id ?? undefined, name: t.serverName ?? t.name, named: !!t.named, pos: t.pos, landings: t.landings ?? 0, spread: t.spread ?? 0 }))),
  });
  if (!res.ok) {
    throw new Error(res.status === 403 ? "not an admin" : (await res.json().catch(() => ({}))).error ?? `error ${res.status}`);
  }
  return res.json();
}

// Fetch wrappers. No DOM access here; callers own status text and overlays.

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
    return { error: res.status === 503 ? "no API: serve needs --geo/--nav/--attrs" : `error ${res.status}` };
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
export async function fetchTrajectory(map, l) {
  const q = new URLSearchParams({
    map, x: l.feet[0], y: l.feet[1], z: l.feet[2],
    type: l.type, pitch: l.pitch, yaw: l.yaw, strength: l.strength,
  });
  const res = await fetch(`/api/trajectory?${q}`);
  if (!res.ok) {
    throw new Error(`trajectory HTTP ${res.status}`);
  }
  return res.json();
}

export async function fetchMesh(map) {
  const res = await fetch(`/api/mesh?map=${encodeURIComponent(map)}`);
  if (!res.ok) {
    throw new Error(`mesh HTTP ${res.status}`);
  }
  return res.arrayBuffer();
}

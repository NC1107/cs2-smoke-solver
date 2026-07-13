// Fetch wrappers. No DOM access here; callers own status text and overlays.

export async function loadMapData() {
  const res = await fetch("data/viewer-map.json");
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

export async function fetchMesh() {
  const res = await fetch("/api/mesh");
  if (!res.ok) {
    throw new Error(`mesh HTTP ${res.status}`);
  }
  return res.arrayBuffer();
}

// Fetch wrappers. No DOM access here; callers own status text and overlays.

export async function loadMapData() {
  const res = await fetch("data/viewer-map.json");
  if (!res.ok) {
    throw new Error(`HTTP ${res.status}`);
  }
  return res.json();
}

// The /api/lineup POST. HTTP failures resolve to `{ error }` carrying the
// exact user-facing status line (503 means the serve command lacks data
// flags); network and JSON-parse failures reject so the caller's catch shows
// `error: <message>` as before. Aborting `signal` rejects with AbortError.
export async function runQuery(body, signal) {
  const res = await fetch("/api/lineup", {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify(body),
    signal,
  });
  if (!res.ok) {
    return { error: res.status === 503 ? "no API: serve needs --geo/--nav/--attrs" : `error ${res.status}` };
  }
  return { data: await res.json() };
}

export async function fetchMesh() {
  const res = await fetch("/api/mesh");
  if (!res.ok) {
    throw new Error(`mesh HTTP ${res.status}`);
  }
  return res.arrayBuffer();
}

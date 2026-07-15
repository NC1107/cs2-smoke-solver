"""Render first-person preview screenshots for a target's top scored lineups.

Live capture from the actual game client was abandoned: Steam's overlay and
the engine's own input handling both silently reject synthetic keyboard
input (X11 XTest via xdotool, kernel uinput via ydotool), which is the same
class of protection games use against macro/cheat tools - not something
worth trying to defeat for a legitimate feature. Instead this renders from
the same collision mesh the solver itself reasons about, using the viewer's
own three.js scene (viewer/js/view3d.js renderPreview) via a headless
Chrome, so the preview always matches exactly what the solver measured.

Requires: the smokesolver-viewer service running, and native
google-chrome-stable installed (chrome-devtools-axi cannot use a Flatpak
build - it needs a real binary at the standard path for its own driver).

Usage: render-previews.py --target x,y[,z] [--origin x,y] [--limit 5] [--out data/previews]
"""
import argparse
import json
import os
import subprocess
import sys
import urllib.request

VIEWER = "http://127.0.0.1:8137"


def run_axi(*args: str, timeout: float = 60) -> str:
    result = subprocess.run(
        ["npx", "-y", "chrome-devtools-axi", *args],
        capture_output=True, text=True, timeout=timeout)
    # A missing Chrome or a crashed bridge otherwise "succeeds" with empty
    # stdout, and the run ends with zero previews and no explanation.
    if result.returncode != 0:
        raise RuntimeError(
            f"chrome-devtools-axi {args[0]} failed (exit {result.returncode}): {result.stderr.strip()[:500]}")
    return result.stdout


def solve(target: list[float], origin: list[float] | None) -> list[dict]:
    body: dict = {"target": target}
    if origin is not None:
        body["origin"] = origin
    req = urllib.request.Request(
        f"{VIEWER}/api/lineup", data=json.dumps(body).encode(),
        headers={"Content-Type": "application/json"})
    with urllib.request.urlopen(req, timeout=300) as resp:
        lines = [json.loads(line) for line in resp.read().decode().splitlines() if line.strip()]
    result = next(m["result"] for m in lines if "result" in m)
    return result["lineups"]


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--target", required=True, help="x,y or x,y,z")
    parser.add_argument("--origin", help="x,y - restrict to lineups near this click")
    parser.add_argument("--limit", type=int, default=5, help="how many lineups to render (skips sky shots)")
    parser.add_argument("--fov", type=float, default=90, help="client fov_desired (CS2/CS:GO default: 90)")
    parser.add_argument("--out", default="data/previews", help="output directory")
    args = parser.parse_args()

    target = [float(v) for v in args.target.split(",")]
    origin = [float(v) for v in args.origin.split(",")] if args.origin else None

    print(f"solving target {target}...", file=sys.stderr)
    lineups = solve(target, origin)
    candidates = [l for l in lineups if l["aimRef"]["tier"] != "sky"][:args.limit]
    if not candidates:
        print("no non-sky lineups found for this target", file=sys.stderr)
        return 1
    print(f"rendering {len(candidates)} of {len(lineups)} lineups", file=sys.stderr)

    run_axi("open", VIEWER)
    setup = (
        "async () => { "
        "document.getElementById('view3d').click(); "
        "const {ensure3d} = await import('/viewer/js/view3d.js'); "
        "await ensure3d(); "
        "document.body.classList.add('preview-mode'); "
        "return 'ready'; }"
    )
    run_axi("eval", setup)

    # The textured GLB is ~300 MB (real materials/UVs exported straight from
    # the VPK), so give it a generous timeout; renderPreview() falls back to
    # the flat collision mesh automatically if this rejects.
    print("loading textured scene (~300MB, one-time)...", file=sys.stderr)
    texture_result = run_axi(
        "eval",
        "async () => { const {ensureTexturedScene} = await import('/viewer/js/view3d.js'); "
        "try { await ensureTexturedScene(); return 'loaded'; } "
        "catch (e) { return 'failed: ' + e.message; } }",
        timeout=180)
    print(f"  {texture_result.strip()}", file=sys.stderr)
    # A failed texture load means every subsequent render is the flat mesh, not
    # the previews this run exists to produce - stop instead of shipping them.
    if "failed:" in texture_result:
        raise RuntimeError(f"textured scene load failed: {texture_result.strip()}")

    slug = f"{target[0]:.0f}_{target[1]:.0f}"
    outdir = os.path.join(args.out, slug)
    os.makedirs(outdir, exist_ok=True)

    for i, l in enumerate(candidates):
        call = (
            "async () => { const {renderPreview} = await import('/viewer/js/view3d.js'); "
            f"renderPreview({{feet:{json.dumps(l['feet'])}, type:{json.dumps(l['type'])}, "
            f"pitchDeg:{l['pitch']}, yawDeg:{l['yaw']}, fovDesiredDeg:{args.fov}}}); return 'rendered'; }}"
        )
        # Each new camera angle can expose materials three.js has not yet
        # compiled shaders for (715 distinct materials in the textured
        # scene), so the first few renders can be markedly slower than a
        # cached one; the flat-mesh fallback never needed this headroom.
        run_axi("eval", call, timeout=120)
        path = os.path.join(outdir, f"{i:02d}_{l['type']}_{l['aimRef']['tier']}.png")
        run_axi("screenshot", path)
        print(f"  {path}", file=sys.stderr)

    print(f"wrote {len(candidates)} previews to {outdir}", file=sys.stderr)
    return 0


if __name__ == "__main__":
    sys.exit(main())

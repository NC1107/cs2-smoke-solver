#!/usr/bin/env python3
"""Read a .s2geo collision mesh (V1-V3) and list the triangles near a point.

    rig/s2geo-dump.py data/de_dust2.s2geo X Y Z RADIUS [--groups]

Prints each group (index, name, interaction layers, exclusions, triangle
count) and the triangles whose centroid lies within RADIUS of the point,
largest first, with attribute, normal, area and corners. The diagnostic
behind the 2026-09-04 accuracy loop: what surface did the sim hit that the
game did not, or the other way round.
"""
import array, math, struct, sys, collections

def read_str(f):
    n = shift = 0
    while True:
        b = f.read(1)[0]
        n |= (b & 0x7F) << shift
        shift += 7
        if not b & 0x80:
            break
    return f.read(n).decode("utf-8")

def load(path):
    f = open(path, "rb")
    magic = f.read(8)
    name, build = read_str(f), read_str(f)
    na = struct.unpack("<i", f.read(4))[0]
    attrs = [read_str(f) for _ in range(na)]
    inter, excl = [], []
    if magic != b"S2SSGEO1":
        for _ in range(na):
            k = struct.unpack("<i", f.read(4))[0]
            inter.append([read_str(f) for _ in range(k)])
    else:
        inter = [[] for _ in range(na)]
    if magic == b"S2SSGEO3":
        for _ in range(na):
            k = struct.unpack("<i", f.read(4))[0]
            excl.append([read_str(f) for _ in range(k)])
    else:
        excl = [[] for _ in range(na)]
    nv = struct.unpack("<i", f.read(4))[0]
    verts = array.array("f"); verts.frombytes(f.read(4 * nv))
    ni = struct.unpack("<i", f.read(4))[0]
    idx = array.array("i"); idx.frombytes(f.read(4 * ni))
    nt = struct.unpack("<i", f.read(4))[0]
    ta = f.read(nt)
    return name, build, attrs, inter, excl, verts, idx, ta

def tris_near(mesh, x, y, z, r):
    _, _, attrs, _, _, verts, idx, ta = mesh
    out = []
    for t in range(len(idx) // 3):
        pts = [(verts[3 * i], verts[3 * i + 1], verts[3 * i + 2]) for i in idx[3 * t:3 * t + 3]]
        cx, cy, cz = (sum(p[k] for p in pts) / 3 for k in range(3))
        if abs(cx - x) < r and abs(cy - y) < r and abs(cz - z) < r:
            a, b, c = pts
            u = (b[0] - a[0], b[1] - a[1], b[2] - a[2]); v = (c[0] - a[0], c[1] - a[1], c[2] - a[2])
            n = (u[1] * v[2] - u[2] * v[1], u[2] * v[0] - u[0] * v[2], u[0] * v[1] - u[1] * v[0])
            L = math.sqrt(sum(q * q for q in n)) or 1
            out.append((t, ta[t], attrs[ta[t]], pts, tuple(q / L for q in n), L / 2))
    return out

if __name__ == "__main__":
    path = sys.argv[1]
    mesh = load(path)
    name, build, attrs, inter, excl, verts, idx, ta = mesh
    counts = collections.Counter(ta)
    print(f"{name} build {build}: {len(idx) // 3} triangles")
    for i, (a, l, e) in enumerate(zip(attrs, inter, excl)):
        print(f"  #{i} {a} as={l} exclude={e} triangles={counts.get(i, 0)}")
    if len(sys.argv) >= 6:
        x, y, z, r = map(float, sys.argv[2:6])
        for t, ai, a, pts, n, area in sorted(tris_near(mesh, x, y, z, r), key=lambda q: -q[5])[:20]:
            print(f"  tri {t} #{ai} {a} n({n[0]:.2f},{n[1]:.2f},{n[2]:.2f}) area {area:.0f} " + " ".join(f"({p[0]:.0f},{p[1]:.0f},{p[2]:.1f})" for p in pts))

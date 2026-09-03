using System.Numerics;
using SmokeSolver.Sim;

namespace SmokeSolver.Solver;

// Origin generation: where a player can stand. Self-contained - nothing here
// depends on the sweep/refine/rank half of the solver except sharing the
// class, which is why it lives in its own file.
public static partial class LineupSolver
{
    /// <summary>
    /// Feet positions sampled from nav-mesh walkable areas: reachable by definition,
    /// unlike raw geometry scanning which happily stands players on rooftops.
    /// Sample z starts from the area plane and snaps to the voxelized ground.
    /// </summary>
    public static List<Vector3> OriginsFromNavAreas(
        VoxelGrid grid,
        IReadOnlyList<float[][]> areaCorners,
        Vector3 min,
        Vector3 max,
        float sampleStep = 32f,
        TriangleCollider? collider = null)
    {
        var origins = new List<Vector3>();
        foreach (var corners in areaCorners)
        {
            var minX = corners.Min(c => c[0]);
            var maxX = corners.Max(c => c[0]);
            var minY = corners.Min(c => c[1]);
            var maxY = corners.Max(c => c[1]);
            if (maxX < min.X || minX > max.X || maxY < min.Y || minY > max.Y)
            {
                continue;
            }
            var avgZ = corners.Average(c => c[2]);
            if (avgZ < min.Z || avgZ > max.Z)
            {
                continue;
            }
            var countBefore = origins.Count;
            for (var x = MathF.Ceiling(minX / sampleStep) * sampleStep; x <= maxX; x += sampleStep)
            {
                for (var y = MathF.Ceiling(minY / sampleStep) * sampleStep; y <= maxY; y += sampleStep)
                {
                    if (x < min.X || x > max.X || y < min.Y || y > max.Y || !PointInPolygon(corners, x, y))
                    {
                        continue;
                    }
                    origins.Add(SnapToGround(grid, collider, new Vector3(x, y, avgZ)));
                }
            }
            // Tiny areas can miss every grid sample; keep their centroid so narrow
            // ledges and stair areas still contribute origins.
            if (origins.Count == countBefore)
            {
                var cx = corners.Average(c => c[0]);
                var cy = corners.Average(c => c[1]);
                if (cx >= min.X && cx <= max.X && cy >= min.Y && cy <= max.Y)
                {
                    origins.Add(SnapToGround(grid, collider, new Vector3(cx, cy, avgZ)));
                }
            }
        }
        AddElevatedOrigins(grid, areaCorners, min, max, sampleStep, collider, origins);
        if (collider != null)
        {
            AddPinnedOrigins(grid, collider, origins);
        }
        return origins;
    }

    // Valve authors the nav mesh for BOT pathing, and bots do not jump onto
    // things - so the crates, platforms and ledges players routinely stand on
    // carry no nav area at all. Sampling nav areas alone therefore cannot put a
    // thrower on any of them: measured across all 14 maps, 19-27 such spots per
    // map are genuinely standable (player-solid floor, hull-sized footprint,
    // full headroom) and reachable by a standing jump, yet no origin is ever
    // generated there. Confirmed by hand on de_dust2 [-1413,2852] (standable at
    // z=47, nearest origin 45u away down at floor z=8) and de_mirage
    // [-2192,-672] (standable at z=-8, nearest origin 128u below it).
    //
    // Scanning is anchored to each nav area - its own footprint plus a band
    // one step outside it - and admits only surfaces within jump height of
    // that area's floor. That reachability gate is what preserves the "nav
    // areas, not raw geometry" property the doc comment above describes: a
    // rooftop or an out-of-bounds shelf has no nav ground one jump below it,
    // so it is still never stood on.
    //
    // The area's own footprint has to be scanned too, not just the ring: nav
    // quads frequently span straight under a raised platform, so its top is
    // inside the polygon rather than outside it. Skipping interior samples as
    // "already covered by the flat-ground pass" silently missed exactly that
    // case - de_mirage's [-2192,-672] platform, whose XY does carry a nav
    // origin, but only down on the z=-134 floor 128u below the surface.
    const float ElevatedMinRise = 20f;
    // A standing jump clears ~55u (Valve's mapper reference: a player jump
    // reaches a 54-55u block); tucking the legs on the way up - the ordinary
    // crouch-jump every player uses to mount a crate - buys roughly another
    // 10u. 65u is that ceiling, and it is the line between "one player can
    // reproduce this alone" and "this needs a teammate boost", which is not a
    // lineup at all. de_mirage's [-2192,-672] platform sits at +59u: a normal
    // crouch-jump, and excluded outright by a standing-jump-only limit.
    const float ElevatedMaxRise = 65f;
    // How far outside a nav area the elevated surface may sit and still be
    // steppable/jumpable onto from it.
    const float ElevatedReach = 48f;

    // Enough of the 32x32 player hull is supported at this height to stand on.
    // A single free-over-solid column only means SOMETHING is underfoot: the
    // top of a railing post, a lamp bracket or a 16u sliver of trim all pass it
    // while being impossible to stand on, and each one would cost the sweep a
    // full set of throw simulations. Requiring half the 3x3 neighbourhood to
    // share the same floor keeps genuine crate and platform tops (including
    // standing near an edge, which is normal) and drops the slivers.
    static bool FitsPlayerHull(VoxelGrid grid, int cx, int cy, int k)
    {
        var supported = 0;
        for (var dx = -1; dx <= 1; dx++)
        {
            for (var dy = -1; dy <= 1; dy++)
            {
                int nx = cx + dx, ny = cy + dy;
                if (grid.InBounds(nx, ny, k) &&
                    grid.IsSolid(grid.Index(nx, ny, k - 1)) &&
                    !grid.IsSolid(grid.Index(nx, ny, k)))
                {
                    supported++;
                }
            }
        }
        return supported >= 5;
    }

    static void AddElevatedOrigins(
        VoxelGrid grid,
        IReadOnlyList<float[][]> areaCorners,
        Vector3 min,
        Vector3 max,
        float sampleStep,
        TriangleCollider? collider,
        List<Vector3> origins)
    {
        var seen = new HashSet<(int, int, int)>();
        var elevated = new List<Vector3>();
        foreach (var corners in areaCorners)
        {
            var avgZ = corners.Average(c => c[2]);
            if (avgZ < min.Z || avgZ > max.Z)
            {
                continue;
            }
            var minX = MathF.Max(corners.Min(c => c[0]) - ElevatedReach, min.X);
            var maxX = MathF.Min(corners.Max(c => c[0]) + ElevatedReach, max.X);
            var minY = MathF.Max(corners.Min(c => c[1]) - ElevatedReach, min.Y);
            var maxY = MathF.Min(corners.Max(c => c[1]) + ElevatedReach, max.Y);

            for (var x = MathF.Ceiling(minX / sampleStep) * sampleStep; x <= maxX; x += sampleStep)
            {
                for (var y = MathF.Ceiling(minY / sampleStep) * sampleStep; y <= maxY; y += sampleStep)
                {
                    var (cx, cy, _) = grid.CellOf(new Vector3(x, y, avgZ));
                    // Widened by a voxel at each end because the grid can only
                    // place a floor on a cell boundary - up to a full voxel
                    // above the real surface. The precise rise test below runs
                    // on the OnSurface-snapped height instead, so this only has
                    // to be loose enough not to miss the candidate: de_mirage's
                    // +59u platform quantizes to a +67u voxel floor and was
                    // dropped outright by an unwidened scan.
                    var (_, _, kLo) = grid.CellOf(new Vector3(x, y, avgZ + ElevatedMinRise - grid.VoxelSize));
                    var (_, _, kHi) = grid.CellOf(new Vector3(x, y, avgZ + ElevatedMaxRise + grid.VoxelSize));
                    kLo = Math.Max(kLo, 1);
                    kHi = Math.Min(kHi, grid.Nz - 6);
                    if (!grid.InBounds(cx, cy, kLo))
                    {
                        continue;
                    }
                    for (var k = kLo; k <= kHi; k++)
                    {
                        // Standing room: floor below, free here, and a player's
                        // height of clearance above.
                        if (!grid.IsSolid(grid.Index(cx, cy, k - 1)) || grid.IsSolid(grid.Index(cx, cy, k)))
                        {
                            continue;
                        }
                        var headroom = true;
                        for (var h = 1; h <= 5 && headroom; h++)
                        {
                            headroom = !grid.IsSolid(grid.Index(cx, cy, k + h));
                        }
                        if (!headroom)
                        {
                            continue;
                        }
                        if (!FitsPlayerHull(grid, cx, cy, k))
                        {
                            continue;
                        }
                        var floorZ = grid.CellCenter(cx, cy, k).Z - grid.VoxelSize / 2;
                        // The voxel says a floor is under this cell, but a cell
                        // is 16u wide and this sample is a point: on a ledge
                        // EDGE the cell is solid because part of it covers the
                        // ledge while the sample itself hangs out over the drop.
                        // OnSurface cannot catch that - its raycast finds
                        // nothing and it hands back the unsnapped position, so
                        // the origin ends up floating in mid air (measured on
                        // de_dust2 [-2044,504]: feet at z=80 with the nearest
                        // real floor at z=22). Demand an actual player-standable
                        // surface at the snapped height instead of assuming one.
                        if (collider is null)
                        {
                            continue;
                        }
                        var probeTop = new Vector3(x, y, floorZ + grid.VoxelSize);
                        var probeBottom = new Vector3(x, y, floorZ - grid.VoxelSize);
                        if (collider.FirstHit(probeTop, probeBottom) is not { } floorHit ||
                            floorHit.Normal.Z < StandableNormalZ)
                        {
                            continue;
                        }
                        var feet = new Vector3(x, y, float.Lerp(probeTop.Z, probeBottom.Z, floorHit.T));
                        var rise = feet.Z - avgZ;
                        if (rise < ElevatedMinRise || rise > ElevatedMaxRise)
                        {
                            continue;
                        }
                        if (seen.Add(((int)MathF.Round(x / 8f), (int)MathF.Round(y / 8f), k)))
                        {
                            elevated.Add(feet);
                        }
                        break; // the lowest reachable surface in this column is the one stood on
                    }
                }
            }
        }
        origins.AddRange(elevated);
    }

    // The CS2 player hull is 32x32; feet pressed against a wall sit exactly
    // 16u from its plane. That is what makes pinned positions valuable: the
    // wall places the player, not the player's eye.
    const float PlayerHalfWidth = 16f;
    // How far from a nav sample a wall still counts as "walk into it" range.
    const float WallProbeRange = 64f;
    // A surface steeper than this is a wall for pinning purposes (the grenade
    // sim's floor test is normal.Z > 0.7; walls are the near-vertical rest).
    const float WallNormalMaxZ = 0.35f;
    // Two probe heights, both above floor trim: waist (36) catches full walls
    // and the clip brushes laid along most railings; the second (46) catches
    // the top bar of an unclipped railing whose sparse balusters a single
    // waist ray can slip between.
    static readonly float[] ProbeHeights = [36f, 46f];
    static readonly Vector3[] ProbeDirs = [.. Enumerable.Range(0, 8)
        .Select(i => new Vector3(MathF.Cos(i * MathF.PI / 4f), MathF.Sin(i * MathF.PI / 4f), 0f))];

    // The wall planes within walk-into range of `feet`, deduped across probe
    // rays and heights: shared by pinned-origin generation and the pin
    // classifier so the two can never disagree about what counts as a wall.
    static List<(Vector2 N, float PlaneD)> NearbyWallPlanes(TriangleCollider collider, Vector3 feet, float range)
    {
        var walls = new List<(Vector2 N, float PlaneD)>();
        foreach (var height in ProbeHeights)
        {
            var probe = feet + new Vector3(0, 0, height);
            foreach (var dir in ProbeDirs)
            {
                if (collider.FirstHit(probe, probe + dir * range) is not { } hit ||
                    MathF.Abs(hit.Normal.Z) > WallNormalMaxZ)
                {
                    continue;
                }
                var n = new Vector2(hit.Normal.X, hit.Normal.Y);
                if (n.Length() < 0.8f)
                {
                    continue;
                }
                n = Vector2.Normalize(n);
                var hitPoint = probe + dir * (hit.T * range);
                // Plane in Hesse form n.x = d; the pinned position satisfies
                // n.x = d + hull half-width.
                var d = Vector2.Dot(n, new Vector2(hitPoint.X, hitPoint.Y));
                if (walls.Any(w => Vector2.Dot(w.N, n) > 0.9f))
                {
                    continue; // same wall seen from a neighboring probe
                }
                walls.Add((n, d));
            }
        }
        return walls;
    }

    /// <summary>
    /// Positions a player reaches by walking INTO geometry: feet pressed flat
    /// against one wall, or wedged into the corner where two meet. The grid
    /// sampling above lands on multiples of the step and misses these, yet they
    /// are the easiest real-world lineups to reproduce - the wall removes the
    /// player's position error entirely, leaving only aim.
    /// </summary>
    /// <summary>
    /// Adds wall- and corner-pinned variants of the given origins in place, for
    /// callers that built their origin list some other way.
    /// </summary>
    public static void AddPinnedOriginsTo(VoxelGrid grid, TriangleCollider collider, List<Vector3> origins, List<Vector3>? crouchOnlyOut = null) =>
        AddPinnedOrigins(grid, collider, origins, crouchOnlyOut);

    static void AddPinnedOrigins(VoxelGrid grid, TriangleCollider collider, List<Vector3> origins, List<Vector3>? crouchOnlyOut = null)
    {
        var seen = new HashSet<(int, int)>(origins.Select(o => ((int)MathF.Round(o.X / 4f), (int)MathF.Round(o.Y / 4f))));
        var pinned = new List<Vector3>();

        void TryAdd(Vector3 baseFeet, Vector2 xy)
        {
            if (Vector2.Distance(xy, new Vector2(baseFeet.X, baseFeet.Y)) > WallProbeRange + PlayerHalfWidth ||
                !seen.Add(((int)MathF.Round(xy.X / 4f), (int)MathF.Round(xy.Y / 4f))))
            {
                return;
            }
            var snapped = SnapToGround(grid, collider, new Vector3(xy.X, xy.Y, baseFeet.Z));
            // The snap is voxel-driven and SnapToGround hands back the position
            // unchanged when its ray finds nothing, so a pin taken from a spot
            // near a ledge edge can end up hanging in mid air - the same 16u
            // cell-vs-point mismatch that put elevated origins in space
            // (measured on de_dust2 [-1402,2742], a floating pin master ships
            // today). Re-seat the pin on the real floor rather than only
            // rejecting it: the probe reaches further down than up because the
            // failure mode is feet left ABOVE the surface, and simply dropping
            // everything that misses a tight window discarded four perfectly
            // good origins whose floor sat just outside it.
            var probeTop = snapped + new Vector3(0, 0, grid.VoxelSize);
            var probeBottom = snapped - new Vector3(0, 0, grid.VoxelSize * 2);
            if (collider.FirstHit(probeTop, probeBottom) is not { } floor ||
                floor.Normal.Z < StandableNormalZ)
            {
                return;
            }
            snapped = snapped with { Z = float.Lerp(probeTop.Z, probeBottom.Z, floor.T) };
            // A pin is placed 16u off ONE wall plane, which says nothing about
            // the rest of the geometry around it - a second wall, a railing or
            // clutter can leave the player hull with nowhere to be. Measured on
            // the live solver: every unstandable origin left in the output came
            // from here, none from the hull-derived set. Hold pins to the same
            // bar as everything else.
            var stance = StandSpots.StanceAt(collider, snapped);
            if (stance == StandSpots.Stance.None)
            {
                return;
            }
            // Sanity-check torso height, not ankle height: a floor plane lying
            // exactly on a voxel boundary marks BOTH neighboring cells solid,
            // so a probe half a voxel up would reject valid floor positions.
            var (cx, cy, cz) = grid.CellOf(snapped + new Vector3(0, 0, grid.VoxelSize * 1.5f));
            if (grid.InBounds(cx, cy, cz) && !grid.IsSolid(grid.Index(cx, cy, cz)))
            {
                pinned.Add(snapped);
                if (stance == StandSpots.Stance.Crouching)
                {
                    // A pin can wedge feet under a soffit or vent the lattice
                    // never reaches; the caller's crouch-only filter needs to
                    // know a standing release from here is not a real throw.
                    crouchOnlyOut?.Add(snapped);
                }
            }
        }

        foreach (var feet in origins.ToArray())
        {
            var walls = NearbyWallPlanes(collider, feet, WallProbeRange);
            foreach (var (n, d) in walls)
            {
                var feetXy = new Vector2(feet.X, feet.Y);
                var dist = Vector2.Dot(n, feetXy) - d;
                if (dist > PlayerHalfWidth + 0.5f)
                {
                    TryAdd(feet, feetXy - n * (dist - PlayerHalfWidth));
                }
            }
            for (var i = 0; i < walls.Count; i++)
            {
                for (var j = i + 1; j < walls.Count; j++)
                {
                    var (a, da) = walls[i];
                    var (b, db) = walls[j];
                    // Solve for the point 16u off BOTH planes - the corner wedge.
                    var det = a.X * b.Y - a.Y * b.X;
                    if (MathF.Abs(Vector2.Dot(a, b)) > 0.5f || MathF.Abs(det) < 0.3f)
                    {
                        continue; // not corner-like
                    }
                    var ra = da + PlayerHalfWidth;
                    var rb = db + PlayerHalfWidth;
                    TryAdd(feet, new Vector2((ra * b.Y - rb * a.Y) / det, (rb * a.X - ra * b.X) / det));
                }
            }
        }
        origins.AddRange(pinned);
    }

    /// <summary>
    /// A user-named stand spot, taken literally: the seed itself ground-snapped,
    /// plus its wall/corner-pinned variants. The sampling lattice above tests
    /// positions NEAR a click; a known lineup lives at ITS feet, up to half a
    /// grid step away from every lattice point, so the click must be tested
    /// as-is or the exact lineup the player asked about can never be found.
    /// </summary>
    /// <summary>
    /// The one origin a player standing at <paramref name="seed"/> actually
    /// occupies - snapped to the ground under it and checked against the player
    /// hull - or empty when nobody can stand there.
    /// </summary>
    // Deliberately without the pinned variants of ExactOriginWithPins: an
    // "exactly here" solve that quietly substitutes a spot against the nearby
    // wall answers a different question, and the answer would not work from
    // where the player is standing.
    /// <summary>
    /// The height a player's feet rest at over <paramref name="at"/>: the
    /// highest surface under the whole 32x32 hull footprint, not under its
    /// centre alone.
    /// </summary>
    // A single downward ray answers for one point, and a player is a box. On
    // any stepped or sloped ground the hull sits on the highest thing beneath
    // it, so a centre-only drop reports a floor BELOW the one the player is
    // standing on - measured against 15 real de_dust2 spawn positions, low by
    // 1.2u at the median and 5.3u at worst, which is enough to teleport
    // whoever pastes the setpos into the ground.
    public static float? FloorUnderHull(TriangleCollider collider, Vector3 at, float up, float down)
    {
        const float half = StandSpots.HullHalfWidth;
        float? best = null;
        foreach (var (dx, dy) in new[] { (0f, 0f), (half, half), (half, -half), (-half, half), (-half, -half) })
        {
            var from = new Vector3(at.X + dx, at.Y + dy, at.Z + up);
            var to = new Vector3(at.X + dx, at.Y + dy, at.Z - down);
            if (collider.FirstHit(from, to) is { } hit)
            {
                var z = from.Z + (to.Z - from.Z) * hit.T;
                if (best is null || z > best)
                {
                    best = z;
                }
            }
        }
        return best;
    }

    // How far above and below a foot position to look for the floor holding it
    // up. A few units either way: enough to survive the sub-unit gap the engine
    // keeps between feet and floor, tight enough that a point in mid-air fails.
    const float SupportProbe = 4f;

    /// <summary>
    /// The feet height the player hull actually rests at in this column,
    /// nearest to <paramref name="seed"/>'s own, or null when no floor within a
    /// step of it holds the hull up.
    /// </summary>
    // A point ray finds the floor under the column's centre; the hull rests
    // on the HIGHEST floor point under its whole 32x32 footprint. On a slope
    // the two differ by up to a unit, and the hull test - which has half a
    // unit of skin - then rejects the click as "inside the floor". That is
    // exactly what happened to a real getpos taken wedged against a crate on
    // de_dust2's sloped A-site ground: the spot a player was standing on
    // came back "no stand spots in range".
    static Vector3? HullRestHeight(TriangleCollider collider, Vector3 seed)
    {
        var heights = StandSpots.SupportedHeights(
            collider, seed.X, seed.Y, seed.Z - StandSpots.StepHeight, seed.Z + StandSpots.StepHeight + StandSpots.StandingHeight);
        float? best = null;
        foreach (var h in heights)
        {
            if (MathF.Abs(h - seed.Z) <= StandSpots.StepHeight && (best is not { } b || MathF.Abs(h - seed.Z) < MathF.Abs(b - seed.Z)))
            {
                best = h;
            }
        }
        return best is { } z ? seed with { Z = z } : null;
    }

    public static List<Vector3> ExactOriginOnly(VoxelGrid grid, TriangleCollider? collider, Vector3 seed, List<Vector3>? crouchOnlyOut = null)
    {
        if (collider == null)
        {
            return [SnapToGround(grid, collider, seed)];
        }
        // The given position first, but only when a player could actually be
        // standing there: the hull fits AND the floor is under it. SnapToGround
        // re-derives a floor from the voxel grid, and on a spot the grid reads
        // differently from the hull test it can land somewhere nobody can
        // stand - which rejected a position that a moment earlier had produced
        // three lineups. Without the support half of the test, though, anything
        // in mid-air is accepted as given: a spawn entity sits 55u above
        // de_dust2's T spawn floor, and every lineup from it was solved from
        // where nobody is standing.
        var supported = collider.FirstHit(
            seed + new Vector3(0, 0, SupportProbe), seed - new Vector3(0, 0, SupportProbe)) != null;
        var candidates = new List<Vector3>();
        if (supported)
        {
            candidates.Add(seed);
        }
        if (HullRestHeight(collider, seed) is { } resting)
        {
            candidates.Add(resting);
        }
        candidates.Add(SnapToGround(grid, collider, seed));
        foreach (var candidate in candidates)
        {
            var stance = StandSpots.StanceAt(collider, candidate);
            if (stance == StandSpots.Stance.None)
            {
                continue;
            }
            if (stance == StandSpots.Stance.Crouching)
            {
                crouchOnlyOut?.Add(candidate);
            }
            return [candidate];
        }
        return [];
    }

    public static List<Vector3> ExactOriginWithPins(VoxelGrid grid, TriangleCollider? collider, Vector3 seed, List<Vector3>? crouchOnlyOut = null)
    {
        var snapped = SnapToGround(grid, collider, seed);
        if (collider == null)
        {
            return [snapped];
        }
        var list = new List<Vector3>();
        // Taking the click literally is the point of this path, but a click a
        // few units from a wall names a spot the player hull cannot occupy - in
        // game they are simply pushed out of it. Handing back a setpos for a
        // position nobody can stand in is worse than saying nothing.
        // Where the hull really rests beats the voxel-derived floor whenever
        // the seed's own height is within a step of a real floor: the grid
        // answer can sit a whole voxel above a sloped floor (a floating origin
        // passes the hull test) or, snapped by a point ray, inside it (see
        // HullRestHeight). A seed further than a step from any floor - a nav
        // height under a crate - keeps the grid's answer.
        if (HullRestHeight(collider, seed) is { } resting &&
            StandSpots.StanceAt(collider, resting) != StandSpots.Stance.None)
        {
            snapped = resting;
        }
        var stance = StandSpots.StanceAt(collider, snapped);
        if (stance != StandSpots.Stance.None)
        {
            list.Add(snapped);
            if (stance == StandSpots.Stance.Crouching)
            {
                crouchOnlyOut?.Add(snapped);
            }
        }
        // The pinned variants are still derived from where they clicked even
        // when that exact spot is unusable - walking into the nearby wall is
        // very often what they were reaching for.
        var pinned = new List<Vector3> { snapped };
        AddPinnedOrigins(grid, collider, pinned, crouchOnlyOut);
        list.AddRange(pinned.Skip(1));
        return list;
    }

    /// <summary>
    /// How geometry pins a lineup's stand spot: 2 = wedged into a corner (both
    /// axes fixed by walking in), 1 = pressed against one wall, 0 = open ground.
    /// </summary>
    public static int PositionPin(TriangleCollider collider, Vector3 feet) =>
        PositionStance(collider, feet).Pin;

    // How far past touching a wall may sit and still count as pressed against
    // it: the hull face is not perfectly flush with a plane it slid along.
    const float TouchSlack = 1.5f;

    // How far out a non-touching wall is still worth reporting. Measured on a
    // real dust2 solve: 110 of 400 lineups sat within 40u of a wall, but past
    // about a hull half-width (16u) the gap is plainly visible and nobody would
    // mistake the spot for a wall press. Inside it they would - and did. So the
    // range is the confusable band, not everything the probe can see.
    const float WallNoticeRange = 16f;

    /// <summary>
    /// The stand spot's relationship to nearby walls: the pin class, and the
    /// gap from the player's shoulder to the nearest wall when it is not
    /// touching one.
    /// </summary>
    // The pin alone answers "is this against something" and drops the number
    // that decides whether a human can reproduce it. Walking into a wall costs
    // nothing and puts your feet exactly right; standing "about eight units
    // off" that same wall is a measurement you cannot make in a round, and the
    // two were indistinguishable - both a spot flush against the wall and one a
    // shoulder's width off it came back as open ground with no wall mentioned
    // at all. The gap travels with the pin so the viewer can say which it is.
    public static (int Pin, float? WallGap) PositionStance(TriangleCollider collider, Vector3 feet)
    {
        var feetXy = new Vector2(feet.X, feet.Y);
        // The hull face sits along the wall normal, not along the probe ray,
        // so "touching" is a point-to-plane distance, not a ray length.
        var walls = NearbyWallPlanes(collider, feet, PlayerHalfWidth + WallNoticeRange);
        var touching = new List<Vector2>();
        float? nearestGap = null;
        foreach (var wall in walls)
        {
            // Distance from the hull's FACE to the wall plane, not from its
            // centre. Signed: negative means the hull would have to be inside
            // the wall to stand here, which is not a position a player can
            // reach and so is never a lineup we should hand out.
            var gap = Vector2.Dot(wall.N, feetXy) - wall.PlaneD - PlayerHalfWidth;
            if (nearestGap is not { } best || gap < best)
            {
                nearestGap = gap;
            }
            if (gap <= TouchSlack)
            {
                touching.Add(wall.N);
            }
        }
        if (touching.Count >= 2 &&
            touching.Any(a => touching.Any(b => a != b && MathF.Abs(Vector2.Dot(a, b)) < 0.7f)))
        {
            return (2, nearestGap);
        }
        return (touching.Count > 0 ? 1 : 0, nearestGap);
    }

    /// <summary>
    /// Ground z for a CLICKED point, tolerating the slivers between nav areas.
    /// </summary>
    // Exact containment is right for deciding whether a position is walkable,
    // but wrong for interpreting a click. Nav polygons do not tile the floor
    // perfectly, and a click that lands in a sliver between two of them has no
    // containing area - so the caller falls through to a top-down geometry
    // scan, which finds the ROOF. That put de_dust2's BombsiteA and BombsiteB
    // ~900u in the air and returned zero lineups for the two most-thrown-at
    // spots on the map, and did the same to de_cache's Heaven and BombsiteB.
    //
    // Distance is measured to the polygon's edges, not to its centre: a click
    // just off the side of a big area belongs to that area, not to a small one
    // whose middle happens to be nearer.
    public static float? NavGroundZNearby(IReadOnlyList<float[][]> areaCorners, float x, float y)
    {
        var inside = NavGroundZ(areaCorners, x, y);
        if (inside is not null)
        {
            return inside;
        }
        float? best = null;
        var bestDistance = NavGapReach;
        foreach (var corners in areaCorners)
        {
            var d = DistanceToPolygon(corners, x, y);
            if (d < bestDistance)
            {
                bestDistance = d;
                best = corners.Average(c => c[2]);
            }
        }
        return best;
    }

    /// <summary>
    /// Walkable height at a point, accepting an area within
    /// <paramref name="maxDistance"/> rather than the default gap reach.
    /// </summary>
    // Callers that must stay ON the playable surface (the mesh diff) need a
    // tighter rule than NavGroundZNearby's 96u gap-bridging, which reaches out
    // over railings into structures nobody stands on.
    public static float? NavGroundZWithin(IReadOnlyList<float[][]> areaCorners, float x, float y, float maxDistance)
    {
        var inside = NavGroundZ(areaCorners, x, y);
        if (inside is not null)
        {
            return inside;
        }
        float? best = null;
        var bestDistance = maxDistance;
        foreach (var corners in areaCorners)
        {
            var d = DistanceToPolygon(corners, x, y);
            if (d < bestDistance)
            {
                bestDistance = d;
                best = corners.Average(c => c[2]);
            }
        }
        return best;
    }

    /// <summary>
    /// Every distinct walkable height stacked over one 2D point, lowest first.
    /// </summary>
    // A top-down click is ambiguous wherever a map stacks levels: on de_nuke a
    // point over the A bombsite is also over B, and over the roof above both.
    // NavGroundZ answers with the lowest, which is the safe default but silently
    // sends a click meant for the site to the floor below it. Callers that can
    // ask the user (the viewer) need the whole set to offer that choice; the
    // separation threshold keeps a sloped or slightly-stepped single floor from
    // reading as several levels.
    // 128u apart to count as a separate level: that clears a standing player plus
    // headroom, so a ramp, a step or a stack of crates stays one level while a
    // real floor above another reads as two.
    public static List<float> NavGroundLevels(IReadOnlyList<float[][]> areaCorners, float x, float y, float separation = 128f, bool strict = false)
    {
        var heights = new List<float>();
        foreach (var corners in areaCorners)
        {
            if (PointInPolygon(corners, x, y))
            {
                heights.Add(corners.Average(c => c[2]));
            }
        }
        // The 96u gap reach bridges the slivers between adjacent nav quads,
        // which is what origin generation needs. Asking "what is stacked under
        // this pixel" it answers with the floor BESIDE the click as well:
        // standing on de_dust2's Xbox, the mid floor is a few units away
        // horizontally and 159u down, so a click on top of the crate was
        // offered a second "floor" that is not under it and cannot hold a
        // smoke. Reached for only when nothing contains the point at all.
        if (!strict || heights.Count == 0)
        {
            foreach (var corners in areaCorners)
            {
                if (!PointInPolygon(corners, x, y) && DistanceToPolygon(corners, x, y) < NavGapReach)
                {
                    heights.Add(corners.Average(c => c[2]));
                }
            }
        }
        heights.Sort();
        var levels = new List<float>();
        foreach (var z in heights)
        {
            if (levels.Count == 0 || z - levels[^1] > separation)
            {
                levels.Add(z);
            }
        }
        return levels;
    }

    static float DistanceToPolygon(float[][] corners, float x, float y)
    {
        var best = float.MaxValue;
        for (int i = 0, j = corners.Length - 1; i < corners.Length; j = i++)
        {
            var (ax, ay) = (corners[j][0], corners[j][1]);
            var (bx, by) = (corners[i][0], corners[i][1]);
            var (ex, ey) = (bx - ax, by - ay);
            var lenSq = ex * ex + ey * ey;
            var t = lenSq > 0 ? Math.Clamp(((x - ax) * ex + (y - ay) * ey) / lenSq, 0f, 1f) : 0f;
            var (px, py) = (ax + ex * t, ay + ey * t);
            best = MathF.Min(best, MathF.Sqrt((x - px) * (x - px) + (y - py) * (y - py)));
        }
        return best;
    }

    // How far outside a nav area a point may sit and still take that area's
    // height. Nav polygons do not tile the floor perfectly - they leave slivers
    // between neighbours, and a click can land in one. Falling through to a
    // top-down geometry scan there picks the ROOF instead of the floor, which
    // put de_dust2's BombsiteA and BombsiteB targets ~900u in the air and
    // returned zero lineups for the two most-thrown-at spots on the map.
    // Comfortably wider than the slivers, far narrower than a room.
    public const float NavGapReach = 96f;

    /// <summary>
    /// Ground z for a 2D point from the nav mesh: the walkable surface a player
    /// (and therefore a smoke target) would be on. A top-down geometry scan would
    /// pick roofs and arches instead. With stacked walkable areas the lowest wins.
    /// </summary>
    public static float? NavGroundZ(IReadOnlyList<float[][]> areaCorners, float x, float y)
    {
        float? best = null;
        foreach (var corners in areaCorners)
        {
            if (PointInPolygon(corners, x, y))
            {
                var z = corners.Average(c => c[2]);
                if (best == null || z < best)
                {
                    best = z;
                }
            }
        }
        return best;
    }

    static bool PointInPolygon(float[][] corners, float x, float y) =>
        StandSpots.PointInPolygon(corners, x, y);

    static Vector3 SnapToGround(VoxelGrid grid, TriangleCollider? collider, Vector3 p)
    {
        var (x, y, z) = grid.CellOf(p + new Vector3(0, 0, 40));
        if (!grid.InBounds(x, y, Math.Clamp(z, 1, grid.Nz - 1)))
        {
            return p;
        }
        z = Math.Clamp(z, 1, grid.Nz - 1);
        for (var k = z; k >= Math.Max(1, z - 8); k--)
        {
            if (!grid.IsSolid(grid.Index(x, y, k)) && grid.IsSolid(grid.Index(x, y, k - 1)))
            {
                var center = grid.CellCenter(x, y, k);
                return OnSurface(grid, collider, new Vector3(p.X, p.Y, center.Z - grid.VoxelSize / 2));
            }
        }
        return p;
    }

    // A player can stand on anything up to Source's 45.57 degree slope limit.
    const float StandableNormalZ = StandSpots.StandableNormalZ;

    /// <summary>
    /// Drops a voxel-derived foot position onto the collision surface underneath it.
    /// </summary>
    // The grid only knows its own 16u cells, so the closest it can put a floor is
    // the cell boundary above it - up to a whole voxel too high. Everywhere else
    // that is close enough, but an origin is a promise: it goes out to the player
    // as a setpos, and the game then drops them onto the real floor. Simulating
    // the throw from the cell boundary therefore models a release up to 16u higher
    // than the one they can actually make, which carries the grenade further and
    // higher than it goes in game - measured 8u too high on a mirage lineup that
    // the sim landed and the player could not.
    static Vector3 OnSurface(VoxelGrid grid, TriangleCollider? collider, Vector3 feet)
    {
        if (collider == null)
        {
            return feet;
        }
        // The cell above the floor is empty by construction, so the first thing a
        // downward ray from its top can hit is the floor itself.
        var from = feet with { Z = feet.Z + grid.VoxelSize };
        var to = feet with { Z = feet.Z - grid.VoxelSize };
        return collider.FirstHit(from, to) is { } hit && hit.Normal.Z >= StandableNormalZ
            ? feet with { Z = float.Lerp(from.Z, to.Z, hit.T) }
            : feet;
    }

    /// <summary>
    /// Feet positions a player can stand at: a free cell over solid ground with
    /// head room, sampled every second column to keep the sweep tractable.
    /// </summary>
    static List<Vector3> FindStandableOrigins(VoxelGrid grid, Vector3 min, Vector3 max, TriangleCollider? collider = null)
    {
        var origins = new List<Vector3>();
        var (x0, y0, z0) = grid.CellOf(min);
        var (x1, y1, z1) = grid.CellOf(max);
        x0 = Math.Max(x0, 0);
        y0 = Math.Max(y0, 0);
        z0 = Math.Max(z0, 1);
        x1 = Math.Min(x1, grid.Nx - 1);
        y1 = Math.Min(y1, grid.Ny - 1);
        z1 = Math.Min(z1, grid.Nz - 6);

        for (var y = y0; y <= y1; y += 2)
        {
            for (var x = x0; x <= x1; x += 2)
            {
                for (var z = z0; z <= z1; z++)
                {
                    if (!grid.IsSolid(grid.Index(x, y, z - 1)) || grid.IsSolid(grid.Index(x, y, z)))
                    {
                        continue;
                    }
                    var headroom = true;
                    for (var h = 1; h <= 5; h++)
                    {
                        if (grid.IsSolid(grid.Index(x, y, z + h)))
                        {
                            headroom = false;
                            break;
                        }
                    }
                    if (!headroom)
                    {
                        continue;
                    }
                    var center = grid.CellCenter(x, y, z);
                    origins.Add(OnSurface(grid, collider, new Vector3(center.X, center.Y, center.Z - grid.VoxelSize / 2)));
                    z += 4;
                }
            }
        }
        return origins;
    }
}

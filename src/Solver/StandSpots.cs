using System.Numerics;
using SmokeSolver.Sim;

namespace SmokeSolver.Solver;

/// <summary>
/// Every position a player can actually stand, derived from the collision mesh
/// with the real player hull instead of inferred from Valve's nav mesh.
/// </summary>
// Two things were wrong with deciding stand spots from nav areas plus point
// raycasts, and they pull in opposite directions:
//
//   * The nav mesh is authored for BOT pathing. Bots never jump onto anything,
//     so crates, platforms and ledges carry no nav area at all and no lineup
//     could ever be thrown from them.
//   * A point raycast is not a player. It slips through the gap between two
//     crates a real hull would bridge, and - worse - it reports "floor here"
//     for a spot whose 32x32 hull is actually hanging off a ledge edge, which
//     is how origins ended up floating in mid air.
//
// Both disappear if the question is asked the way the engine asks it: place
// the actual hull, see whether it fits, see what holds it up, and then see
// whether a player could have got there. That is what this does.
public static class StandSpots
{
    // Source player collision hull: 32x32x72 standing, 32x32x54 crouched.
    public const float HullHalfWidth = 16f;
    public const float StandingHeight = 72f;
    public const float CrouchHeight = 54f;

    // sv_stepsize: how high a player walks up without jumping.
    public const float StepHeight = 18f;

    // sv_standable_normal - the same 45.57 degree limit the grenade sim uses
    // for its floor test.
    public const float StandableNormalZ = GrenadeTrajectory.FloorNormalZ;

    // Apex of a jump, from the engine's own numbers rather than folklore
    // (published figures disagree: 64, 66 and 71 all appear). CS jump impulse
    // is 301.993 u/s against sv_gravity 800, so the feet rise v^2/2g =
    // 301.993^2 / 1600 = 57.0u. Community "you can jump 64-66" figures fold in
    // the crouch tuck below.
    public const float JumpRise = 57f;

    // Tucking the legs mid-air lifts the feet, so a crouch-jump mounts higher
    // than a plain one. The hull delta (72-54=18) would put the ceiling at 75u,
    // but that is the instantaneous peak of the tuck, not a height players
    // actually land and stay on; the figure quoted for mounting a ledge is
    // 64-66. Taking the conservative end of the measured behaviour rather than
    // the theoretical maximum: over-reporting reach invents lineups nobody can
    // execute, which is worse than missing a marginal one.
    // TODO: pin this down with a real in-game reach test - it is the one
    // constant here not derived from an engine value.
    public const float CrouchJumpRise = 66f;

    // Keeps the hull off the very surface it rests on: a box flush with the
    // floor plane reports an intersection with it.
    const float SkinWidth = 0.5f;

    // Vertical clearance used to step past a surface when walking down a
    // column, and the minimum separation between two surfaces treated as
    // distinct. Comfortably thicker than map trim, well under step height.
    const float SurfaceGap = 8f;

    // A drop is only a route to somewhere if the player survives it intact.
    // Source hurts you above PLAYER_MAX_SAFE_FALL_SPEED (580 u/s), which
    // against sv_gravity 800 is a fall of 580^2/(2*800) = 210u. Without this
    // the search walks off the top of de_vertigo and "reaches" every surface
    // on the way down - 89% of that map's spots were the far side of a lethal
    // fall, and none of them are somewhere a player throws a smoke from.
    public const float MaxSafeFallHeight = 210f;

    // How far the search may wander from ground the nav mesh vouches for,
    // counted in lattice steps rather than units so walls and drops count
    // against it properly.
    //
    // Unlike everything above this is a judgement call, not an engine value.
    // Reachability alone is not the same as "in the map": de_fachwerk's
    // decorative countryside and cs_office's roofs are perfectly walkable and
    // genuinely connected to the play area, and they accounted for 74% and 19%
    // of those maps' extra spots - thousands of positions nobody throws a smoke
    // from. The things worth recovering (crates, platforms, ledges, balconies)
    // all sit within a few steps of ground bots already walk, so the search is
    // bounded there. Nav-covered ground resets the budget, so this never cuts
    // the playable area itself short.
    public const int MaxStepsFromNav = 6;

    public enum Stance { None, Standing, Crouching }

    public readonly record struct Spot(Vector3 Feet, Stance Stance, bool NavCovered);

    /// <summary>
    /// Can the player hull occupy this spot, and what stance does it need?
    /// </summary>
    // Standing is tried first because a spot a player can stand on is worth
    // more than one they must crouch on - the eye height, and therefore the
    // whole throw, differs.
    public static Stance StanceAt(TriangleCollider collider, Vector3 feet)
    {
        foreach (var (stance, height) in
                 new[] { (Stance.Standing, StandingHeight), (Stance.Crouching, CrouchHeight) })
        {
            var half = new Vector3(HullHalfWidth - SkinWidth, HullHalfWidth - SkinWidth, height / 2 - SkinWidth);
            var center = feet + new Vector3(0, 0, height / 2);
            if (!collider.BoxIntersects(center, half))
            {
                return stance;
            }
        }
        return Stance.None;
    }

    /// <summary>
    /// Feet heights at this column where a hull-wide footprint finds support,
    /// lowest first.
    /// </summary>
    // Swept with the hull's own 32x32 footprint, not a ray: a player straddling
    // the gap between two crates is held up by both, and one standing at the
    // lip of a ledge is not held up at all. A ray answers neither question.
    public static List<float> SupportedHeights(
        TriangleCollider collider, float x, float y, float zMin, float zMax, int maxSurfaces = 8)
    {
        var heights = new List<float>();
        var probeHalf = new Vector3(HullHalfWidth - SkinWidth, HullHalfWidth - SkinWidth, 0.5f);
        var top = zMax;
        for (var i = 0; i < maxSurfaces && top > zMin; i++)
        {
            var from = new Vector3(x, y, top);
            var to = new Vector3(x, y, zMin);
            // minNormalZ keeps the sweep looking for floor: walls and ceilings
            // are not something feet come to rest on.
            if (collider.FirstHitHull(from, to, probeHalf, StandableNormalZ) is not { } hit)
            {
                break;
            }
            var z = float.Lerp(from.Z, to.Z, hit.T) - probeHalf.Z;
            heights.Add(z);
            // Resume clear of the surface just found, not just under its face.
            // Restarting a 1u-tall probe 1u lower begins it INSIDE that surface,
            // which contacts at t=0 and reports the same floor again a shade
            // lower - a descent that crawls 1.5u per iteration and never reaches
            // the real ground. SurfaceGap also merges floors too close together
            // to be separately standable (a player cannot occupy two surfaces
            // a few units apart anyway).
            top = z - SurfaceGap;
        }
        return heights;
    }

    /// <summary>
    /// Could a player move between two stand spots in one step, jump or drop?
    /// </summary>
    public static bool CanTraverse(TriangleCollider collider, Vector3 from, Vector3 to, Stance toStance)
    {
        var rise = to.Z - from.Z;
        var maxRise = toStance == Stance.Crouching ? CrouchJumpRise : JumpRise;
        if (rise > maxRise || rise < -MaxSafeFallHeight)
        {
            return false;
        }
        // Falling is free, so only a climb needs the body lifted before it can
        // move across; the horizontal move is then tested at whichever height
        // the player actually crosses at.
        var crossZ = MathF.Max(from.Z, to.Z);
        // A walk-up within step height does not lift the player over anything,
        // so it is tested at the destination height; a jump clears the lip.
        var clearance = rise > StepHeight ? SkinWidth * 2 : 0f;
        var height = toStance == Stance.Crouching ? CrouchHeight : StandingHeight;
        var half = new Vector3(HullHalfWidth - SkinWidth, HullHalfWidth - SkinWidth, height / 2 - SkinWidth);
        var a = new Vector3(from.X, from.Y, crossZ + clearance) + new Vector3(0, 0, height / 2);
        var b = new Vector3(to.X, to.Y, crossZ + clearance) + new Vector3(0, 0, height / 2);
        if (collider.FirstHitHull(a, b, half) is not null)
        {
            return false;
        }
        if (rise >= -StepHeight)
        {
            return true;
        }
        // Walking off an edge is a fall, and a fall lands on the FIRST surface
        // under the player - not on whichever lower surface we happen to be
        // testing. Without this the search drops straight through floors into
        // the sealed voids under them: it was reaching spots a median 69u (and
        // up to 274u) below the nav floor at the same spot, none of which a
        // player can ever occupy.
        // Deliberately a point ray and not the hull sweep used elsewhere. A
        // neighbour is only a lattice step away, so a 32-wide hull dropped at
        // the destination still overlaps the ledge being stepped off and
        // "lands" on it instantly, rejecting every legitimate step-down. The
        // falling player's centre is what has to clear, and that is a point.
        // Started just above the lip, not exactly on it: a ray beginning
        // precisely on a floor plane can miss it, which would let the fall pass
        // straight through the very floor being stepped off.
        var dropFrom = new Vector3(to.X, to.Y, crossZ + 1f);
        var dropTo = new Vector3(to.X, to.Y, to.Z - SurfaceGap);
        return collider.FirstHit(dropFrom, dropTo) is { } landing &&
               landing.Normal.Z >= StandableNormalZ &&
               MathF.Abs(float.Lerp(dropFrom.Z, dropTo.Z, landing.T) - to.Z) <= SurfaceGap;
    }

    /// <summary>
    /// Every reachable stand spot in the region, seeded from the nav mesh and
    /// grown outward across the geometry by walking, jumping and dropping.
    /// </summary>
    // Seeding from nav rather than trusting geometry alone is what keeps the
    // result honest: a rooftop or an out-of-bounds shelf is perfectly standable
    // and completely unreachable, and only a connectivity argument can tell the
    // two apart. Nav areas are known-reachable ground, so anything the player
    // hull can walk, jump or fall to from one is reachable too.
    public static List<Spot> Compute(
        TriangleCollider collider,
        IReadOnlyList<float[][]> navAreas,
        Vector3 regionMin,
        Vector3 regionMax,
        float step = 16f,
        Action<int, int>? onProgress = null)
    {
        // Anchored to world multiples of the step rather than to the mesh's
        // arbitrary bounding box, so the lattice is reproducible across maps
        // and lines up with everything else sampled on the same grid.
        var originX = MathF.Ceiling(regionMin.X / step) * step;
        var originY = MathF.Ceiling(regionMin.Y / step) * step;
        var columns = new Dictionary<(int, int), List<Spot>>();
        var nx = (int)MathF.Floor((regionMax.X - originX) / step) + 1;
        var ny = (int)MathF.Floor((regionMax.Y - originY) / step) + 1;

        // Nav areas give both the seeds and the height to match them against.
        var navByCell = new Dictionary<(int, int), List<float>>();
        foreach (var corners in navAreas)
        {
            var z = corners.Average(c => c[2]);
            var minX = corners.Min(c => c[0]);
            var maxX = corners.Max(c => c[0]);
            var minY = corners.Min(c => c[1]);
            var maxY = corners.Max(c => c[1]);
            for (var gx = (int)MathF.Floor((minX - originX) / step); gx <= (int)MathF.Ceiling((maxX - originX) / step); gx++)
            {
                for (var gy = (int)MathF.Floor((minY - originY) / step); gy <= (int)MathF.Ceiling((maxY - originY) / step); gy++)
                {
                    var px = originX + gx * step;
                    var py = originY + gy * step;
                    if (!PointInPolygon(corners, px, py))
                    {
                        continue;
                    }
                    if (!navByCell.TryGetValue((gx, gy), out var list))
                    {
                        navByCell[(gx, gy)] = list = [];
                    }
                    list.Add(z);
                }
            }
        }

        var queue = new Queue<(int X, int Y, int Index, int Depth)>();
        var done = 0;
        for (var gx = 0; gx < nx; gx++)
        {
            for (var gy = 0; gy < ny; gy++)
            {
                var x = originX + gx * step;
                var y = originY + gy * step;
                var spots = new List<Spot>();
                foreach (var z in SupportedHeights(collider, x, y, regionMin.Z, regionMax.Z))
                {
                    var feet = new Vector3(x, y, z);
                    var stance = StanceAt(collider, feet);
                    if (stance == Stance.None)
                    {
                        continue;
                    }
                    var navCovered = navByCell.TryGetValue((gx, gy), out var zs) &&
                                     zs.Any(nz => MathF.Abs(nz - z) <= StepHeight * 2);
                    spots.Add(new Spot(feet, stance, navCovered));
                }
                if (spots.Count > 0)
                {
                    columns[(gx, gy)] = spots;
                    for (var i = 0; i < spots.Count; i++)
                    {
                        if (spots[i].NavCovered)
                        {
                            queue.Enqueue((gx, gy, i, 0));
                        }
                    }
                }
            }
            onProgress?.Invoke(++done, nx);
        }

        var reachable = new HashSet<(int, int, int)>(queue.Select(q => (q.X, q.Y, q.Index)));
        int[] dx = [1, -1, 0, 0, 1, 1, -1, -1];
        int[] dy = [0, 0, 1, -1, 1, -1, 1, -1];
        while (queue.Count > 0)
        {
            var (cx, cy, ci, depth) = queue.Dequeue();
            if (depth >= MaxStepsFromNav)
            {
                continue;
            }
            var from = columns[(cx, cy)][ci].Feet;
            for (var d = 0; d < dx.Length; d++)
            {
                var key = (cx + dx[d], cy + dy[d]);
                if (!columns.TryGetValue(key, out var neighbours))
                {
                    continue;
                }
                for (var i = 0; i < neighbours.Count; i++)
                {
                    if (reachable.Contains((key.Item1, key.Item2, i)))
                    {
                        continue;
                    }
                    var n = neighbours[i];
                    if (!CanTraverse(collider, from, n.Feet, n.Stance))
                    {
                        continue;
                    }
                    reachable.Add((key.Item1, key.Item2, i));
                    // Landing back on ground the nav mesh covers means we are
                    // in the playable area again, so the wander budget resets.
                    queue.Enqueue((key.Item1, key.Item2, i, n.NavCovered ? 0 : depth + 1));
                }
            }
        }

        return [.. reachable.Select(r => columns[(r.Item1, r.Item2)][r.Item3])];
    }

    // Shared with LineupSolver's nav-area sampling and ground lookups: one
    // even-odd test for every "is this 2D point inside this nav polygon" ask.
    public static bool PointInPolygon(float[][] corners, float x, float y)
    {
        var inside = false;
        for (int i = 0, j = corners.Length - 1; i < corners.Length; j = i++)
        {
            var (xi, yi) = (corners[i][0], corners[i][1]);
            var (xj, yj) = (corners[j][0], corners[j][1]);
            if (yi > y != yj > y && x < (xj - xi) * (y - yi) / (yj - yi) + xi)
            {
                inside = !inside;
            }
        }
        return inside;
    }
}

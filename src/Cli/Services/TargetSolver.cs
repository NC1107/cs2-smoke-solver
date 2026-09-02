using System.Numerics;
using SmokeSolver.Extraction;
using SmokeSolver.Sim;
using SmokeSolver.Solver;
using static SmokeSolver.Cli.MeshSetup;

namespace SmokeSolver.Cli;

public static class TargetSolver
{
    // Height above the smoke's resting point used as the far end of the
    // line-of-sight test - roughly a player's eye at the landing, between crouch
    // (46) and stand (64). Lifting off the floor is what stops a landing point
    // from being occluded by its own ground.
    const float DefenderEyeHeight = 55f;

    // How far under a spawn entity to look for the floor it spawns players onto.
    const float SpawnDrop = 512f;

    // A spawn entity is a marker, not a foot position: on de_dust2's T spawn it
    // floats 55u over the floor, and solving from it puts every lineup's feet
    // in mid-air and its setpos inside the ground. Dropped straight down onto
    // the surface the player will land on.
    static Vector3 DropToFloor(TriangleCollider collider, Vector3 p) =>
        LineupSolver.FloorUnderHull(collider, p, GrenadeTrajectory.StandEyeHeight, SpawnDrop) is { } z
            ? new Vector3(p.X, p.Y, z)
            : p;

    // How far from a spawn to accept a validated stand spot in its place.
    const float SpawnSnap = 32f;

    // The precomputed stand spot nearest a point, within a radius. That set
    // knows about crates and ledges the nav mesh omits, and every entry has
    // already been checked against the real player hull.
    static StandSpotOrigin? NearestStandSpot(IReadOnlyList<StandSpotOrigin>? standSpots, Vector2 at, float radius)
    {
        if (standSpots is not { Count: > 0 })
        {
            return null;
        }
        StandSpotOrigin? best = null;
        var bestD = radius;
        foreach (var s in standSpots)
        {
            var d = Vector2.Distance(new Vector2(s.Feet.X, s.Feet.Y), at);
            if (d < bestD)
            {
                bestD = d;
                best = s;
            }
        }
        return best;
    }

    static float? NearestStandSpotZ(IReadOnlyList<StandSpotOrigin>? standSpots, Vector2 at) =>
        NearestStandSpot(standSpots, at, 24f)?.Feet.Z;

    public static TargetSolve SolveForTarget(
        CollisionMesh mesh,
        Func<byte, bool>? attributeFilter,
        List<NavAreaJson> navAreas,
        Vector3 target,
        bool hasTargetZ,
        Vector2? originClickOpt,
        float originReach,
        float tolerance,
        ThrowConstants constants,
        Action<string, int>? onPhase = null,
        Action<Vector3, int>? onOrigin = null,
        Action<Vector3, bool>? onCandidate = null,
        float minStability = 0.4f,
        bool fineScan = false,
        IReadOnlyList<ThrowType>? types = null,
        IReadOnlyList<float>? strengths = null,
        IReadOnlyList<StandSpotOrigin>? standSpots = null,
        // Collision groups this solve treats as gone (shot-out glass, opened
        // doors). The voxel grid gets this through attributeFilter; the exact
        // collider is built from the interactAs-based grenade filter instead,
        // so it has to be told separately or verification would keep bouncing
        // throws off glass the sweep already flew through.
        IReadOnlyList<string>? brokenGroups = null,
        // Where a round starts, so a map-wide sweep grows outward from the
        // spawns as well as from the target instead of reaching the spots a
        // player actually stands on last.
        IReadOnlyList<Vector3>? spawnFronts = null,
        // Every spawn point, and how far from one a throw spot may be, when the
        // search is scoped to spawns: the answer to "what can I throw as the
        // round starts" rather than "what can be thrown from anywhere". Zero
        // radius means the whole map, as before.
        IReadOnlyList<Vector3>? spawnPoints = null,
        float spawnScopeRadius = 0f,
        // Only the spawn positions themselves are throwable from, rather than
        // the walkable area around them.
        bool spawnsOnly = false,
        // One origin and nothing else: the exact spot the player is standing
        // on, with no lattice neighbour and no pinned variant substituted for
        // it. What someone gets when they paste their own getpos and expect the
        // answer to work from where they are actually standing.
        bool exactOrigin = false,
        // The height the caller means, when it has one - a solved throw spot or
        // a pasted setpos both carry their own.
        float? originZ = null)
    {
        var hasOrigin = originClickOpt.HasValue;
        var originClick = originClickOpt ?? new Vector2(target.X, target.Y);
        // Materialized once: three consumers below used to each rebuild this
        // array-of-arrays from the nav list per solve.
        float[][][] corners = [.. navAreas.Select(a => a.Corners)];
        onPhase?.Invoke("prepare", 0);

        var (meshMin, meshMax) = mesh.ComputeBounds();
        var voxelSize = 16f;

        // Resolve the target's height BEFORE sizing the main voxel grid: a 2D
        // click's target.Z defaults to 0, and capping the grid to "near Z=0"
        // (the relative cap below) would silently miss every playable cell on
        // a map whose baseline sits far from zero (de_vertigo's floors are
        // ~11,500-11,800 world Z) - the grid would top out at 900 before any
        // real floor was ever in view. NavGroundZ needs no grid at all; only
        // the raw-geometry fallback does, and only then (a click that misses
        // every nav area) does it pay for a second, full-height probe grid.
        var navZ = hasTargetZ ? null : LineupSolver.NavGroundZNearby(corners, target.X, target.Y);
        if (navZ is { } z0)
        {
            target = target with { Z = z0 };
        }
        else if (!hasTargetZ)
        {
            var probeMin = new Vector3(target.X - 200, target.Y - 200, meshMin.Z);
            var probeMax = new Vector3(target.X + 200, target.Y + 200, meshMax.Z);
            var probeGrid = VoxelGrid.Build(mesh, voxelSize, probeMin, probeMax, attributeFilter);
            var (tx, ty, _) = probeGrid.CellOf(target with { Z = meshMin.Z + 100 });
            // The nearest walkable area at ANY distance: too far to trust as the
            // answer (that is what the 96u NavGroundZNearby above already
            // refused), but it says which of the column's stacked floors the
            // click meant, which is the whole question the scan cannot answer.
            var anchor = LineupSolver.NavGroundZWithin(corners, target.X, target.Y, float.MaxValue);
            target = SnapTargetToGround(probeGrid, tx, ty, anchor) ?? target with { Z = 0 };
        }

        var min = new Vector3(
            MathF.Max(MathF.Min(target.X, originClick.X - originReach) - 500, meshMin.X),
            MathF.Max(MathF.Min(target.Y, originClick.Y - originReach) - 500, meshMin.Y),
            meshMin.Z);
        var max = new Vector3(
            MathF.Min(MathF.Max(target.X, originClick.X + originReach) + 500, meshMax.X),
            MathF.Min(MathF.Max(target.Y, originClick.Y + originReach) + 500, meshMax.Y),
            // Cap relative to the (now-resolved) target height: an absolute
            // world-Z cap silently excluded all playable space on high maps
            // like de_vertigo.
            MathF.Min(meshMax.Z + 64, target.Z + 900));
        var grid = VoxelGrid.Build(mesh, voxelSize, min, max, attributeFilter);

        // The "zone" for a two-click query is simply resting close enough to the target.
        var zoneCrossings = new Dictionary<int, int>();
        var cellRange = (int)MathF.Ceiling(tolerance / voxelSize);
        var (cx, cy, cz) = grid.CellOf(target);
        for (var dz = -1; dz <= cellRange; dz++)
        {
            for (var dy = -cellRange; dy <= cellRange; dy++)
            {
                for (var dx = -cellRange; dx <= cellRange; dx++)
                {
                    int x = cx + dx, y = cy + dy, z = cz + dz;
                    if (!grid.InBounds(x, y, z) || grid.IsSolid(grid.Index(x, y, z)))
                    {
                        continue;
                    }
                    if (Vector3.Distance(grid.CellCenter(x, y, z), target) <= tolerance + voxelSize)
                    {
                        zoneCrossings[grid.Index(x, y, z)] = 1;
                    }
                }
            }
        }

        // Built before the origins, not after: they are snapped onto its triangles
        // so that the spot a lineup names is the spot the player actually stands on.
        var collider = brokenGroups is { Count: > 0 }
            ? BuildGrenadeColliderExcluding(mesh, min, max, brokenGroups)
            : BuildGrenadeCollider(mesh, min, max);
        // Origins model where the PLAYER can be, so their ground snap and wall
        // pin probes run against player-solid geometry: the clip brushes along
        // railings and ledges are exactly what pins feet in game, and they are
        // invisible to the grenade collider by design.
        var playerCollider = BuildPlayerCollider(mesh, min, max);

        // Nowhere for a smoke to come to rest means no throw can possibly
        // qualify, so sweeping every origin would burn a full solve - minutes of
        // CPU - to arrive at the empty list we already have. It used to do
        // exactly that, and say why only in the server's own log, leaving the
        // caller unable to tell a broken target from an unreachable one.
        if (zoneCrossings.Count == 0)
        {
            var why = $"target ({target.X:F0},{target.Y:F0},{target.Z:F0}) has no reachable landing cells - " +
                "it resolved inside solid geometry, or the tolerance is too small";
            Console.Error.WriteLine(why);
            return new TargetSolve(target, 0, [], [], collider, playerCollider, why);
        }

        // Prefer the precomputed hull-derived set when the map has one: it is
        // the only source that knows about crates and ledges (the nav mesh is
        // bot-pathing data and omits them) AND guarantees a real 32x32x72 hull
        // fits, which the voxel grid cannot - a 16u cell straddling a ledge
        // edge reads solid while the point inside it hangs over the drop.
        var origins = standSpots is { Count: > 0 }
            ? standSpots
                .Where(s => Vector2.Distance(new Vector2(s.Feet.X, s.Feet.Y), originClick) <= originReach
                            && s.Feet.Z >= min.Z && s.Feet.Z <= max.Z)
                .Select(s => s.Feet)
                .ToList()
            : LineupSolver.OriginsFromNavAreas(
                    grid,
                    corners,
                    new Vector3(originClick.X - originReach, originClick.Y - originReach, meshMin.Z),
                    new Vector3(originClick.X + originReach, originClick.Y + originReach, max.Z),
                    sampleStep: 24f,
                    collider: playerCollider)
                .Where(o => Vector2.Distance(new Vector2(o.X, o.Y), originClick) <= originReach)
                .ToList();
        var crouchOnlyExtras = new List<Vector3>();
        // Spawn-scoped search. "Only" means literally the spawn positions: the
        // question is what a player can throw from where the round puts them,
        // standing still, so the answer must not quietly include the walkable
        // ground around each one.
        if (spawnsOnly && spawnPoints is { Count: > 0 })
        {
            // Snapped onto the collision surface and hull-checked, not dropped
            // to the nav height. A nav level is the average of a polygon's
            // corners and at de_dust2's T spawn it sits 5u UNDER the real floor
            // - and that height goes out as a setpos, which teleports the
            // player into the ground.
            origins = [];
            foreach (var raw in spawnPoints)
            {
                if (raw.Z < min.Z - SpawnDrop || raw.Z > max.Z)
                {
                    continue;
                }
                var dropped = DropToFloor(playerCollider, raw);
                var exact = LineupSolver.ExactOriginOnly(grid, playerCollider, dropped, crouchOnlyExtras);
                if (exact.Count > 0)
                {
                    origins.AddRange(exact);
                    continue;
                }
                // Half of de_dust2's spawns land on stepped ground, where a
                // 32u hull placed on the exact surface point straddles two
                // heights and fails the fit test - the player is pushed clear
                // of it in game, and a spawn nobody can be solved from is a
                // worse answer than a spawn resolved to the validated stand
                // spot beside it. The lattice is 16u, so "beside it" is a step.
                if (NearestStandSpot(standSpots, new Vector2(dropped.X, dropped.Y), SpawnSnap) is { } spot)
                {
                    origins.Add(spot.Feet);
                    if (spot.Crouched)
                    {
                        crouchOnlyExtras.Add(spot.Feet);
                    }
                }
            }
        }
        else if (spawnScopeRadius > 0f && spawnPoints is { Count: > 0 })
        {
            origins = origins
                .Where(o => spawnPoints.Any(p =>
                    Vector2.Distance(new Vector2(o.X, o.Y), new Vector2(p.X, p.Y)) <= spawnScopeRadius))
                .ToList();
        }

        // Origins added below the stand-spot lattice (pins, the exact click)
        // report their own stance, so a wedge under a vent or soffit joins the
        // crouch-only filter alongside the precomputed crouch-only spots.
        // An exact solve has one origin by definition: pinned variants move the
        // feet to a wall, and the lattice offers a neighbour up to half a step
        // away - both of which answer a different question from "from here".
        if (exactOrigin && hasOrigin)
        {
            // Height comes from the caller when it has one, then from the
            // precomputed stand spots, and only then from the nav mesh. The nav
            // mesh is bot-pathing data that omits crates and ledges, so asking
            // it about a spot on top of one answers with the floor below and
            // the "exact" solve is run from the wrong height entirely - which
            // is how a spot that had just produced three lineups returned none.
            var exactZ = originZ
                ?? NearestStandSpotZ(standSpots, originClick)
                ?? LineupSolver.NavGroundZ(corners, originClick.X, originClick.Y)
                ?? target.Z;
            // Snapped and hull-checked exactly as every other origin is. Handing
            // the sweep a raw point that floats a unit off the floor releases
            // the grenade from the wrong height, and every throw from it fails
            // verification - a spot that had just produced three lineups
            // returned none.
            origins = LineupSolver.ExactOriginOnly(
                grid, playerCollider, new Vector3(originClick.X, originClick.Y, exactZ), crouchOnlyExtras);
        }
        else if (standSpots is { Count: > 0 } && !spawnsOnly)
        {
            // Walking into a wall is still the most reproducible way to place
            // feet exactly, and the lattice never lands on those spots.
            LineupSolver.AddPinnedOriginsTo(grid, playerCollider, origins, crouchOnlyExtras);
        }
        if (hasOrigin && !exactOrigin)
        {
            // The click names the player's exact intended stand spot. Test it
            // literally (and its pinned variants) - the lattice's nearest sample
            // can sit half a grid step away, and for a tight known lineup that
            // is the difference between finding it and not.
            var clickZ = LineupSolver.NavGroundZ(corners, originClick.X, originClick.Y) ?? target.Z;
            origins.AddRange(LineupSolver.ExactOriginWithPins(grid, playerCollider, new Vector3(originClick.X, originClick.Y, clickZ), crouchOnlyExtras));
        }

        // Map-wide searches use a coarser angle grid to stay interactive; a near-click
        // search can afford a fine one. At long range one degree of pitch moves the
        // landing tens of units, so local grids must be fine enough not to step over
        // the tolerance sphere.
        // Fine scan halves the angle lattice: a probe goes 1 -> 0.5 deg, a
        // map-wide sweep 3x4 -> 2x2 - roughly 3x the work, for lineups whose
        // in-zone angle ribbon the normal lattice steps over.
        var (yawStep, pitchStep) = hasOrigin
            ? (fineScan ? (0.5f, 0.5f) : (1f, 1f))
            : (fineScan ? (2f, 2f) : (3f, 4f));
        var coverage = new System.Collections.Concurrent.ConcurrentDictionary<(int X, int Y), int>();
        onPhase?.Invoke("sweep", origins.Count);
        var candidates = LineupSolver.Solve(
            grid, zoneCrossings, min, max,
            types ?? [ThrowType.Stand, ThrowType.Crouch, ThrowType.JumpThrow, ThrowType.CrouchJumpThrow, ThrowType.RunJumpThrow],
            yawStep, pitchStep,
            // A probe is about ONE spot: keep the exact click, its pinned
            // variants, and each lattice neighbor as distinct results instead
            // of collapsing them into one 64u representative.
            dedupeBucketSize: hasOrigin ? 8f : 64f,
            origins: origins, strengths: strengths, constants: constants, coverage: coverage, onOrigin: onOrigin,
            collider: collider, target: target,
            // Ordering only, and only for a map-wide sweep: a one-spot probe
            // has a single origin, so there is no order to grow.
            extraFronts: hasOrigin ? null : spawnFronts);
        onPhase?.Invoke("verify", candidates.Count);
        // The cell zone above is the sweep's recall filter; the promise the
        // user actually made ("within `tolerance` of this point") is enforced
        // here, against each candidate's exact rest point.
        var verified = LineupSolver.VerifyExact(grid, collider, zoneCrossings, candidates, minStability: minStability, constants: constants, onCandidate: onCandidate, aimTarget: target, tolerance: tolerance);

        // Some stand spots only fit the player crouched - under a vent, a stair
        // soffit, a low balcony. A standing or run-jump throw from one of those
        // is not a throw anybody can make, and the sim released it from the
        // standing eye height, 18u above where the grenade would really leave
        // the hand. Keep only the crouched variants there. 0.5% of spots
        // overall, but 4% on cs_office, which is full of them.
        {
            static (int, int, int) Key(Vector3 v) =>
                ((int)MathF.Round(v.X), (int)MathF.Round(v.Y), (int)MathF.Round(v.Z));
            var crouchOnly = crouchOnlyExtras.Select(Key).ToHashSet();
            if (standSpots is { Count: > 0 })
            {
                crouchOnly.UnionWith(standSpots.Where(s => s.Crouched).Select(s => Key(s.Feet)));
            }
            if (crouchOnly.Count > 0)
            {
                verified = [.. verified.Where(l =>
                    !crouchOnly.Contains(Key(l.Feet)) ||
                    l.Type is ThrowType.Crouch or ThrowType.CrouchJumpThrow)];
            }
        }

        // Flag lineups whose throw spot has a clear line of sight to the area
        // the smoke lands in: being visible to that area while throwing is
        // exactly the exposure the smoke is meant to deny, so the ranking
        // (LineupApi) sinks them below concealed throws. The sightline uses the
        // same world-solid mesh/filter as the rest of the solver (grenade-clips
        // are invisible and don't block vision, so they are already excluded), a
        // zero-width ray that must not be voxel-quantized - the exact triangle
        // raycaster, not the grid. Region-bounded to the solved area so the
        // per-lineup scan stays cheap; trivial next to the sweep that just ran.
        //
        // The ray runs eye -> landing lifted to a defender's eye height, NOT to
        // the resting grenade on the floor: a ground point is grazed by its own
        // floor from almost any angle, which flagged even a spot 80u away as
        // concealed. The lifted endpoint is both the mutual-visibility line
        // ("can someone holding this see me") and free of that self-occlusion.
        var sightline = new TriangleRaycaster(mesh, min, max, attributeFilter);
        var lineups = verified.ToArray();
        Parallel.For(0, lineups.Length, Cpu.Bound, i =>
        {
            var l = lineups[i];
            var eye = l.Feet + new Vector3(0, 0, GrenadeTrajectory.EyeHeight(l.Type));
            var landingEye = l.RestPoint + new Vector3(0, 0, DefenderEyeHeight);
            lineups[i] = l with { DirectLos = !sightline.Blocked(eye, landingEye) };
        });

        // Pin class for every evaluated origin, so the viewer's stand-spot heat
        // view can rank corner wedges and wall presses above open ground. Eight
        // short raycasts per origin - trivial next to the sweep that just ran.
        var originPins = new System.Collections.Concurrent.ConcurrentDictionary<(int X, int Y), int>();
        Parallel.ForEach(origins, Cpu.Bound, o =>
            originPins.GetOrAdd(((int)MathF.Round(o.X), (int)MathF.Round(o.Y)), _ => LineupSolver.PositionPin(playerCollider, o)));

        // Distinguishing "nowhere to throw from" from "nowhere that works" is
        // the difference between a bug and an answer, and the two are identical
        // from outside without this.
        var emptyReason = lineups.Length > 0 ? null
            : origins.Count == 0
                ? "no stand spots in range of that throw position - try a wider search, or a spot on the ground"
                : $"none of the {origins.Count} stand spots in range can land a smoke there";

        return new TargetSolve(
            target,
            origins.Count,
            [.. coverage.Select(kv => new[] { kv.Key.X, kv.Key.Y, kv.Value, originPins.GetValueOrDefault((kv.Key.X, kv.Key.Y)) })],
            [.. lineups],
            collider,
            playerCollider,
            emptyReason);
    }

    /// <summary>
    /// Ground height for a 2D point straight from the geometry, for clicks the
    /// nav mesh cannot answer for. <paramref name="expectedZ"/> is the height
    /// the click most likely meant (the nearest walkable area, at any distance);
    /// with one, the surface nearest it wins, otherwise the lowest does.
    /// </summary>
    // Never the TOPMOST surface. Scanning a column down from the sky and taking
    // the first floor it meets is what put de_dust2's BombsiteA and BombsiteB
    // ~900u in the air and returned zero lineups for the two most-thrown-at
    // spots on the map (see NavGapReach). The 96u gap-bridging added then keeps
    // most clicks away from this fallback but does not fix the fallback itself,
    // which is still reached by any click further than that from a nav polygon:
    // sparse-nav interiors, exterior ledges, rooftop-adjacent spots.
    public static Vector3? SnapTargetToGround(VoxelGrid grid, int x, int y, float? expectedZ = null)
    {
        if (x < 0 || x >= grid.Nx || y < 0 || y >= grid.Ny)
        {
            return null;
        }
        float? best = null;
        foreach (var z in Enumerable.Range(1, Math.Max(0, grid.Nz - 2)))
        {
            if (grid.IsSolid(grid.Index(x, y, z)) || !grid.IsSolid(grid.Index(x, y, z - 1)))
            {
                continue;
            }
            var surface = grid.CellCenter(x, y, z).Z - grid.VoxelSize / 2;
            if (best is not { } b)
            {
                best = surface;
            }
            else if (expectedZ is { } want)
            {
                if (MathF.Abs(surface - want) < MathF.Abs(b - want)) { best = surface; }
            }
            // No anchor to judge by: the lowest floor is the one a player stands
            // on, matching NavGroundZ's "with stacked walkable areas the lowest
            // wins". The scan runs upward, so the first hit already is it.
        }
        if (best is not { } z0)
        {
            return null;
        }
        var column = grid.CellCenter(x, y, 0);
        return new Vector3(column.X, column.Y, z0);
    }

    // Where to draw the in-sky aim X: the first surface the aim ray hits, pulled
    // 24u toward the eye so the marker is never buried inside the geometry the
    // player must line their crosshair against.
    public static Vector3 AimReferencePoint(TriangleCollider collider, Vector3 feet, ThrowType type, float pitchDeg, float yawDeg)
    {
        var eye = feet + new Vector3(0, 0, GrenadeTrajectory.EyeHeight(type));
        var dir = GrenadeTrajectory.ForwardFromAngles(pitchDeg, yawDeg);
        var far = eye + dir * 1200f;
        var hit = collider.FirstHit(eye, far);
        var dist = hit is { } h ? MathF.Max(60f, h.T * 1200f - 24f) : 1200f;
        return eye + dir * dist;
    }
}

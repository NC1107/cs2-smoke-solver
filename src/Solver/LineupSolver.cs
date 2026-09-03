using System.Collections.Concurrent;
using System.Numerics;
using SmokeSolver.Sim;

namespace SmokeSolver.Solver;

public sealed record Lineup(
    Vector3 Feet,
    float YawDeg,
    float PitchDeg,
    ThrowType Type,
    Vector3 RestPoint,
    int Bounces,
    float FlightTime,
    int RestCrossings,
    float Stability = 0f,
    float Strength = 1f,
    // Movement-key direction of a running jump throw relative to the facing
    // (see ThrowSpec.RunYawOffsetDeg); 0 for every grounded/W throw.
    float RunYawOffsetDeg = 0f,
    // Position-chaos score: how far the predicted rest moves when the feet
    // shift by a single movement-key tick (0.25u). Sub-unit for well-behaved
    // throws; hundreds of units when a bounce boundary sits inside the
    // shift, which real-world validation showed the aim-only Stability
    // score completely misses.
    float RestScatter = 0f,
    // The thrower has a clear line of sight to where the smoke lands, so anyone
    // holding that area can see them throw it. Filled in by the target solver
    // (a sightline raycast eye->landing), used to penalize the ranking: an
    // exposed throw spot is where the danger is, so a concealed lineup is
    // preferred even at some cost to reliability.
    bool DirectLos = false);

/// <summary>
/// Stage 2 of the inverse solver: sweep standable origins and view angles, keep
/// throws whose grenade comes to rest inside the stage 1 landing zone.
/// Fewer bounces rank higher: bounce-free lineups tolerate constant error best.
/// </summary>
public static partial class LineupSolver
{
    const float YawSpreadDeg = 30f;
    // The pitch sweep's two floors. -65 was the historical cap ("impractical
    // sky-lob") - but real play uses near-vertical drops when standing close to
    // the target, and capping there made those lineups unfindable. -89 stops
    // short of straight up only to keep the launch direction non-degenerate.
    const float StandardPitchFloorDeg = -65f;
    const float SteepPitchFloorDeg = -89f;
    // A -65deg left-click lands ~1470u out (range scales with sin(2*pitch));
    // everything steeper lands closer, so past this distance the steep band
    // cannot reach and is not worth simulating.
    const float SteepLobMaxRange = 1500f;
    // The in-zone region of angle space is a thin ribbon at range (often under a
    // degree thick), so any fixed angle grid aliases into distance bands of false
    // "impossible" origins. Coarse samples that nearly land in-zone seed a local
    // fine sweep at a quarter of the coarse step instead.
    const int MaxRefineSeeds = 8;
    const int RefineHalfSpan = 2;
    static readonly float[] AllStrengths = [1f, 0.5f, 0f];
    // Movement directions a running jump throw is swept with: W, the two
    // diagonals, and pure strafes. Players line these up exactly like W run
    // throws (hold a movement key into the jump), and the carried velocity
    // rotates with the key, so a target unreachable with W is often exactly
    // reachable with A or D. Every other throw type has no run component.
    static readonly float[] RunYawOffsets = [0f, 45f, -45f, 90f, -90f];
    static readonly float[] NoRunOffset = [0f];

    // The free-space path is a lower bound on flight distance, but a real arc
    // curves and bounces over it, so the budget is scaled generously before
    // anything is discarded: this prune exists to remove the impossible, not
    // to trim the difficult.
    const float FreeSpaceBudgetScale = 1.6f;

    // Slack on the vertical-reach prune above: the envelope is derived from
    // drag-free flight off a point launch, while the real throw releases 16u
    // ahead of the eye and the zone is a volume rather than its centre.
    const float VerticalReachMargin = 128f;

    // A throw that ran out the full flight budget never came to rest - its
    // "rest point" is wherever the integrator gave up. The 0.01s slack absorbs
    // float accumulation in the per-tick time sum. Shared by the coarse sweep
    // and VerifyExact, which used to carry inverted copies of this comparison.
    static bool Settled(TrajectoryResult r) =>
        !r.Lost && r.FlightTime < GrenadeTrajectory.MaxFlightSeconds - 0.01f;

    // Loose upper bounds used only to prune hopeless origins; a real measured
    // jumpthrow covers 2286u, so err generously.
    static float MaxRange(ThrowType type) => type switch
    {
        ThrowType.Stand or ThrowType.Crouch => 2000f,
        ThrowType.JumpThrow or ThrowType.CrouchJumpThrow => 2700f,
        _ => 3100f,
    };

    // The first open cell at or above a point, or null when the column is
    // solid for the whole search. Three cells is a body's worth of headroom -
    // past that the point was not standing anywhere.
    static int? FreeCellNear(VoxelGrid grid, Vector3 point)
    {
        var (x, y, z) = grid.CellOf(point);
        for (var dz = 0; dz <= 3; dz++)
        {
            if (!grid.InBounds(x, y, z + dz))
            {
                break;
            }
            var index = grid.Index(x, y, z + dz);
            if (!grid.IsSolid(index))
            {
                return index;
            }
        }
        return null;
    }

    public static List<Lineup> Solve(
        VoxelGrid grid,
        IReadOnlyDictionary<int, int> zoneCrossings,
        Vector3 originMin,
        Vector3 originMax,
        IReadOnlyList<ThrowType> types,
        float yawStepDeg = 2f,
        float pitchStepDeg = 2f,
        // One surviving lineup per bucket of this size. 64u keeps a map-wide
        // list readable, but a single-click probe must keep the user's exact
        // spot distinct from its lattice neighbors - merging them silently
        // replaces the position the user actually asked about with one up to
        // half a bucket away.
        float dedupeBucketSize = 64f,
        IReadOnlyList<Vector3>? origins = null,
        // Click strengths to sweep; a player who knows they want a left-click
        // throw should not pay for simulating the other two from every origin.
        IReadOnlyList<float>? strengths = null,
        ThrowConstants? constants = null,
        ConcurrentDictionary<(int X, int Y), int>? coverage = null,
        Action<Vector3, int>? onOrigin = null,
        TriangleCollider? collider = null,
        // The exact point the user asked for, when there is one. Only used to
        // break ties between candidates in the same origin bucket (see
        // Better); the zone still decides what counts as a hit.
        Vector3? target = null,
        // Extra places the sweep should also grow outward from - in practice
        // the two team spawns. Ordering only; every reachable origin is swept
        // either way.
        IReadOnlyList<Vector3>? extraFronts = null,
        // How many coarse near-misses per type/strength get the fine lattice
        // around them. Eight keeps a map-wide sweep interactive; an exact-spot
        // solve has one origin and refines every one of them.
        int maxRefineSeeds = MaxRefineSeeds,
        // Keep the best candidate per throw kind (type, strength, run
        // direction) in each bucket instead of one per bucket. One per bucket
        // is what keeps a map-wide list readable; for an exact-spot solve it
        // meant the whole answer rode on a single candidate, and when that
        // one failed verification the spot reported nothing even though a
        // different kind of throw from the same feet would have verified.
        bool keepEveryKind = false)
    {
        if (zoneCrossings.Count == 0)
        {
            // A NaN centroid from the division below defeats every distance
            // prune (NaN comparisons are false), so the solver would simulate
            // every angle from every origin and still return nothing.
            return [];
        }
        var zoneCentroid = Vector3.Zero;
        foreach (var cell in zoneCrossings.Keys)
        {
            zoneCentroid += grid.CellCenter(cell);
        }
        zoneCentroid /= zoneCrossings.Count;
        var zoneRadius = 0f;
        foreach (var cell in zoneCrossings.Keys)
        {
            zoneRadius = MathF.Max(zoneRadius, Vector3.Distance(grid.CellCenter(cell), zoneCentroid));
        }

        origins ??= FindStandableOrigins(grid, originMin, originMax, collider);
        var best = new ConcurrentDictionary<(int, int, int), Lineup>();

        // How far every part of the map is from the zone through open air. A
        // throw spot whose shortest free-space path is longer than any throw
        // can travel cannot reach the zone by any angle or bounce, however
        // close it looks in a straight line - lower tunnels sit right under a
        // bombsite, and a lobby is a short line but a long journey from the
        // roof above it. Budgeted at the longest range any type can manage
        // plus a margin, since the flight path is always at least as long as
        // this lower bound.
        var reachBudget = MaxRange(ThrowType.RunJumpThrow) * FreeSpaceBudgetScale;
        var reach = FreeSpaceReach.Build(grid, zoneCrossings.Keys, reachBudget);

        // Drop the hopeless origins before the sweep rather than inside it.
        // Doing it here means the reported progress counts spots that can
        // actually produce a throw, instead of walking tens of thousands of
        // positions the grenade could never leave - the count was honest about
        // the loop and misleading about the work.
        var pathDistances = new Dictionary<Vector3, float>();
        var reachable = new List<Vector3>(origins.Count);
        foreach (var feet in origins)
        {
            var release = feet + new Vector3(0, 0, GrenadeTrajectory.CrouchEyeHeight);
            if ((reach.DistanceFrom(release) ?? reach.DistanceFrom(feet)) is not { } pathDistance)
            {
                coverage?[((int)MathF.Round(feet.X), (int)MathF.Round(feet.Y))] = 0;
                continue;
            }
            pathDistances[feet] = pathDistance;
            reachable.Add(feet);
        }

        // A second and third front, grown from the places a round actually
        // starts. A map-wide sweep watched from the target alone reaches the
        // spots a player is standing on last, which is the wrong end to watch
        // when the question is "what can I throw on the way out of spawn".
        // These only reorder the sweep - what prunes an origin is still its
        // distance from the zone, which is the only one of the three that
        // bounds a throw.
        var orderKeys = new Dictionary<Vector3, float>(pathDistances);
        foreach (var front in extraFronts ?? [])
        {
            // A spawn entity sits at the player's feet, which usually quantises
            // into the solid floor cell underneath; seeding that would flood
            // nothing at all. Lifted to eye height, then upward a cell at a
            // time until the seed is actually in open air.
            if (FreeCellNear(grid, front + new Vector3(0, 0, GrenadeTrajectory.CrouchEyeHeight)) is not { } seed)
            {
                continue;
            }
            var field = FreeSpaceReach.Build(grid, [seed], reachBudget);
            foreach (var feet in reachable)
            {
                var release = feet + new Vector3(0, 0, GrenadeTrajectory.CrouchEyeHeight);
                if ((field.DistanceFrom(release) ?? field.DistanceFrom(feet)) is { } d && d < orderKeys[feet])
                {
                    orderKeys[feet] = d;
                }
            }
        }
        // Swept nearest-first, measured through open air rather than in a
        // straight line, so the search grows outward from the target the way
        // it is watched: the spots most likely to produce a throw resolve
        // first, and the live view fills from the smoke outwards instead of
        // in whatever order the stand spots happen to be stored. Ordering
        // changes nothing about the result - every reachable origin is still
        // swept - only when each one is reported.
        reachable.Sort((a, b) => orderKeys[a].CompareTo(orderKeys[b]));
        origins = reachable;

        // NoBuffering makes the workers pull the next origin one at a time
        // instead of each taking a static slice of the list up front. With the
        // list ordered nearest-first that is what turns the sweep into an
        // actual expanding front: range partitioning had eight workers running
        // eight different radii at once, which is why the live view filled in
        // diagonal stripes rather than growing out of the target. Each item is
        // a whole set of simulations, so per-item hand-out costs nothing next
        // to the work it hands out.
        Parallel.ForEach(Partitioner.Create(origins, EnumerablePartitionerOptions.NoBuffering), Cpu.Bound, feet =>
        {
            var toZone = zoneCentroid - feet;
            var distance = new Vector2(toZone.X, toZone.Y).Length();
            var yawCenter = MathF.Atan2(toZone.Y, toZone.X) * 180f / MathF.PI;
            var hits = 0;

            var pathDistance = pathDistances[feet];

            // Returns how far the rest point missed the zone centroid (squared), or
            // 0 for an in-zone hit, or MaxValue for a lost/expired throw. Hits are
            // recorded into the bucket dictionary as a side effect.
            float Evaluate(Vector3 eye, float yaw, float pitch, ThrowType type, float strength, float runOffset)
            {
                var result = GrenadeTrajectory.Simulate(grid, new ThrowSpec(eye, yaw, pitch, type, strength, runOffset), constants);
                if (!Settled(result))
                {
                    return float.MaxValue;
                }
                var (cx, cy, cz) = grid.CellOf(result.RestPoint);
                if (!grid.InBounds(cx, cy, cz) || !zoneCrossings.TryGetValue(grid.Index(cx, cy, cz), out var crossings))
                {
                    return Vector3.DistanceSquared(result.RestPoint, zoneCentroid);
                }
                hits++;
                var lineup = new Lineup(feet, Normalize(yaw), pitch, type, result.RestPoint, result.Bounces, result.FlightTime, crossings, Strength: strength, RunYawOffsetDeg: runOffset);
                var kind = keepEveryKind
                    ? (int)type * 1000 + (int)MathF.Round(strength * 10f) * 10 + (int)MathF.Round(runOffset / 45f) + 2
                    : 0;
                var key = ((int)MathF.Floor(feet.X / dedupeBucketSize), (int)MathF.Floor(feet.Y / dedupeBucketSize), kind);
                best.AddOrUpdate(key, lineup, (_, current) => Better(lineup, current, target) ? lineup : current);
                return 0f;
            }

            var k = constants ?? ThrowConstants.Default;
            foreach (var type in types)
            {
                var eye = feet + new Vector3(0, 0, GrenadeTrajectory.EyeHeight(type));
                // How far the zone sits above THIS throw's release point. It
                // has to use the same eye height the throw will actually launch
                // from: charging every type the crouch height overstated the
                // climb by 18u for the standing ones, and the prune below is
                // only sound while it errs toward keeping candidates.
                var zoneRise = toZone.Z - GrenadeTrajectory.EyeHeight(type);
                foreach (var runOffset in type is ThrowType.RunJumpThrow ? RunYawOffsets : NoRunOffset)
                {
                    foreach (var strength in strengths ?? AllStrengths)
                    {
                        // Range scales with the square of throw speed.
                        var speedFactor = k.SpeedScale(strength);
                        if (distance > MaxRange(type) * speedFactor * speedFactor)
                        {
                            continue;
                        }
                        // Same range test against the honest distance: the path
                        // the grenade must actually travel, not the line the
                        // wall is in the way of.
                        if (pathDistance > MaxRange(type) * speedFactor * speedFactor * FreeSpaceBudgetScale)
                        {
                            continue;
                        }
                        // Vertical reach. A projectile launched at speed v
                        // cannot pass above the parabola of safety
                        // v^2/2g - g*d^2/2v^2 at horizontal distance d, and a
                        // bounce only ever removes energy, so a zone above that
                        // envelope cannot be reached from here at this power by
                        // any angle or any number of bounces. The launch speed
                        // is overstated on purpose (jump and run velocity added
                        // outright, plus a margin) so the test only ever
                        // discards throws that are impossible, never ones that
                        // are merely hard - it is a prune, not a filter.
                        if (zoneRise > 0f)
                        {
                            var launchSpeed = k.ThrowSpeed * speedFactor
                                + (type is ThrowType.JumpThrow or ThrowType.CrouchJumpThrow or ThrowType.RunJumpThrow ? k.JumpVelocity : 0f)
                                + (type is ThrowType.RunJumpThrow ? k.RunSpeed : 0f);
                            var gravity = GrenadeTrajectory.BaseGravity * k.GravityScale;
                            var apex = launchSpeed * launchSpeed / (2f * gravity);
                            var reachAtDistance = apex - gravity * distance * distance / (2f * launchSpeed * launchSpeed);
                            if (zoneRise > reachAtDistance + VerticalReachMargin)
                            {
                                continue;
                            }
                        }
                        // A near miss is one coarse step's worth of landing displacement
                        // (roughly distance * step in radians) from the zone edge.
                        var reach = zoneRadius + distance * MathF.Max(yawStepDeg, pitchStepDeg) * MathF.PI / 180f;
                        var reachSq = reach * reach;
                        var nearMisses = new List<(float Yaw, float Pitch, float MissSq)>();
                        // Steep lobs (-65 down to -89, i.e. up to nearly straight up)
                        // only ever land close to the thrower - horizontal speed is
                        // cos(pitch) - so they are swept only when the zone is within
                        // plausible steep-lob range. That keeps a map-wide sweep from
                        // paying for angles that physically cannot reach, while a
                        // player standing near the target gets the near-vertical
                        // drop-on-your-own-head lineups real play uses.
                        var pitchFloor = distance <= SteepLobMaxRange * speedFactor * speedFactor
                            ? SteepPitchFloorDeg
                            : StandardPitchFloorDeg;
                        // A lateral run carries the grenade sideways, so the aim yaw
                        // that lands on the zone sits AWAY from the straight line to
                        // it - by up to ~60 degrees for a right-click full strafe.
                        // The deflection depends on pitch (horizontal throw speed is
                        // cos(pitch)), so the swept window spans the deflections at
                        // both pitch extremes plus the usual spread on either side.
                        var shiftShallow = RunYawShiftDeg(k, strength, 0f, runOffset);
                        var shiftSteep = RunYawShiftDeg(k, strength, pitchFloor, runOffset);
                        var yawLo = yawCenter + MathF.Min(shiftShallow, shiftSteep) - YawSpreadDeg;
                        var yawHi = yawCenter + MathF.Max(shiftShallow, shiftSteep) + YawSpreadDeg;
                        for (var yaw = yawLo; yaw <= yawHi; yaw += yawStepDeg)
                        {
                            for (var pitch = StandardPitchFloorDeg; pitch <= 0f; pitch += pitchStepDeg)
                            {
                                var missSq = Evaluate(eye, yaw, pitch, type, strength, runOffset);
                                if (missSq > 0f && missSq <= reachSq)
                                {
                                    nearMisses.Add((yaw, pitch, missSq));
                                }
                            }
                            // Extend downward on the same lattice so the classic range's
                            // samples stay exactly where they always were.
                            for (var pitch = StandardPitchFloorDeg - pitchStepDeg; pitch >= pitchFloor; pitch -= pitchStepDeg)
                            {
                                var missSq = Evaluate(eye, yaw, pitch, type, strength, runOffset);
                                if (missSq > 0f && missSq <= reachSq)
                                {
                                    nearMisses.Add((yaw, pitch, missSq));
                                }
                            }
                        }
                        nearMisses.Sort((a, b) => a.MissSq.CompareTo(b.MissSq));
                        foreach (var (seedYaw, seedPitch, _) in nearMisses.Take(maxRefineSeeds))
                        {
                            // The fine lattice spans the seed's whole coarse Voronoi cell
                            // so ribbons anywhere between coarse samples get sampled.
                            for (var i = -RefineHalfSpan; i <= RefineHalfSpan; i++)
                            {
                                for (var j = -RefineHalfSpan; j <= RefineHalfSpan; j++)
                                {
                                    if (i == 0 && j == 0)
                                    {
                                        continue;
                                    }
                                    Evaluate(eye,
                                        seedYaw + i * yawStepDeg / (2 * RefineHalfSpan),
                                        seedPitch + j * pitchStepDeg / (2 * RefineHalfSpan),
                                        type, strength, runOffset);
                                }
                            }
                        }
                    }
                }
            }
            // Per-origin option count, including zeroes: the heat map view uses
            // "evaluated but impossible" cells to expose sim or geometry gaps.
            coverage?[((int)MathF.Round(feet.X), (int)MathF.Round(feet.Y))] = hits;
            // Fires from parallel workers; subscribers must be thread-safe.
            onOrigin?.Invoke(feet, hits);
        });

        return [.. best.Values.OrderBy(l => l.Bounces).ThenByDescending(l => l.RestCrossings).ThenBy(l => l.FlightTime)];
    }

    /// <summary>
    /// Gates lineups on a stability score: the fraction of slightly perturbed
    /// sphere-cast exact simulations still resting in the zone. The sphere cast
    /// rolls over thin trim (unlike a point trace) and has no voxel inflation, the
    /// two failure modes real throws exposed, so it is trusted as the referee.
    /// </summary>
    /// <summary>
    /// The last resort of an exact-spot solve: every throw kind over a full
    /// angle lattice, each run through the exact simulator, from one origin.
    /// Returns the throws whose exact rest point lands within
    /// <paramref name="tolerance"/> of the target, as candidates for VerifyExact.
    /// </summary>
    // The voxel sweep is a recall filter, and a 16u grid can lose a throw the
    // real geometry allows - a bounce off an edge the grid rounds away, a gap
    // it closes. Map-wide that is an accepted cost. When someone asks "from
    // exactly here, is there ANY way", it is not: with one origin the real
    // simulator can afford the whole lattice, so the answer no longer depends
    // on the approximation having been kind. Hundreds of thousands of exact
    // flights; seconds, in parallel.
    public static List<Lineup> ExhaustiveExactSpot(
        TriangleCollider collider,
        Vector3 feet,
        Vector3 target,
        float tolerance,
        IReadOnlyList<ThrowType> types,
        IReadOnlyList<float>? strengths,
        ThrowConstants? constants,
        float stepDeg = 1f)
    {
        var k = constants ?? ThrowConstants.Default;
        var toTarget = target - feet;
        var yawCenter = MathF.Atan2(toTarget.Y, toTarget.X) * 180f / MathF.PI;
        var kinds = new List<(ThrowType Type, float Strength, float Run)>();
        foreach (var type in types)
        {
            foreach (var run in type is ThrowType.RunJumpThrow ? RunYawOffsets : NoRunOffset)
            {
                foreach (var strength in strengths ?? AllStrengths)
                {
                    kinds.Add((type, strength, run));
                }
            }
        }
        var found = new ConcurrentBag<Lineup>();
        var tolSq = tolerance * tolerance;
        Parallel.ForEach(kinds, Cpu.Bound, kind =>
        {
            var (type, strength, run) = kind;
            var eye = feet + new Vector3(0, 0, GrenadeTrajectory.EyeHeight(type));
            var shiftShallow = RunYawShiftDeg(k, strength, 0f, run);
            var shiftSteep = RunYawShiftDeg(k, strength, SteepPitchFloorDeg, run);
            var yawLo = yawCenter + MathF.Min(shiftShallow, shiftSteep) - YawSpreadDeg;
            var yawHi = yawCenter + MathF.Max(shiftShallow, shiftSteep) + YawSpreadDeg;
            for (var yaw = yawLo; yaw <= yawHi; yaw += stepDeg)
            {
                for (var pitch = SteepPitchFloorDeg; pitch <= 0f; pitch += stepDeg)
                {
                    var r = GrenadeTrajectory.SimulateExact(collider, new ThrowSpec(eye, yaw, pitch, type, strength, run), k);
                    if (!Settled(r))
                    {
                        continue;
                    }
                    var dx = r.RestPoint.X - target.X;
                    var dy = r.RestPoint.Y - target.Y;
                    if (dx * dx + dy * dy <= tolSq)
                    {
                        found.Add(new Lineup(feet, Normalize(yaw), pitch, type, r.RestPoint, r.Bounces, r.FlightTime, 1, Strength: strength, RunYawOffsetDeg: run));
                    }
                }
            }
        });
        // One per kind, the closest: VerifyExact re-aims and probes each, and a
        // thousand near-identical hits from one kind would cost verification
        // time to say the same thing.
        return [.. found
            .GroupBy(l => (l.Type, l.Strength, l.RunYawOffsetDeg))
            .Select(g => g.OrderBy(l => Vector3.DistanceSquared(l.RestPoint, target)).First())];
    }

    public static List<Lineup> VerifyExact(
        VoxelGrid grid,
        TriangleCollider collider,
        IReadOnlyDictionary<int, int> zoneCrossings,
        IEnumerable<Lineup> candidates,
        float minStability = 0.4f,
        ThrowConstants? constants = null,
        Action<Vector3, bool>? onCandidate = null,
        // When set, every lineup is refined to land as close to this exact point
        // as it can while staying stable, instead of keeping whatever aim the
        // coarse sweep nominated (which lands anywhere in the tolerance zone).
        // This is what makes the precision filter able to surface sub-unit
        // lineups. Null keeps the original stability-first behaviour (CLI, tests).
        Vector3? aimTarget = null,
        // When set together with aimTarget, acceptance is the EXACT rest
        // point's distance to the target, not membership in the voxel zone.
        // The voxel zone accepts anywhere inside 16u cells inflated by a
        // voxel, which stretched a "16u" promise to a ~58u envelope - the
        // measured reason solved lineups landed outside the radius the user
        // asked for. The coarse zone stays as the sweep's recall filter; this
        // is the precision gate. Null keeps zone-membership acceptance.
        float? tolerance = null)
    {
        // One perturbation step; also the re-aim lattice pitch, so the rescue
        // search and the stability probes share simulations.
        const float StepDeg = 0.6f;
        const int AimReach = 2;
        (int DYaw, int DPitch)[] offsets = [(0, 0), (-1, 0), (1, 0), (0, -1), (0, 1)];

        var zoneCentroid = Vector3.Zero;
        foreach (var cell in zoneCrossings.Keys)
        {
            zoneCentroid += grid.CellCenter(cell);
        }
        zoneCentroid /= Math.Max(zoneCrossings.Count, 1);

        var verified = new ConcurrentBag<Lineup>();
        Parallel.ForEach(candidates, Cpu.Bound, lineup =>
        {
            var eye = lineup.Feet + new Vector3(0, 0, GrenadeTrajectory.EyeHeight(lineup.Type));
            var cache = new Dictionary<(int, int), TrajectoryResult>();

            TrajectoryResult SimAt(int dYaw, int dPitch)
            {
                if (!cache.TryGetValue((dYaw, dPitch), out var result))
                {
                    result = GrenadeTrajectory.SimulateExact(collider, new ThrowSpec(
                        eye, lineup.YawDeg + dYaw * StepDeg, lineup.PitchDeg + dPitch * StepDeg, lineup.Type, lineup.Strength, lineup.RunYawOffsetDeg), constants);
                    cache[(dYaw, dPitch)] = result;
                }
                return result;
            }

            // The precision gate when the caller supplied one, the voxel zone
            // otherwise. Every acceptance decision below - stability scoring,
            // the aim-window search, the final settle check - goes through
            // this one predicate, so "stable" and "in tolerance" mean the same
            // radius the user was promised.
            bool Accepts(Vector3 restPoint) =>
                aimTarget is { } g && tolerance is { } tol
                    ? WithinTolerance(restPoint, g, tol, grid.VoxelSize)
                    : InZone(grid, zoneCrossings, restPoint);

            float StabilityAround(int cYaw, int cPitch)
            {
                var hits = 0;
                foreach (var (dYaw, dPitch) in offsets)
                {
                    var result = SimAt(cYaw + dYaw, cPitch + dPitch);
                    if (Settled(result) && Accepts(result.RestPoint))
                    {
                        hits++;
                    }
                }
                return (float)hits / offsets.Length;
            }

            int aimYaw, aimPitch;
            float stability;
            if (aimTarget is { } goal)
            {
                // Precision path: refine to the exact target the user picked.
                // Gather every in-zone aim in the window, ordered by how close its
                // exact rest lands to the target (XY, matching the viewer's
                // precision filter), then take the closest one that is itself
                // stable enough. A stable coarse aim is no longer kept just because
                // it is stable - if a neighbouring aim lands nearer the target and
                // holds up, that is the one reported, so genuinely tight lineups
                // surface instead of hiding inside the tolerance zone.
                var inWindow = new List<(float DistSq, int DYaw, int DPitch)>();
                for (var dYaw = -AimReach; dYaw <= AimReach; dYaw++)
                {
                    for (var dPitch = -AimReach; dPitch <= AimReach; dPitch++)
                    {
                        var result = SimAt(dYaw, dPitch);
                        if (!Settled(result) || !Accepts(result.RestPoint))
                        {
                            continue;
                        }
                        var dx = result.RestPoint.X - goal.X;
                        var dy = result.RestPoint.Y - goal.Y;
                        inWindow.Add((dx * dx + dy * dy, dYaw, dPitch));
                    }
                }
                inWindow.Sort((a, b) => a.DistSq.CompareTo(b.DistSq));
                (aimYaw, aimPitch, stability) = (0, 0, -1f);
                foreach (var (_, dYaw, dPitch) in inWindow)
                {
                    var s = StabilityAround(dYaw, dPitch);
                    if (s >= minStability)
                    {
                        (aimYaw, aimPitch, stability) = (dYaw, dPitch, s);
                        break;
                    }
                }
                if (stability < 0f)
                {
                    onCandidate?.Invoke(lineup.Feet, false);
                    return;
                }
            }
            else
            {
                (aimYaw, aimPitch) = (0, 0);
                stability = StabilityAround(0, 0);
                if (stability < minStability)
                {
                    // The voxel sim that nominated this candidate drifts from the exact
                    // sim by tens of units at range, so the exact-sim in-zone window may
                    // sit a degree away. Re-aim to the searched offset whose exact rest
                    // lands in-zone closest to the zone centroid, then re-judge there.
                    var bestScore = float.MaxValue;
                    for (var dYaw = -AimReach; dYaw <= AimReach; dYaw++)
                    {
                        for (var dPitch = -AimReach; dPitch <= AimReach; dPitch++)
                        {
                            var result = SimAt(dYaw, dPitch);
                            if (!Settled(result) || !Accepts(result.RestPoint))
                            {
                                continue;
                            }
                            var score = Vector3.DistanceSquared(result.RestPoint, zoneCentroid);
                            if (score < bestScore)
                            {
                                bestScore = score;
                                (aimYaw, aimPitch) = (dYaw, dPitch);
                            }
                        }
                    }
                    if ((aimYaw, aimPitch) == (0, 0))
                    {
                        onCandidate?.Invoke(lineup.Feet, false);
                        return;
                    }
                    stability = StabilityAround(aimYaw, aimPitch);
                    if (stability < minStability)
                    {
                        onCandidate?.Invoke(lineup.Feet, false);
                        return;
                    }
                }
            }
            var best = SimAt(aimYaw, aimPitch);
            // Bounces and FlightTime used to be left at the values the coarse
            // voxel sweep produced while the rest point was taken from the exact
            // sim, so a lineup could report 4 bounces and 4.4s while the throw it
            // actually describes takes 5 and 4.6s - visible now that the viewer
            // draws the real path, and quietly wrong before that, because the
            // bounce and flight-time filters were sifting on the approximation.
            var settled = Settled(best) && Accepts(best.RestPoint);
            // Position-chaos probe: the aim-window stability above misses
            // throws that are stable in ANGLE but explode when the FEET move
            // one movement tick (a bounce boundary inside 0.25u). In-game
            // validation showed exactly those throws landing hundreds of
            // units off while scoring 100% stability.
            var scatter = 0f;
            if (settled)
            {
                var finalYaw = lineup.YawDeg + aimYaw * StepDeg;
                var finalPitch = lineup.PitchDeg + aimPitch * StepDeg;
                foreach (var (dx, dy) in ((float, float)[])[(0.25f, 0f), (-0.25f, 0f), (0f, 0.25f), (0f, -0.25f)])
                {
                    var probe = GrenadeTrajectory.SimulateExact(collider, new ThrowSpec(
                        eye + new Vector3(dx, dy, 0f), finalYaw, finalPitch, lineup.Type, lineup.Strength, lineup.RunYawOffsetDeg), constants);
                    scatter = MathF.Max(scatter, Settled(probe)
                        ? Vector3.Distance(probe.RestPoint, best.RestPoint)
                        : 512f);
                }
            }
            verified.Add(lineup with
            {
                YawDeg = Normalize(lineup.YawDeg + aimYaw * StepDeg),
                PitchDeg = lineup.PitchDeg + aimPitch * StepDeg,
                RestPoint = settled ? best.RestPoint : lineup.RestPoint,
                Bounces = settled ? best.Bounces : lineup.Bounces,
                FlightTime = settled ? best.FlightTime : lineup.FlightTime,
                Stability = stability,
                RestScatter = scatter,
            });
            // Fires from parallel workers; subscribers must be thread-safe.
            onCandidate?.Invoke(lineup.Feet, true);
        });
        return
        [
            .. verified
                .OrderByDescending(l => l.Stability)
                .ThenBy(l => l.Bounces)
                .ThenByDescending(l => l.RestCrossings)
                .ThenBy(l => l.FlightTime),
        ];
    }

    // The promise the tolerance knob makes, applied to the EXACT rest point:
    // within `tolerance` of the target in XY (the plane the user clicks and the
    // viewer's precision filter measures). The vertical band is deliberately
    // looser - a couple of voxels below for the grenade settling onto the
    // surface the target Z was resolved from, and tolerance plus that slack
    // above for crates and steps near the click - because target Z comes from
    // nav data that is itself only voxel-accurate; it mirrors the asymmetric
    // vertical extent the old cell zone had, without its 30-45u of XY slop.
    public static bool WithinTolerance(Vector3 restPoint, Vector3 target, float tolerance, float voxelSize)
    {
        var dx = restPoint.X - target.X;
        var dy = restPoint.Y - target.Y;
        if (dx * dx + dy * dy > tolerance * tolerance)
        {
            return false;
        }
        var dz = restPoint.Z - target.Z;
        return dz >= -(2f * voxelSize) && dz <= tolerance + 2f * voxelSize;
    }

    static bool InZone(VoxelGrid grid, IReadOnlyDictionary<int, int> zoneCrossings, Vector3 restPoint)
    {
        var (x, y, z) = grid.CellOf(restPoint);
        for (var dz = 0; dz <= 1; dz++)
        {
            if (grid.InBounds(x, y, z + dz) && zoneCrossings.ContainsKey(grid.Index(x, y, z + dz)))
            {
                return true;
            }
        }
        return false;
    }

    // Where the resultant horizontal velocity of a running jump throw points,
    // relative to the aim yaw: the throw contributes speed*scale*cos(pitch)
    // along the aim, the run contributes RunSpeed rotated by the movement key.
    // The aim that lands on the zone is the zone bearing MINUS this deflection,
    // hence the negative sign. Zero for W (the vectors are collinear).
    static float RunYawShiftDeg(ThrowConstants k, float strength, float pitchDeg, float runOffsetDeg)
    {
        if (runOffsetDeg == 0f)
        {
            return 0f;
        }
        var horizontal = k.ThrowSpeed * k.SpeedScale(strength) * MathF.Cos(pitchDeg * MathF.PI / 180f);
        var offset = runOffsetDeg * MathF.PI / 180f;
        return -MathF.Atan2(k.RunSpeed * MathF.Sin(offset), horizontal + k.RunSpeed * MathF.Cos(offset)) * 180f / MathF.PI;
    }

    static bool Better(Lineup a, Lineup b, Vector3? target)
    {
        if (a.Bounces != b.Bounces)
        {
            return a.Bounces < b.Bounces;
        }
        // Between equal-bounce candidates at the same origin, prefer the one
        // whose (voxel-sim) rest sits closer to the target: the old
        // bounce-only pick routinely nominated a candidate on the far side of
        // the acceptance zone, and VerifyExact's +/-1.2 degree re-aim window
        // cannot walk a coarse-lattice-sized miss back to the click. The 1u
        // dead band keeps voxel-sim noise from overriding the real tiebreaks.
        if (target is { } t)
        {
            var da = Vector3.DistanceSquared(a.RestPoint, t);
            var db = Vector3.DistanceSquared(b.RestPoint, t);
            if (MathF.Abs(da - db) > 1f)
            {
                return da < db;
            }
        }
        if (a.RestCrossings != b.RestCrossings)
        {
            return a.RestCrossings > b.RestCrossings;
        }
        return a.FlightTime < b.FlightTime;
    }

    static float Normalize(float yaw)
    {
        while (yaw > 180f)
        {
            yaw -= 360f;
        }
        while (yaw < -180f)
        {
            yaw += 360f;
        }
        return yaw;
    }

}

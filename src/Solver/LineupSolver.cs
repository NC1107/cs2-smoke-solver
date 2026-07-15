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
    float RunYawOffsetDeg = 0f);

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
        TriangleCollider? collider = null)
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
        var best = new ConcurrentDictionary<(int, int), Lineup>();

        Parallel.ForEach(origins, Cpu.Bound, feet =>
        {
            var toZone = zoneCentroid - feet;
            var distance = new Vector2(toZone.X, toZone.Y).Length();
            var yawCenter = MathF.Atan2(toZone.Y, toZone.X) * 180f / MathF.PI;
            var hits = 0;

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
                var key = ((int)MathF.Floor(feet.X / dedupeBucketSize), (int)MathF.Floor(feet.Y / dedupeBucketSize));
                best.AddOrUpdate(key, lineup, (_, current) => Better(lineup, current) ? lineup : current);
                return 0f;
            }

            var k = constants ?? ThrowConstants.Default;
            foreach (var type in types)
            {
                var eye = feet + new Vector3(0, 0, GrenadeTrajectory.EyeHeight(type));
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
                        foreach (var (seedYaw, seedPitch, _) in nearMisses.Take(MaxRefineSeeds))
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
    public static List<Lineup> VerifyExact(
        VoxelGrid grid,
        TriangleCollider collider,
        IReadOnlyDictionary<int, int> zoneCrossings,
        IEnumerable<Lineup> candidates,
        float minStability = 0.4f,
        ThrowConstants? constants = null,
        Action<Vector3, bool>? onCandidate = null)
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


            float StabilityAround(int cYaw, int cPitch)
            {
                var hits = 0;
                foreach (var (dYaw, dPitch) in offsets)
                {
                    var result = SimAt(cYaw + dYaw, cPitch + dPitch);
                    if (Settled(result) && InZone(grid, zoneCrossings, result.RestPoint))
                    {
                        hits++;
                    }
                }
                return (float)hits / offsets.Length;
            }

            var (aimYaw, aimPitch) = (0, 0);
            var stability = StabilityAround(0, 0);
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
                        if (!Settled(result) || !InZone(grid, zoneCrossings, result.RestPoint))
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
            var best = SimAt(aimYaw, aimPitch);
            // Bounces and FlightTime used to be left at the values the coarse
            // voxel sweep produced while the rest point was taken from the exact
            // sim, so a lineup could report 4 bounces and 4.4s while the throw it
            // actually describes takes 5 and 4.6s - visible now that the viewer
            // draws the real path, and quietly wrong before that, because the
            // bounce and flight-time filters were sifting on the approximation.
            var settled = Settled(best) && InZone(grid, zoneCrossings, best.RestPoint);
            verified.Add(lineup with
            {
                YawDeg = Normalize(lineup.YawDeg + aimYaw * StepDeg),
                PitchDeg = lineup.PitchDeg + aimPitch * StepDeg,
                RestPoint = settled ? best.RestPoint : lineup.RestPoint,
                Bounces = settled ? best.Bounces : lineup.Bounces,
                FlightTime = settled ? best.FlightTime : lineup.FlightTime,
                Stability = stability,
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

    static bool Better(Lineup a, Lineup b)
    {
        if (a.Bounces != b.Bounces)
        {
            return a.Bounces < b.Bounces;
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

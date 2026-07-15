using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json;
using SmokeSolver.Extraction;
using SmokeSolver.Sim;
using SmokeSolver.Solver;
using static SmokeSolver.Cli.CliParsing;
using static SmokeSolver.Cli.MeshSetup;
using static SmokeSolver.Cli.LineupApi;
using static SmokeSolver.Cli.TargetSolver;
using static SmokeSolver.Cli.ExtractCommand;
using static SmokeSolver.Cli.InfoCommand;
using static SmokeSolver.Cli.SmokeCommand;
using static SmokeSolver.Cli.SightlineCommand;
using static SmokeSolver.Cli.SolveCommand;
using static SmokeSolver.Cli.GroundCommand;
using static SmokeSolver.Cli.LineupsCommand;
using static SmokeSolver.Cli.ViewerDataCommand;
using static SmokeSolver.Cli.ServeCommand;
using static SmokeSolver.Cli.ThrowCommand;
using static SmokeSolver.Cli.CalibrateCommand;
using static SmokeSolver.Cli.ValidateCommand;
using static SmokeSolver.Cli.ExportGltfCommand;
using static SmokeSolver.Cli.BestLineupCommand;
using static SmokeSolver.Cli.PointLineupCommand;

namespace SmokeSolver.Cli;

public static class LineupApi
{
    // Binary mesh for the 3D view: [int32 vertexCount][int32 indexCount]
    // [float32 x,y,z per vertex][uint32 indices], vision-filtered triangles.
    public static byte[] MeshPayload(CollisionMesh mesh, Func<byte, bool>? attributeFilter)
    {
        var indices = new List<int>(mesh.Indices.Length);
        for (var t = 0; t < mesh.Indices.Length; t += 3)
        {
            if (attributeFilter == null || attributeFilter(mesh.TriangleAttributes[t / 3]))
            {
                indices.Add(mesh.Indices[t]);
                indices.Add(mesh.Indices[t + 1]);
                indices.Add(mesh.Indices[t + 2]);
            }
        }
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);
        bw.Write(mesh.Vertices.Length / 3);
        bw.Write(indices.Count);
        foreach (var v in mesh.Vertices)
        {
            bw.Write(v);
        }
        foreach (var i in indices)
        {
            bw.Write((uint)i);
        }
        bw.Flush();
        return ms.ToArray();
    }

    /// <summary>
    /// The path a lineup's grenade actually flies, for drawing. Runs the same
    /// exact-collider integrator that verified the lineup in the first place -
    /// so this is the throw's real arc and its real bounces, not a curve fitted
    /// between the throw spot and where it came to rest.
    /// </summary>
    public static byte[] TrajectoryPayload(TriangleCollider collider, ThrowSpec spec, ThrowConstants constants)
    {
        var ticks = new List<(Vector3 Position, Vector3 Velocity)>();
        var (position, velocity) = GrenadeTrajectory.DeriveInitial(spec, constants);
        var result = GrenadeTrajectory.SimulateExactRaw(collider, position, velocity, constants, tickTrace: ticks);

        var json = new StringBuilder("{\"points\":[");
        for (var i = 0; i < ticks.Count; i++)
        {
            var p = ticks[i].Position;
            json.Append(i == 0 ? "" : ",")
                .Append(CultureInfo.InvariantCulture, $"[{p.X:F1},{p.Y:F1},{p.Z:F1}]");
        }
        json.Append(CultureInfo.InvariantCulture,
            $"],\"bounces\":{result.Bounces},\"flightTime\":{result.FlightTime:F3},\"lost\":{(result.Lost ? "true" : "false")}}}");
        return Encoding.UTF8.GetBytes(json.ToString());
    }

    // Validation bounds, named so the checks and their error messages cannot
    // drift apart. The margin allows targets slightly past the mesh AABB
    // (overhangs at map edges); the reach/tolerance ranges bracket every value
    // the viewer can produce plus generous CLI headroom.
    const float MapBoundsMargin = 512f;
    const float MinOriginReach = 16f, MaxOriginReach = 4000f;
    const float MinTolerance = 1f, MaxTolerance = 512f;

    // Malformed or absurd queries must fail fast with a 400: a NaN or out-of-map
    // coordinate otherwise flows into a minutes-long map-wide solve and a fresh
    // cache file per distinct body.
    public static string? ValidateLineupQuery(JsonElement query, CollisionMesh mesh)
    {
        if (query.ValueKind != JsonValueKind.Object)
        {
            return "body must be a JSON object";
        }
        if (!query.TryGetProperty("target", out var targetEl) || targetEl.ValueKind != JsonValueKind.Array ||
            targetEl.GetArrayLength() is < 2 or > 3)
        {
            return "target must be [x,y] or [x,y,z]";
        }
        var (meshMin, meshMax) = mesh.ComputeBounds();
        foreach (var el in targetEl.EnumerateArray())
        {
            if (el.ValueKind != JsonValueKind.Number || !float.IsFinite(el.GetSingle()))
            {
                return "target coordinates must be finite numbers";
            }
        }
        var tx = targetEl[0].GetSingle();
        var ty = targetEl[1].GetSingle();
        if (tx < meshMin.X - MapBoundsMargin || tx > meshMax.X + MapBoundsMargin ||
            ty < meshMin.Y - MapBoundsMargin || ty > meshMax.Y + MapBoundsMargin)
        {
            return "target is outside the map bounds";
        }
        if (query.TryGetProperty("origin", out var originEl))
        {
            if (originEl.ValueKind != JsonValueKind.Array || originEl.GetArrayLength() < 2 ||
                originEl.EnumerateArray().Any(e => e.ValueKind != JsonValueKind.Number || !float.IsFinite(e.GetSingle())))
            {
                return "origin must be [x,y] with finite numbers";
            }
        }
        if (query.TryGetProperty("originReach", out var reachEl) &&
            (reachEl.ValueKind != JsonValueKind.Number || !float.IsFinite(reachEl.GetSingle()) ||
             reachEl.GetSingle() is < MinOriginReach or > MaxOriginReach))
        {
            return $"originReach must be between {MinOriginReach} and {MaxOriginReach}";
        }
        if (query.TryGetProperty("tolerance", out var tolEl) &&
            (tolEl.ValueKind != JsonValueKind.Number || !float.IsFinite(tolEl.GetSingle()) ||
             tolEl.GetSingle() is < MinTolerance or > MaxTolerance))
        {
            return $"tolerance must be between {MinTolerance} and {MaxTolerance}";
        }
        if (query.TryGetProperty("minStability", out var stabEl) &&
            (stabEl.ValueKind != JsonValueKind.Number || !float.IsFinite(stabEl.GetSingle()) ||
             stabEl.GetSingle() is < 0.05f or > 1f))
        {
            return "minStability must be between 0.05 and 1";
        }
        if (query.TryGetProperty("fineScan", out var fineEl) &&
            fineEl.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            return "fineScan must be a boolean";
        }
        return null;
    }

    public static string QueryCacheKey(CollisionMesh mesh, string meshVersion, ThrowConstants constants, JsonElement query, string attrs)
    {
        var targetEl = query.GetProperty("target");
        var tx = (int)MathF.Round(targetEl[0].GetSingle() / 16f);
        var ty = (int)MathF.Round(targetEl[1].GetSingle() / 16f);
        var tz = targetEl.GetArrayLength() > 2 ? (int)MathF.Round(targetEl[2].GetSingle() / 16f) : int.MinValue;
        var origin = query.TryGetProperty("origin", out var originEl)
            ? $"{(int)MathF.Round(originEl[0].GetSingle() / 32f)},{(int)MathF.Round(originEl[1].GetSingle() / 32f)}"
            : "all";
        // Every input that changes the answer must be in the key, or two queries
        // differing only in that input replay each other's cached results.
        var reach = query.TryGetProperty("originReach", out var reachEl) ? reachEl.GetSingle() : -1f;
        var tol = query.TryGetProperty("tolerance", out var tolEl) ? tolEl.GetSingle() : 80f;
        var stab = query.TryGetProperty("minStability", out var stabEl) ? stabEl.GetSingle() : 0.4f;
        var fine = query.TryGetProperty("fineScan", out var fineEl) && fineEl.GetBoolean();
        // Bump when solver or sim behavior changes: cached answers from older code
        // must never be replayed as current results.
        const int QueryVersion = 9;
        // meshVersion is the content-hashed mesh identity (not just the game
        // build), so re-extracting a map - e.g. dropping the Retake tape - forces
        // a re-solve instead of replaying results computed against the old mesh.
        var seed = $"v{QueryVersion}|{mesh.MapName}|{meshVersion}|{JsonSerializer.Serialize(constants)}|{tx},{ty},{tz}|{origin}|{reach:F0}|{tol:F0}|{stab:F2}|{(fine ? 1 : 0)}|{attrs}";
        var hash = System.Security.Cryptography.SHA1.HashData(Encoding.UTF8.GetBytes(seed));
        return Convert.ToHexString(hash)[..20].ToLowerInvariant();
    }

    /// <summary>
    /// Interactive two-click query: land a smoke at `target`, throwing from near `origin`.
    /// Returns the best lineups from nav-walkable positions around the origin click.
    /// </summary>
    public static string RunTargetQuery(
        CollisionMesh mesh,
        Func<byte, bool>? attributeFilter,
        List<NavAreaJson> navAreas,
        JsonElement query,
        ThrowConstants constants,
        Action<string, int>? onPhase = null,
        Action<Vector3, int>? onOrigin = null,
        Action<Vector3, bool>? onCandidate = null)
    {
        var targetEl = query.GetProperty("target");
        var target = new Vector3(targetEl[0].GetSingle(), targetEl[1].GetSingle(), 0);
        var hasTargetZ = targetEl.GetArrayLength() > 2;
        if (hasTargetZ)
        {
            target.Z = targetEl[2].GetSingle();
        }
        // Without an origin click, search everywhere a player can stand within throw
        // power of the target: reachability and ballistics are the only limits.
        var hasOrigin = query.TryGetProperty("origin", out var originEl);
        var originClick = hasOrigin
            ? new Vector2(originEl[0].GetSingle(), originEl[1].GetSingle())
            : (Vector2?)null;
        var originReach = hasOrigin
            ? (query.TryGetProperty("originReach", out var reachEl) ? reachEl.GetSingle() : 300f)
            : 3100f;
        var tolerance = query.TryGetProperty("tolerance", out var tolEl) ? tolEl.GetSingle() : 80f;
        var minStability = query.TryGetProperty("minStability", out var stabEl) ? stabEl.GetSingle() : 0.4f;
        var fineScan = query.TryGetProperty("fineScan", out var fineEl) && fineEl.GetBoolean();

        var solve = SolveForTarget(mesh, attributeFilter, navAreas, target, hasTargetZ, originClick, originReach, tolerance, constants, onPhase, onOrigin, onCandidate, minStability, fineScan);

        // Raw voxel-stage counts overstate throwability (many candidates die in
        // exact-sim verification), so each cell also says whether a verified
        // lineup actually stands there. The viewer renders the two differently.
        var verifiedAt = solve.Lineups
            .Select(l => ((int)MathF.Round(l.Feet.X), (int)MathF.Round(l.Feet.Y)))
            .ToHashSet();

        // A throw aimed into featureless sky has nothing to line the crosshair
        // against, so sky shots sink below every referenced lineup regardless
        // of trajectory quality (stable sort keeps the physical order within
        // each group). Within that, a stand spot the geometry pins - a corner
        // wedge or a wall press - outranks open ground: walking into the wall
        // removes the player's position error entirely, which is what makes a
        // lineup reproducible in a real round.
        var aimRefs = solve.Lineups.ToDictionary(
            l => l,
            l => AimReference.Analyze(solve.Collider, l.Feet, l.Type, l.PitchDeg, l.YawDeg));
        var pins = solve.Lineups.ToDictionary(
            l => l,
            l => LineupSolver.PositionPin(solve.Collider, l.Feet));
        // A probe means "I stand HERE": closeness to the click outranks
        // everything but a usable aim reference, in 32u bands so a pinned spot
        // still wins among near-equals. A map-wide sweep has no "here", so
        // pinned spots lead outright.
        var ranked = (originClick is { } click
                ? solve.Lineups
                    .OrderBy(l => aimRefs[l].IsSkyShot ? 1 : 0)
                    .ThenBy(l => (int)(Vector2.Distance(new Vector2(l.Feet.X, l.Feet.Y), click) / 32f))
                    .ThenByDescending(l => pins[l])
                : solve.Lineups
                    .OrderBy(l => aimRefs[l].IsSkyShot ? 1 : 0)
                    .ThenByDescending(l => pins[l]))
            .ToList();

        return JsonSerializer.Serialize(new
        {
            target = new[] { solve.Target.X, solve.Target.Y, solve.Target.Z },
            origins = solve.OriginCount,
            // Per evaluated origin: [x, y, raw option count, verified lineup here].
            // Zero-count cells are the interesting ones - places a player can stand
            // where no simulated throw reaches the target (either truly impossible
            // or a sim gap).
            coverage = solve.Coverage
                .Select(c => new[] { c[0], c[1], c[2], verifiedAt.Contains((c[0], c[1])) ? 1 : 0 }),
            // 12 for a probe (steep lobs and pinned spots widened the field a
            // single click can deserve), 400 for the map-wide sweep.
            lineups = ranked.Take(hasOrigin ? 12 : 400).Select(l => new
            {
                feet = new[] { l.Feet.X, l.Feet.Y, l.Feet.Z },
                yaw = l.YawDeg,
                pitch = l.PitchDeg,
                type = l.Type.ToString(),
                how = Describe(l.Type, l.Strength), strength = l.Strength, click = ClickName(l.Strength),
                rest = new[] { l.RestPoint.X, l.RestPoint.Y, l.RestPoint.Z },
                l.Bounces,
                flightTime = l.FlightTime,
                stability = l.Stability,
                pin = pins[l] switch { 2 => "corner", 1 => "wall", _ => (string?)null },
                aimRef = new
                {
                    tier = aimRefs[l].Tier,
                    sky = aimRefs[l].SkyFraction,
                    edgeDeg = float.IsFinite(aimRefs[l].NearestSilhouetteDeg) ? (float?)aimRefs[l].NearestSilhouetteDeg : null,
                    reticleDeg = float.IsFinite(aimRefs[l].NearestReticleDeg) ? (float?)aimRefs[l].NearestReticleDeg : null,
                },
                console = SetposCommand(l.Feet, l.PitchDeg, l.YawDeg),
            }),
        });
    }
}

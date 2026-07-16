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
    /// <summary>
    /// The 3D view's "what the solver actually collides grenades with" mesh:
    /// every triangle the grenade filter treats as solid, split into two index
    /// groups so the viewer can draw them apart -
    ///   world:   ordinary walls that also exist in the textured render, and
    ///   phantom: grenade-clips, physics-clips, and glass - solid to the sim but
    ///            invisible or special in the real world, the usual suspects
    ///            behind "why won't this smoke go through / why is this blocked".
    /// Player/NPC clips and sky are excluded exactly as the sim excludes them,
    /// so a "wall" that is really just a movement clip disappears here - which is
    /// how the view stops lying about what blocks a smoke.
    /// Format: [int32 vertexCount][int32 worldIndexCount][int32 phantomIndexCount]
    ///         [float32 x,y,z per vertex][uint32 world indices][uint32 phantom indices].
    /// </summary>
    public static byte[] MeshPayloadSolid(CollisionMesh mesh)
    {
        var grenadeSolid = mesh.GrenadeSolidFilter();
        var phantom = new bool[mesh.AttributeNames.Length];
        for (var i = 0; i < phantom.Length; i++)
        {
            var layers = mesh.AttributeInteractAs[i];
            phantom[i] = grenadeSolid((byte)i) && (
                layers.Any(l => l.Equals("csgo_grenadeclip", StringComparison.OrdinalIgnoreCase)) ||
                layers.Any(l => l.Equals("window", StringComparison.OrdinalIgnoreCase)) ||
                mesh.AttributeNames[i].Equals("EntityPhysicsClip", StringComparison.Ordinal));
        }
        var world = new List<int>();
        var special = new List<int>();
        for (var t = 0; t < mesh.Indices.Length; t += 3)
        {
            var attr = mesh.TriangleAttributes[t / 3];
            if (!grenadeSolid(attr))
            {
                continue;
            }
            var group = phantom[attr] ? special : world;
            group.Add(mesh.Indices[t]);
            group.Add(mesh.Indices[t + 1]);
            group.Add(mesh.Indices[t + 2]);
        }
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);
        bw.Write(mesh.Vertices.Length / 3);
        bw.Write(world.Count);
        bw.Write(special.Count);
        foreach (var v in mesh.Vertices)
        {
            bw.Write(v);
        }
        foreach (var i in world)
        {
            bw.Write((uint)i);
        }
        foreach (var i in special)
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

    /// <summary>
    /// Everything the viewer shows for one selected lineup, computed from the
    /// throw's physical spec instead of a map-wide solve: the flight path, its
    /// rest and bounces, the aim reference, the corner/wall pin, the one-tick
    /// position-chaos scatter, and the aim-window robustness. A shared link
    /// carries the spec exactly, so opening it renders that single throw at once
    /// - no sweeping the rest of the map for spots the user did not ask about.
    /// The lineup object mirrors a sweep result's shape so the viewer draws it
    /// identically; the flight points ride alongside so no second fetch is needed.
    /// </summary>
    public static byte[] LineupOnePayload(
        TriangleCollider collider, TriangleCollider playerCollider, Vector3 feet, Vector3 target,
        ThrowType type, float strength, float pitchDeg, float yawDeg, float runDeg, ThrowConstants constants)
    {
        var eye = feet + new Vector3(0, 0, GrenadeTrajectory.EyeHeight(type));
        var spec = new ThrowSpec(eye, yawDeg, pitchDeg, type, strength, runDeg);
        var ticks = new List<(Vector3 Position, Vector3 Velocity)>();
        var (position, velocity) = GrenadeTrajectory.DeriveInitial(spec, constants);
        var result = GrenadeTrajectory.SimulateExactRaw(collider, position, velocity, constants, tickTrace: ticks);

        static bool Settled(TrajectoryResult r) =>
            !r.Lost && r.FlightTime < GrenadeTrajectory.MaxFlightSeconds - 0.01f;

        // One-tick (0.25u) foot-shift scatter: the same four-probe measure the
        // solver's verify stage records, so a link to a fragile lineup reads as
        // fragile here too.
        var scatter = 0f;
        if (Settled(result))
        {
            foreach (var (dx, dy) in ((float, float)[])[(0.25f, 0f), (-0.25f, 0f), (0f, 0.25f), (0f, -0.25f)])
            {
                var probe = GrenadeTrajectory.SimulateExact(collider, new ThrowSpec(
                    eye + new Vector3(dx, dy, 0f), yawDeg, pitchDeg, type, strength, runDeg), constants);
                scatter = MathF.Max(scatter, Settled(probe) ? Vector3.Distance(probe.RestPoint, result.RestPoint) : 512f);
            }
        }

        // Aim robustness: the share of a +-1.2 deg aim window whose exact rest
        // still lands within a smoke radius of the target - the same "how
        // forgiving is the aim" idea as the solver's stability, judged against
        // the shared target rather than the solve's internal zone.
        const float StepDeg = 0.6f;
        const int AimReach = 2;
        const float CoverRadius = 72f;
        var hits = 0;
        var total = 0;
        for (var dYaw = -AimReach; dYaw <= AimReach; dYaw++)
        {
            for (var dPitch = -AimReach; dPitch <= AimReach; dPitch++)
            {
                total++;
                var probe = GrenadeTrajectory.SimulateExact(collider, new ThrowSpec(
                    eye, yawDeg + dYaw * StepDeg, pitchDeg + dPitch * StepDeg, type, strength, runDeg), constants);
                if (Settled(probe) &&
                    new Vector2(probe.RestPoint.X - target.X, probe.RestPoint.Y - target.Y).Length() <= CoverRadius)
                {
                    hits++;
                }
            }
        }
        var stability = total == 0 ? 0f : (float)hits / total;

        var aim = AimReference.Analyze(collider, feet, type, pitchDeg, yawDeg);
        var pin = LineupSolver.PositionPin(playerCollider, feet);

        var json = new StringBuilder("{\"points\":[");
        for (var i = 0; i < ticks.Count; i++)
        {
            var p = ticks[i].Position;
            json.Append(i == 0 ? "" : ",").Append(CultureInfo.InvariantCulture, $"[{p.X:F1},{p.Y:F1},{p.Z:F1}]");
        }
        json.Append("],\"lineup\":").Append(JsonSerializer.Serialize(new
        {
            feet = new[] { feet.X, feet.Y, feet.Z },
            yaw = yawDeg,
            pitch = pitchDeg,
            type = type.ToString(),
            how = Describe(type, strength, runDeg),
            strength,
            click = ClickName(strength),
            runDeg,
            rest = new[] { result.RestPoint.X, result.RestPoint.Y, result.RestPoint.Z },
            result.Bounces,
            flightTime = result.FlightTime,
            stability,
            scatter,
            pin = pin switch { 2 => "corner", 1 => "wall", _ => (string?)null },
            aimRef = new
            {
                tier = aim.Tier,
                sky = aim.SkyFraction,
                edgeDeg = float.IsFinite(aim.NearestSilhouetteDeg) ? (float?)aim.NearestSilhouetteDeg : null,
                reticleDeg = float.IsFinite(aim.NearestReticleDeg) ? (float?)aim.NearestReticleDeg : null,
            },
            console = SetposCommand(feet, pitchDeg, yawDeg),
            lost = result.Lost,
        })).Append('}');
        return Encoding.UTF8.GetBytes(json.ToString());
    }

    /// <summary>
    /// How far the player can drift from a lineup's exact stand spot, per world
    /// direction, before the same aim angles stop landing within `within` units
    /// of the target. This is the honest answer to "how precisely must I stand
    /// here": each direction is bisected against the exact simulator, so walls
    /// (a pinned spot), ledges, and bounce geometry all shape the ring rather
    /// than assuming the landing shifts linearly with the feet.
    /// </summary>
    public static byte[] PositionSlackPayload(
        TriangleCollider collider, TriangleCollider playerCollider, Vector3 feet, ThrowType type, float strength,
        float pitchDeg, float yawDeg, float runDeg, Vector3 target, float within, ThrowConstants constants)
    {
        const int Directions = 12;
        const float MaxProbe = 64f;
        // sv_standable_normal: the same floor test the sim and origin snap use.
        const float StandableNormalZ = 0.7f;

        bool LandsFrom(Vector3 candidateFeet)
        {
            var eye = candidateFeet + new Vector3(0, 0, GrenadeTrajectory.EyeHeight(type));
            var result = GrenadeTrajectory.SimulateExact(collider, new ThrowSpec(eye, yawDeg, pitchDeg, type, strength, runDeg), constants);
            return !result.Lost && result.FlightTime < GrenadeTrajectory.MaxFlightSeconds - 0.01f &&
                new Vector2(result.RestPoint.X - target.X, result.RestPoint.Y - target.Y).Length() <= within;
        }

        // Probes at waist height so floor trim does not read as a wall, exactly
        // like the pin logic does.
        var waist = feet + new Vector3(0, 0, 36f);
        Vector3? FeetAt(Vector2 xy)
        {
            // The player has to be able to slide there: geometry between the
            // stand spot and the offset (a wall or clip the lineup is pinned
            // against) truncates that direction to zero, which is the point of
            // pinning. Player-solid, not grenade-solid - clips stop feet.
            var offsetWaist = new Vector3(xy.X, xy.Y, waist.Z);
            if (playerCollider.FirstHit(waist, offsetWaist) != null)
            {
                return null;
            }
            // And there must be standable floor beneath - stepping off a ledge
            // is not a small aim error, it is a different throw entirely.
            var hit = playerCollider.FirstHit(offsetWaist, offsetWaist - new Vector3(0, 0, 72f));
            return hit is { } h && h.Normal.Z >= StandableNormalZ
                ? new Vector3(xy.X, xy.Y, offsetWaist.Z - 72f * h.T)
                : null;
        }

        var feetXy = new Vector2(feet.X, feet.Y);
        // If even the exact spot misses the asked-for precision, every radius
        // is zero rather than a bisection against an unsatisfiable predicate.
        var centered = LandsFrom(feet);
        var json = new StringBuilder();
        json.Append(CultureInfo.InvariantCulture, $"{{\"within\":{within:F0},\"dirs\":[");
        for (var i = 0; i < Directions; i++)
        {
            var ang = i * 2f * MathF.PI / Directions;
            var dir = new Vector2(MathF.Cos(ang), MathF.Sin(ang));
            bool Ok(float r) => FeetAt(feetXy + dir * r) is { } f && LandsFrom(f);
            var radius = 0f;
            if (centered)
            {
                // Bisection treats the landing as monotone in the drift, which
                // holds in practice for the ranges probed here (<= 64u).
                float lo = 0f, hi = MaxProbe;
                if (Ok(MaxProbe))
                {
                    radius = MaxProbe;
                }
                else
                {
                    for (var step = 0; step < 6; step++)
                    {
                        var mid = (lo + hi) / 2f;
                        if (Ok(mid))
                        {
                            lo = mid;
                        }
                        else
                        {
                            hi = mid;
                        }
                    }
                    radius = lo;
                }
            }
            json.Append(i == 0 ? "" : ",")
                .Append(CultureInfo.InvariantCulture, $"[{ang * 180f / MathF.PI:F0},{radius:F1}]");
        }
        json.Append("]}");
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
        if (query.TryGetProperty("types", out var typesEl) &&
            (typesEl.ValueKind != JsonValueKind.Array || typesEl.GetArrayLength() is 0 or > 5 ||
             typesEl.EnumerateArray().Any(e => e.ValueKind != JsonValueKind.String || !Enum.TryParse<ThrowType>(e.GetString(), ignoreCase: true, out _))))
        {
            return "types must be a non-empty array of throw type names";
        }
        if (query.TryGetProperty("strengths", out var strengthsEl) &&
            (strengthsEl.ValueKind != JsonValueKind.Array || strengthsEl.GetArrayLength() is 0 or > 3 ||
             strengthsEl.EnumerateArray().Any(e => e.ValueKind != JsonValueKind.Number || e.GetSingle() is not (0f or 0.5f or 1f))))
        {
            return "strengths must be a non-empty array drawn from 0, 0.5, 1";
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
        var typesKey = query.TryGetProperty("types", out var typesEl)
            ? string.Join(",", typesEl.EnumerateArray().Select(e => e.GetString()!.ToLowerInvariant()).OrderBy(t => t, StringComparer.Ordinal))
            : "all";
        var strengthsKey = query.TryGetProperty("strengths", out var strengthsEl)
            ? string.Join(",", strengthsEl.EnumerateArray().Select(e => e.GetSingle().ToString("F1", CultureInfo.InvariantCulture)).OrderBy(v => v, StringComparer.Ordinal))
            : "all";
        // Bump when solver or sim behavior changes: cached answers from older code
        // must never be replayed as current results.
        const int QueryVersion = 13;
        // meshVersion is the content-hashed mesh identity (not just the game
        // build), so re-extracting a map - e.g. dropping the Retake tape - forces
        // a re-solve instead of replaying results computed against the old mesh.
        var seed = $"v{QueryVersion}|{mesh.MapName}|{meshVersion}|{JsonSerializer.Serialize(constants)}|{tx},{ty},{tz}|{origin}|{reach:F0}|{tol:F0}|{stab:F2}|{(fine ? 1 : 0)}|{typesKey}|{strengthsKey}|{attrs}";
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
        // Scoping the sweep to the filters the user already set skips whole
        // type/strength combinations from every origin - up to 15x less work.
        var types = query.TryGetProperty("types", out var typesEl)
            ? typesEl.EnumerateArray().Select(e => Enum.Parse<ThrowType>(e.GetString()!, ignoreCase: true)).Distinct().ToList()
            : null;
        var strengths = query.TryGetProperty("strengths", out var strengthsEl)
            ? strengthsEl.EnumerateArray().Select(e => e.GetSingle()).Distinct().ToList()
            : null;

        var solve = SolveForTarget(mesh, attributeFilter, navAreas, target, hasTargetZ, originClick, originReach, tolerance, constants, onPhase, onOrigin, onCandidate, minStability, fineScan, types, strengths);

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
            l => LineupSolver.PositionPin(solve.PlayerCollider, l.Feet));
        // A probe means "I stand HERE": closeness to the click outranks
        // everything but a usable aim reference, in 32u bands so a pinned spot
        // still wins among near-equals. A map-wide sweep has no "here", so
        // pinned spots lead outright.
        // Chaotic lineups (rest flips when the feet move one tick) sink below
        // everything reproducible - the in-game misses users hit were exactly
        // these, scoring 100% aim stability while being position-fragile.
        var bySky = solve.Lineups.OrderBy(l => aimRefs[l].IsSkyShot ? 1 : 0).ThenBy(l => l.RestScatter > 16f ? 1 : 0);
        var ranked = (originClick is { } click
                ? bySky.ThenBy(l => (int)(Vector2.Distance(new Vector2(l.Feet.X, l.Feet.Y), click) / 32f)).ThenByDescending(l => pins[l])
                : bySky.ThenByDescending(l => pins[l]))
            .ToList();

        return JsonSerializer.Serialize(new
        {
            target = new[] { solve.Target.X, solve.Target.Y, solve.Target.Z },
            origins = solve.OriginCount,
            // Per evaluated origin: [x, y, raw option count, verified lineup
            // here, pin class (2 corner / 1 wall / 0 open)]. Zero-count cells
            // are the interesting ones - places a player can stand where no
            // simulated throw reaches the target (either truly impossible or a
            // sim gap); the pin class drives the stand-spot heat view.
            coverage = solve.Coverage
                .Select(c => new[] { c[0], c[1], c[2], verifiedAt.Contains((c[0], c[1])) ? 1 : 0, c.Length > 3 ? c[3] : 0 }),
            // 12 for a probe (steep lobs and pinned spots widened the field a
            // single click can deserve), 400 for the map-wide sweep.
            lineups = ranked.Take(hasOrigin ? 12 : 400).Select(l => new
            {
                feet = new[] { l.Feet.X, l.Feet.Y, l.Feet.Z },
                yaw = l.YawDeg,
                pitch = l.PitchDeg,
                type = l.Type.ToString(),
                how = Describe(l.Type, l.Strength, l.RunYawOffsetDeg), strength = l.Strength, click = ClickName(l.Strength),
                // Movement-key direction for running jump throws (0 = W,
                // +90 = A, -90 = D, +-45 = diagonals); part of the throw's
                // physical identity, so the viewer must echo it back when
                // fetching this lineup's trajectory.
                runDeg = l.RunYawOffsetDeg,
                rest = new[] { l.RestPoint.X, l.RestPoint.Y, l.RestPoint.Z },
                l.Bounces,
                flightTime = l.FlightTime,
                stability = l.Stability,
                // Rest displacement under a one-tick (0.25u) foot shift; big
                // values mean the lineup is physically fragile however
                // precisely it is aimed.
                scatter = l.RestScatter,
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

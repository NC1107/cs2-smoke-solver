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

public static class ValidateCommand
{
    public static int Run(Dictionary<string, string> options)
    {
        // --markers <file.json>: run the full validation once per saved in-game
        // marker (written by the plugin's !mark command), using each marker as the
        // target. One report per marker.
        if (options.TryGetValue("markers", out var markersPath))
        {
            var markers = LoadJson<Dictionary<string, float[]>>(markersPath, "markers");
            if (markers.Count == 0)
            {
                Console.Error.WriteLine("no markers in file; use !mark <name> in-game first");
                return 1;
            }
            Console.WriteLine($"validating {markers.Count} marker(s): {string.Join(", ", markers.Keys)}");
            var failures = 0;
            foreach (var (name, pos) in markers)
            {
                Console.WriteLine($"=== marker '{name}' ({pos[0]:F0},{pos[1]:F0},{pos[2]:F0}) ===");
                var sub = new Dictionary<string, string>(options);
                sub.Remove("markers");
                sub.Remove("getpos");
                sub["target"] = FormattableString.Invariant($"{pos[0]},{pos[1]},{pos[2]}");
                failures += Run(sub) == 0 ? 0 : 1;
            }
            return failures == 0 ? 0 : 1;
        }

        // Defaults land on a local clone: the caller's dictionary is shared
        // state and must not be mutated (--markers already clones for this).
        options = new Dictionary<string, string>(options);
        options.TryAdd("attrs", SingleTargetDefaultAttrs);
        var (mesh, _, _, attributeFilter) = LoadCommon(options);
        var navAreas = LoadJson<List<NavAreaJson>>(options.GetValueOrDefault("nav", DefaultNavAreasPath(options, mesh)), "nav areas");
        var constants = LoadConstants(options);

        Vector3 target;
        bool hasTargetZ;
        if (options.TryGetValue("getpos", out var gp))
        {
            var (eye, _, _) = ParseGetPos(gp);
            target = eye - new Vector3(0, 0, 64);
            hasTargetZ = true;
        }
        else
        {
            (target, hasTargetZ) = ParseVec2or3(Require(options, "target"));
        }
        var tolerance = float.Parse(options.GetValueOrDefault("tolerance", "80"), CultureInfo.InvariantCulture);
        var limit = int.Parse(options.GetValueOrDefault("limit", "0"), CultureInfo.InvariantCulture);
        var passRadius = float.Parse(options.GetValueOrDefault("pass", "3"), CultureInfo.InvariantCulture);
        var calibDir = options.GetValueOrDefault(
            "calib", Environment.GetEnvironmentVariable("SMOKESOLVER_CALIB_DIR") ?? "data/calib");
        var dryRun = options.ContainsKey("dry-run");

        Console.WriteLine($"solving target ({target.X:F0},{target.Y:F0}{(hasTargetZ ? $",{target.Z:F0}" : "")}) tolerance {tolerance:F0}u ...");
        var started = System.Diagnostics.Stopwatch.StartNew();
        var solve = SolveForTarget(mesh, attributeFilter, navAreas, target, hasTargetZ, null, 3100f, tolerance, constants);
        var lineups = limit > 0 ? solve.Lineups.Take(limit).ToList() : solve.Lineups;
        Console.WriteLine($"{solve.Lineups.Count} lineups solved in {started.Elapsed.TotalSeconds:F0}s ({lineups.Count} selected, {solve.OriginCount} origins)");
        if (lineups.Count == 0)
        {
            return 1;
        }

        var collider = solve.Collider;
        var plans = lineups.Select((l, i) =>
        {
            var eye = l.Feet + new Vector3(0, 0, GrenadeTrajectory.EyeHeight(l.Type));
            // RunYawOffsetDeg must ride along: a lateral strafe-jump lineup
            // thrown with the W-direction velocity is a different throw, and
            // grading the real one against that prediction reports a phantom
            // launch error on every directional run-jump.
            var (pos, vel) = GrenadeTrajectory.DeriveInitial(new ThrowSpec(eye, l.YawDeg, l.PitchDeg, l.Type, l.Strength, l.RunYawOffsetDeg), constants);
            var predicted = GrenadeTrajectory.SimulateExactRaw(collider, pos, vel, constants);
            return new ValidatePlan(i, l, pos, vel, predicted.RestPoint, predicted.Bounces);
        }).ToList();

        var perturb = Math.Clamp(float.Parse(options.GetValueOrDefault("perturb", "0"), CultureInfo.InvariantCulture), 0f, 8f);
        if (perturb > 0)
        {
            // One-tick-movement probes: the same aim thrown again from feet
            // shifted by the quantum a single movement-key tick produces
            // (~0.25u). Grenade flight is continuous in the origin, so these
            // land within the shift of the base throw - except where a bounce
            // boundary sits closer than the shift, which is exactly what this
            // measures in the real game rather than in the sim's opinion.
            var extra = new List<ValidatePlan>();
            foreach (var basePlan in plans)
            {
                foreach (var (dx, dy) in new[] { (perturb, 0f), (-perturb, 0f), (0f, perturb), (0f, -perturb) })
                {
                    var feet = basePlan.Lineup.Feet + new Vector3(dx, dy, 0f);
                    // Stand the offset on the real floor so a probe next to a
                    // step edge does not release from inside geometry.
                    var probeTop = feet + new Vector3(0, 0, 36f);
                    if (solve.PlayerCollider.FirstHit(probeTop, probeTop - new Vector3(0, 0, 72f)) is { } floor && floor.Normal.Z >= 0.7f)
                    {
                        feet.Z = probeTop.Z - 72f * floor.T;
                    }
                    var lineup = basePlan.Lineup with { Feet = feet };
                    var eye = feet + new Vector3(0, 0, GrenadeTrajectory.EyeHeight(lineup.Type));
                    var (pos, vel) = GrenadeTrajectory.DeriveInitial(new ThrowSpec(eye, lineup.YawDeg, lineup.PitchDeg, lineup.Type, lineup.Strength, lineup.RunYawOffsetDeg), constants);
                    var predicted = GrenadeTrajectory.SimulateExactRaw(collider, pos, vel, constants);
                    extra.Add(new ValidatePlan(plans.Count + extra.Count, lineup, pos, vel, predicted.RestPoint, predicted.Bounces, perturb));
                }
            }
            plans.AddRange(extra);
            Console.WriteLine($"added {extra.Count} one-tick perturbation probes (+-{perturb:F2}u per axis)");
        }

        if (dryRun)
        {
            foreach (var p in plans.Take(20))
            {
                Console.WriteLine($"  [{p.Index}] {p.Lineup.Type} s={p.Lineup.Strength:F1} pos ({p.Pos.X:F0},{p.Pos.Y:F0},{p.Pos.Z:F0}) vel ({p.Vel.X:F0},{p.Vel.Y:F0},{p.Vel.Z:F0}) -> predicted rest ({p.PredictedRest.X:F0},{p.PredictedRest.Y:F0},{p.PredictedRest.Z:F0})");
            }
            Console.WriteLine($"dry run: {plans.Count} throws planned, nothing submitted");
            return 0;
        }

        var requestPath = Path.Combine(calibDir, "request.json");
        var capturesPath = Path.Combine(calibDir, "captures.jsonl");
        var tailer = new CaptureTailer(capturesPath);
        var baseline = tailer.InitializeAtEnd();
        Console.WriteLine($"submitting {plans.Count} throws to {requestPath} (captures baseline offset {baseline}) ...");

        // Batches of 6 per request file: the plugin polls every 8 ticks (~1/8s)
        // and launches the whole batch in one tick, so throughput is bounded by
        // poll cadence rather than per-throw round trips.
        var submitted = 0;
        var stopPath = Path.Combine(calibDir, "stop-request");
        File.Delete(stopPath);
        foreach (var batch in plans.Chunk(6))
        {
            if (File.Exists(stopPath))
            {
                File.Delete(stopPath);
                Console.WriteLine($"stopped by user request after {submitted}/{plans.Count}");
                break;
            }
            RequestFile.WriteAtomic(requestPath, JsonSerializer.Serialize(new
            {
                throws = batch.Select(p => new
                {
                    pos = new[] { p.Pos.X, p.Pos.Y, p.Pos.Z },
                    vel = new[] { p.Vel.X, p.Vel.Y, p.Vel.Z },
                    predict = new[] { p.PredictedRest.X, p.PredictedRest.Y, p.PredictedRest.Z },
                    // Shown in server chat by the plugin when a spectator is connected.
                    note = $"#{p.Index + 1}/{plans.Count} {p.Lineup.Type} {ClickName(p.Lineup.Strength)} {p.PredictedBounces}b -> predict ({p.PredictedRest.X:F0},{p.PredictedRest.Y:F0},{p.PredictedRest.Z:F0})",
                }).ToArray(),
            }));
            var waited = 0;
            while (File.Exists(requestPath) && waited < 15000)
            {
                Thread.Sleep(50);
                waited += 50;
            }
            if (File.Exists(requestPath))
            {
                Console.Error.WriteLine($"  batch at {batch[0].Index} not consumed after 15s; is the rig server running with the plugin loaded?");
                File.Delete(requestPath);
                continue;
            }
            submitted += batch.Length;
            if (submitted / 25 != (submitted - batch.Length) / 25)
            {
                Console.WriteLine($"  submitted {submitted}/{plans.Count}");
            }
            // ~4 throws/sec keeps concurrent smoke volumes (~20s lifetime) under
            // control so the server stays smooth for anyone spectating.
            Thread.Sleep(1500);
        }

        Console.WriteLine($"all {submitted} submitted; waiting for captures (smokes persist ~20s each) ...");

        // Captures arrive when each projectile despawns; match them back to plans
        // by initial position + velocity (synthetic throws echo them exactly).
        var matches = new Dictionary<int, JsonElement>();
        var idleMs = 0;
        while (matches.Count < submitted && idleMs < 120000)
        {
            Thread.Sleep(2000);
            idleMs += 2000;
            foreach (var line in tailer.ReadNewLines())
            {
                JsonElement c;
                float[] s, v;
                try
                {
                    c = JsonSerializer.Deserialize<JsonElement>(line);
                    s = c.GetProperty("start").EnumerateArray().Select(e => e.GetSingle()).ToArray();
                    v = c.GetProperty("velocity").EnumerateArray().Select(e => e.GetSingle()).ToArray();
                }
                catch (JsonException)
                {
                    Console.Error.WriteLine("  skipping malformed capture line");
                    continue;
                }
                foreach (var p in plans)
                {
                    if (matches.ContainsKey(p.Index))
                    {
                        continue;
                    }
                    if (MathF.Abs(s[0] - p.Pos.X) < 0.5f && MathF.Abs(s[1] - p.Pos.Y) < 0.5f && MathF.Abs(s[2] - p.Pos.Z) < 0.5f &&
                        MathF.Abs(v[0] - p.Vel.X) < 0.5f && MathF.Abs(v[1] - p.Vel.Y) < 0.5f && MathF.Abs(v[2] - p.Vel.Z) < 0.5f)
                    {
                        matches[p.Index] = c;
                        idleMs = 0;
                        break;
                    }
                }
            }

            if (matches.Count > 0 && matches.Count % 50 == 0)
            {
                Console.WriteLine($"  matched {matches.Count}/{submitted}");
            }
        }
        Console.WriteLine($"matched {matches.Count}/{submitted} captures");

        var results = new List<ValidateRow>();
        var errors = new List<float>();
        var perturbErrors = new List<float>();
        var withinPass = 0;
        var within1 = 0;
        var within2 = 0;
        var within8 = 0;
        var notDetonated = 0;
        var culled = 0;
        foreach (var p in plans)
        {
            if (!matches.TryGetValue(p.Index, out var c))
            {
                continue;
            }
            var r = c.GetProperty("rest").EnumerateArray().Select(e => e.GetSingle()).ToArray();
            var real = new Vector3(r[0], r[1], r[2]);
            var detonated = c.GetProperty("detonated").GetBoolean();
            if (!detonated)
            {
                notDetonated++;
                // An undetonated capture whose last sample is still moving fast
                // is a projectile the engine CULLED mid-flight (dense batches
                // keep dozens airborne) - its "rest" is wherever deletion caught
                // it, not physics. Grading those poisoned a whole campaign's
                // metrics with phantom 1000u+ errors; they are counted and
                // skipped, never scored.
                var lastSample = c.GetProperty("samples").EnumerateArray().LastOrDefault();
                if (lastSample.ValueKind == JsonValueKind.Array)
                {
                    var ls = lastSample.EnumerateArray().Select(e => e.GetSingle()).ToArray();
                    if (ls.Length >= 7 && new Vector3(ls[4], ls[5], ls[6]).Length() > 50f)
                    {
                        culled++;
                        continue;
                    }
                }
            }
            var err = Vector3.Distance(real, p.PredictedRest);
            // Headline metrics stay base-throws-only so run summaries remain
            // comparable whether or not a batch used perturbation probes; the
            // probes get their own block below.
            if (p.PerturbU > 0f)
            {
                perturbErrors.Add(err);
            }
            else
            {
                errors.Add(err);
                if (err <= passRadius)
                {
                    withinPass++;
                }
                // The bars the dashboard trends against: 1-2u is "the sim IS
                // the game" territory, 8u is indistinguishable to a player.
                if (err <= 1f)
                {
                    within1++;
                }
                if (err <= 2f)
                {
                    within2++;
                }
                if (err <= 8f)
                {
                    within8++;
                }
            }

            // Auto-diagnosis: replay the sim per tick against the captured real
            // flight, count real bounces (velocity discontinuities), and classify
            // the first divergence so misses arrive pre-triaged.
            var samples = c.GetProperty("samples").EnumerateArray()
                .Select(s => s.EnumerateArray().Select(e => e.GetSingle()).ToArray()).ToList();
            var simTicks = new List<(Vector3 Position, Vector3 Velocity)>();
            GrenadeTrajectory.SimulateExactRaw(collider, p.Pos, p.Vel, constants, tickTrace: simTicks);
            var realBounces = 0;
            for (var i = 1; i < samples.Count; i++)
            {
                var dvx = samples[i][4] - samples[i - 1][4];
                var dvy = samples[i][5] - samples[i - 1][5];
                var dvz = samples[i][6] - samples[i - 1][6];
                if (samples[i][4] == 0 && samples[i][5] == 0 && samples[i][6] == 0)
                {
                    break;
                }
                if (MathF.Abs(dvx) > 0.5f || MathF.Abs(dvy) > 0.5f || MathF.Abs(dvz + 5f) > 0.5f)
                {
                    realBounces++;
                }
            }
            var divergenceTick = -1;
            // Sim rest ends its tick trace; if the flights matched to that point
            // but the rests differ, the sim stopped where the real one kept going
            // (or vice versa) - that is a rest mismatch, not a tracked flight.
            var divergenceClass = err > 8f ? "REST-MISMATCH" : "TRACKED";
            for (var i = 0; i < Math.Min(simTicks.Count, samples.Count); i++)
            {
                var realPos = new Vector3(samples[i][1], samples[i][2], samples[i][3]);
                if (Vector3.Distance(simTicks[i].Position, realPos) > 8f)
                {
                    divergenceTick = i;
                    var realVel = new Vector3(samples[i][4], samples[i][5], samples[i][6]);
                    var prevRealVel = i > 0 ? new Vector3(samples[i - 1][4], samples[i - 1][5], samples[i - 1][6]) : p.Vel;
                    var rdv = realVel - prevRealVel;
                    var realBounced = MathF.Abs(rdv.X) > 0.5f || MathF.Abs(rdv.Y) > 0.5f || MathF.Abs(rdv.Z + 5f) > 0.5f;
                    var simVel = simTicks[i].Velocity;
                    var prevSimVel = i > 0 ? simTicks[i - 1].Velocity : p.Vel;
                    var sdv = simVel - prevSimVel;
                    var simBounced = MathF.Abs(sdv.X) > 0.5f || MathF.Abs(sdv.Y) > 0.5f || MathF.Abs(sdv.Z + 5f) > 0.5f;
                    divergenceClass = realBounced && !simBounced ? "MISSED-BOUNCE"
                        : simBounced && !realBounced ? "PHANTOM-BOUNCE"
                        : simBounced && realBounced ? "BOUNCE-MISMATCH"
                        : "DRIFT";
                    break;
                }
            }

            results.Add(new ValidateRow(
                p.Index,
                p.Lineup.Type.ToString(),
                p.Lineup.Strength,
                p.Lineup.Stability,
                new[] { p.Lineup.Feet.X, p.Lineup.Feet.Y, p.Lineup.Feet.Z },
                p.Lineup.YawDeg,
                p.Lineup.PitchDeg,
                p.Lineup.RunYawOffsetDeg,
                p.PerturbU,
                p.Lineup.RestScatter,
                p.PredictedBounces,
                realBounces,
                new[] { p.Pos.X, p.Pos.Y, p.Pos.Z },
                new[] { p.Vel.X, p.Vel.Y, p.Vel.Z },
                new[] { p.PredictedRest.X, p.PredictedRest.Y, p.PredictedRest.Z },
                new[] { real.X, real.Y, real.Z },
                detonated,
                err,
                Vector2.Distance(new Vector2(real.X, real.Y), new Vector2(solve.Target.X, solve.Target.Y)),
                divergenceTick,
                divergenceClass));
        }

        if (errors.Count == 0)
        {
            Console.Error.WriteLine("no captures matched; nothing to grade");
            return 1;
        }
        errors.Sort();
        perturbErrors.Sort();
        var perturbed = perturbErrors.Count == 0 ? null : new
        {
            radius = perturb,
            count = perturbErrors.Count,
            errMedian = perturbErrors[perturbErrors.Count / 2],
            errP90 = perturbErrors[(int)(perturbErrors.Count * 0.9)],
            errMax = perturbErrors[^1],
            within1 = perturbErrors.Count(e => e <= 1f),
            within2 = perturbErrors.Count(e => e <= 2f),
        };
        var summary = new
        {
            lineups = plans.Count,
            submitted,
            matched = matches.Count,
            notDetonated,
            culled,
            passRadius,
            withinPass,
            within1,
            within2,
            within8,
            errMedian = errors[errors.Count / 2],
            errMean = errors.Average(),
            errP90 = errors[(int)(errors.Count * 0.9)],
            errMax = errors[^1],
            perturbed,
        };
        Console.WriteLine($"predicted-vs-real rest error: median {summary.errMedian:F1}u  mean {summary.errMean:F1}u  p90 {summary.errP90:F1}u  max {summary.errMax:F1}u");
        Console.WriteLine($"within {passRadius:F0}u: {withinPass}/{errors.Count} ({100.0 * withinPass / errors.Count:F0}%)   within 8u: {within8}/{errors.Count} ({100.0 * within8 / errors.Count:F0}%)   failed to detonate: {notDetonated}   culled by engine (excluded): {culled}");
        if (perturbed != null)
        {
            Console.WriteLine($"one-tick probes (+-{perturbed.radius:F2}u): {perturbed.count} thrown, median {perturbed.errMedian:F1}u, p90 {perturbed.errP90:F1}u, max {perturbed.errMax:F1}u, within 1u {100.0 * perturbed.within1 / perturbed.count:F0}%, within 2u {100.0 * perturbed.within2 / perturbed.count:F0}%");
        }
        Console.WriteLine("worst 10 (pre-triaged gap candidates):");
        foreach (var row in results.OrderByDescending(r => r.ErrPredicted).Take(10))
        {
            Console.WriteLine($"  [{row.Index}] err {row.ErrPredicted:F0}u {row.DivergenceClass}@tick{row.DivergenceTick}  {row.Type} s={row.Strength:F1} stab={row.Stability:P0} {row.PredictedBounces}b(sim)/{row.RealBounces}b(real)  predicted ({row.PredictedRest[0]:F0},{row.PredictedRest[1]:F0},{row.PredictedRest[2]:F0}) real ({row.RealRest[0]:F0},{row.RealRest[1]:F0},{row.RealRest[2]:F0})");
        }

        Directory.CreateDirectory("data/validation");
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var reportPath = $"data/validation/{mesh.MapName}-{stamp}.json";
        File.WriteAllText(reportPath, JsonSerializer.Serialize(new
        {
            map = mesh.MapName,
            build = mesh.GameBuildId,
            timestamp = DateTime.Now.ToString("o"),
            // Human labels for the dashboard: which spot this was ("A site",
            // a marker name) and which batch run produced it.
            name = options.GetValueOrDefault("name", ""),
            batch = options.GetValueOrDefault("batch", ""),
            target = new[] { solve.Target.X, solve.Target.Y, solve.Target.Z },
            tolerance,
            summary,
            results,
        }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"wrote {reportPath}");
        RebuildValidationIndex();

        var md = new StringBuilder();
        md.AppendLine($"# Validation run: {mesh.MapName} @ ({solve.Target.X:F0},{solve.Target.Y:F0},{solve.Target.Z:F0})");
        md.AppendLine();
        md.AppendLine($"Build {mesh.GameBuildId}, {DateTime.Now:yyyy-MM-dd HH:mm}, tolerance {tolerance:F0}u, pass radius {passRadius:F0}u.");
        md.AppendLine($"{plans.Count} lineups solved, {submitted} thrown, {matches.Count} captured, {notDetonated} failed to detonate.");
        md.AppendLine();
        md.AppendLine($"Overall predicted-vs-real rest error: median {summary.errMedian:F1}u, mean {summary.errMean:F1}u, p90 {summary.errP90:F1}u, max {summary.errMax:F1}u.");
        md.AppendLine($"Within {passRadius:F0}u: {withinPass}/{errors.Count} ({100.0 * withinPass / errors.Count:F0}%); within 8u: {within8}/{errors.Count} ({100.0 * within8 / errors.Count:F0}%).");
        md.AppendLine();
        md.AppendLine("| Segment | n | median | p90 | max | <=pass | <=8u |");
        md.AppendLine("|---|---|---|---|---|---|---|");
        void Segment(string name, List<ValidateRow> rows)
        {
            if (rows.Count == 0)
            {
                return;
            }
            var es = rows.Select(r => r.ErrPredicted).OrderBy(e => e).ToList();
            md.AppendLine($"| {name} | {es.Count} | {es[es.Count / 2]:F1}u | {es[(int)(es.Count * 0.9)]:F1}u | {es[^1]:F1}u | {100.0 * es.Count(e => e <= passRadius) / es.Count:F0}% | {100.0 * es.Count(e => e <= 8f) / es.Count:F0}% |");
        }
        // Comparability: the named segments describe base throws only, so a
        // batch that used perturbation probes reads the same as one that
        // did not; the probes get their own summary row.
        var baseResults = results.Where(r => r.PerturbU == 0f).ToList();
        foreach (var t in new[] { "Stand", "Crouch", "JumpThrow", "CrouchJumpThrow", "RunJumpThrow" })
        {
            Segment(t, baseResults.Where(r => r.Type == t).ToList());
        }
        Segment("bounces 0-4", baseResults.Where(r => r.PredictedBounces <= 4).ToList());
        Segment("bounces 5-30", baseResults.Where(r => r.PredictedBounces is > 4 and <= 30).ToList());
        Segment("bounces >30", baseResults.Where(r => r.PredictedBounces > 30).ToList());
        Segment("stability 100%", baseResults.Where(r => r.Stability >= 0.99f).ToList());
        Segment("stability <100%", baseResults.Where(r => r.Stability < 0.99f).ToList());
        Segment($"one-tick probes ±{perturb:F2}u", results.Where(r => r.PerturbU > 0f).ToList());
        md.AppendLine();
        md.AppendLine("## Worst 10 (pre-triaged)");
        md.AppendLine();
        md.AppendLine("| # | err | class | tick | type | click | stab | sim/real bounces | predicted rest | real rest |");
        md.AppendLine("|---|---|---|---|---|---|---|---|---|---|");
        foreach (var row in results.OrderByDescending(r => r.ErrPredicted).Take(10))
        {
            var click = ClickName(row.Strength);
            md.AppendLine($"| {row.Index} | {row.ErrPredicted:F0}u | {row.DivergenceClass} | {row.DivergenceTick} | {row.Type} | {click} | {row.Stability:P0} | {row.PredictedBounces}/{row.RealBounces} | ({row.PredictedRest[0]:F0},{row.PredictedRest[1]:F0},{row.PredictedRest[2]:F0}) | ({row.RealRest[0]:F0},{row.RealRest[1]:F0},{row.RealRest[2]:F0}) |");
        }
        md.AppendLine();
        md.AppendLine("Divergence class counts across misses (>8u): " + string.Join(", ",
            results.Where(r => r.ErrPredicted > 8f).GroupBy(r => r.DivergenceClass).OrderByDescending(g => g.Count()).Select(g => $"{g.Key} {g.Count()}")));
        var mdPath = $"data/validation/{mesh.MapName}-{stamp}.md";
        File.WriteAllText(mdPath, md.ToString());
        Console.WriteLine($"wrote {mdPath}");
        return 0;
    }

    /// <summary>
    /// Regenerates data/validation/index.json from every report on disk. The
    /// dashboard reads only this file to enumerate runs, so the index is a pure
    /// derivation - rebuilt whole each time rather than appended to, which also
    /// heals it after reports are hand-deleted or synced from another machine.
    /// </summary>
    public static void RebuildValidationIndex(string dir = "data/validation")
    {
        if (!Directory.Exists(dir))
        {
            return;
        }
        var runs = new List<object>();
        foreach (var path in Directory.EnumerateFiles(dir, "*.json").OrderBy(p => p, StringComparer.Ordinal))
        {
            var file = Path.GetFileName(path);
            if (file == "index.json")
            {
                continue;
            }
            try
            {
                var doc = JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(path));
                // Older reports predate the name/batch labels; they index fine
                // with empty strings.
                runs.Add(new
                {
                    file,
                    map = doc.GetProperty("map").GetString(),
                    name = doc.TryGetProperty("name", out var nameEl) ? nameEl.GetString() : "",
                    batch = doc.TryGetProperty("batch", out var batchEl) ? batchEl.GetString() : "",
                    timestamp = doc.GetProperty("timestamp").GetString(),
                    target = doc.GetProperty("target").EnumerateArray().Select(e => e.GetSingle()).ToArray(),
                    summary = doc.GetProperty("summary"),
                });
            }
            catch (Exception e) when (e is JsonException or KeyNotFoundException)
            {
                Console.Error.WriteLine($"index: skipping unreadable report {file}: {e.Message}");
            }
        }
        var indexPath = Path.Combine(dir, "index.json");
        // Temp+rename so a dashboard fetch can never observe a half-written index.
        var temp = indexPath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(new
        {
            generated = DateTime.Now.ToString("o"),
            runs,
        }, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temp, indexPath, overwrite: true);
        Console.WriteLine($"index: {runs.Count} run(s) -> {indexPath}");
    }
}

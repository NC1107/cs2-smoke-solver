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

public static class PointLineupCommand
{
    // Fixed-position inverse solve: the stand spot is given (player's exact feet)
    // and we search angles x click x movement for a throw that rests on the
    // target. Coarse grid aimed at the target, then two refinement passes.
    public static int Run(Dictionary<string, string> options)
    {
        if (!options.ContainsKey("attrs"))
        {
            options["attrs"] = "Default,default,EntitySolid";
        }
        var (mesh, _, _, attributeFilter) = LoadCommon(options);
        var constants = LoadConstants(options);
        var feet = ParseVec(Require(options, "from"));
        var (target, _) = ParseVec2or3(Require(options, "target"));

        var (meshMin, meshMax) = mesh.ComputeBounds();
        var lo = Vector3.Min(feet, target) - new Vector3(700, 700, 250);
        var hi = Vector3.Max(feet, target) + new Vector3(700, 700, 950);
        var min = Vector3.Max(lo, meshMin);
        var max = Vector3.Min(hi, meshMax);
        var collider = new TriangleCollider(mesh, min, max, mesh.GrenadeSolidFilter());

        var baseYaw = MathF.Atan2(target.Y - feet.Y, target.X - feet.X) * 180f / MathF.PI;
        var types = new[] { ThrowType.Stand, ThrowType.Crouch, ThrowType.JumpThrow, ThrowType.CrouchJumpThrow };
        var strengths = new[] { 1f, 0.5f, 0f };

        (float Err, ThrowType Type, float Strength, float Pitch, float Yaw, Vector3 RestPos) best =
            (float.MaxValue, ThrowType.Stand, 1f, 0, 0, default);
        // Simpler executions win near-ties.
        float TypePenalty(ThrowType t) => t switch
        {
            ThrowType.Stand => 0f,
            ThrowType.Crouch => 1f,
            ThrowType.JumpThrow => 3f,
            _ => 4f,
        };

        (float Err, float Pitch, float Yaw, Vector3 RestPos) Simulate(ThrowType type, float strength, float pitch, float yaw)
        {
            var eye = feet + new Vector3(0, 0, GrenadeTrajectory.EyeHeight(type));
            var (pos, vel) = GrenadeTrajectory.DeriveInitial(new ThrowSpec(eye, yaw, pitch, type, strength), constants);
            var sim = GrenadeTrajectory.SimulateExactRaw(collider, pos, vel, constants);
            return (Vector3.Distance(sim.RestPoint, target), pitch, yaw, sim.RestPoint);
        }

        var samples = new List<(float Err, ThrowType Type, float Strength, float Pitch, float Yaw, Vector3 RestPos)>();
        void CoarseSweep(float yawRange, float yawStep)
        {
            // Independent simulations over a read-only collider: fan the
            // (type, strength, yaw) columns across cores and merge.
            var columns = new List<(ThrowType Type, float Strength, float Yaw)>();
            foreach (var type in types)
            {
                foreach (var strength in strengths)
                {
                    for (var yaw = baseYaw - yawRange; yaw <= baseYaw + yawRange; yaw += yawStep)
                    {
                        columns.Add((type, strength, yaw));
                    }
                }
            }
            var merged = new System.Collections.Concurrent.ConcurrentBag<(float, ThrowType, float, float, float, Vector3)>();
            Parallel.ForEach(columns, column =>
            {
                for (var pitch = -86f; pitch <= 2f; pitch += 4f)
                {
                    var s = Simulate(column.Type, column.Strength, pitch, column.Yaw);
                    merged.Add((s.Err, column.Type, column.Strength, pitch, column.Yaw, s.RestPos));
                }
            });
            samples.AddRange(merged);
        }

        // Bounce landscapes are non-convex: refining only the single best coarse
        // cell strands the answer in one basin. Refine the best N cells that are
        // meaningfully apart instead, and escalate to wall-bank yaws if needed.
        void RefineTopCandidates(int topN)
        {
            var accepted = new List<(float Err, ThrowType Type, float Strength, float Pitch, float Yaw, Vector3 RestPos)>();
            foreach (var c in samples.OrderBy(s => s.Err + TypePenalty(s.Type)))
            {
                if (accepted.Count >= topN) { break; }
                if (accepted.Any(a => a.Type == c.Type && a.Strength == c.Strength &&
                                      MathF.Abs(a.Yaw - c.Yaw) < 6f && MathF.Abs(a.Pitch - c.Pitch) < 8f))
                {
                    continue;
                }
                accepted.Add(c);
            }
            var refined = new System.Collections.Concurrent.ConcurrentBag<(float Err, ThrowType Type, float Strength, float Pitch, float Yaw, Vector3 RestPos)>();
            Parallel.ForEach(accepted, c =>
            {
                var local = c;
                foreach (var (range, step) in new[] { (2.2f, 0.4f), (0.45f, 0.08f) })
                {
                    for (var yaw = local.Yaw - range; yaw <= local.Yaw + range; yaw += step)
                    {
                        for (var pitch = local.Pitch - range; pitch <= local.Pitch + range; pitch += step)
                        {
                            var s = Simulate(c.Type, c.Strength, pitch, yaw);
                            if (s.Err < local.Err)
                            {
                                local = (s.Err, c.Type, c.Strength, s.Pitch, s.Yaw, s.RestPos);
                            }
                        }
                    }
                }
                refined.Add(local);
            });
            foreach (var local in refined)
            {
                if (local.Err + TypePenalty(local.Type) < best.Err + TypePenalty(best.Type))
                {
                    best = local;
                }
            }
        }

        // quick: aimed sweep with bank-shot escalation; deep: exhaustive 360
        // sweep at finer steps for the best achievable answer.
        if (options.GetValueOrDefault("mode", "quick") == "deep")
        {
            CoarseSweep(180f, 2f);
            RefineTopCandidates(20);
        }
        else
        {
            CoarseSweep(32f, 2.5f);
            RefineTopCandidates(10);
            if (best.Err > 8f)
            {
                samples.Clear();
                CoarseSweep(90f, 3f);
                RefineTopCandidates(10);
            }
        }

        var tolerance = float.Parse(options.GetValueOrDefault("tolerance", "32"), CultureInfo.InvariantCulture);
        var aim = AimReferencePoint(collider, feet, best.Type, best.Pitch, best.Yaw);
        var bestEye = feet + new Vector3(0, 0, GrenadeTrajectory.EyeHeight(best.Type));
        var (initPos, initVel) = GrenadeTrajectory.DeriveInitial(new ThrowSpec(bestEye, best.Yaw, best.Pitch, best.Type, best.Strength), constants);
        var result = new
        {
            found = best.Err <= tolerance,
            err = best.Err,
            pitch = best.Pitch,
            yaw = best.Yaw,
            type = best.Type.ToString(),
            strength = best.Strength,
            rest = new[] { best.RestPos.X, best.RestPos.Y, best.RestPos.Z },
            aim = new[] { aim.X, aim.Y, aim.Z },
            initpos = new[] { initPos.X, initPos.Y, initPos.Z },
            initvel = new[] { initVel.X, initVel.Y, initVel.Z },
        };
        Console.WriteLine(JsonSerializer.Serialize(result));
        return 0;
    }
}

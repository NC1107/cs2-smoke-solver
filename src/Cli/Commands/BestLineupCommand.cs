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

public static class BestLineupCommand
{
    // End-to-end accuracy pipeline: solve a target, throw every lineup's exact
    // initial conditions on the live rig server via the CalibrationThrower request
    // file, match the captured real rest points back to predictions, and grade.
    // This validates flight physics + map geometry (initial conditions are
    // injected, so player release derivation is exercised only in the sim).
    // Nearest practical lineup for a target, ranked by walking distance from the
    // player's current position (movement-type penalties keep "walk 50u further"
    // preferable to "hit a run-jump-throw"). Emits one JSON object for the watcher.
    public static int Run(Dictionary<string, string> options)
    {
        if (!options.ContainsKey("attrs"))
        {
            options["attrs"] = "Default,default,EntitySolid";
        }
        var (mesh, _, _, attributeFilter) = LoadCommon(options);
        var navAreas = LoadJson<List<NavAreaJson>>(Require(options, "nav"), "nav areas");
        var constants = LoadConstants(options);
        var (target, hasZ) = ParseVec2or3(Require(options, "target"));
        var near = ParseVec(Require(options, "near"));

        var solve = SolveForTarget(mesh, attributeFilter, navAreas, target, hasZ, null, 3100f, 80f, constants);
        if (solve.Lineups.Count == 0)
        {
            Console.WriteLine("{\"found\":false}");
            return 0;
        }
        float TypePenalty(ThrowType t) => t switch
        {
            ThrowType.Stand or ThrowType.Crouch => 0f,
            ThrowType.JumpThrow or ThrowType.CrouchJumpThrow => 200f,
            _ => 400f,
        };
        var best = solve.Lineups
            .OrderBy(l => Vector3.Distance(l.Feet, near) + TypePenalty(l.Type))
            .First();
        var aim = AimReferencePoint(solve.Collider, best.Feet, best.Type, best.PitchDeg, best.YawDeg);
        var result = new
        {
            found = true,
            feet = new[] { best.Feet.X, best.Feet.Y, best.Feet.Z },
            pitch = best.PitchDeg,
            yaw = best.YawDeg,
            type = best.Type.ToString(),
            strength = best.Strength,
            dist = Vector3.Distance(best.Feet, near),
            candidates = solve.Lineups.Count,
            aim = new[] { aim.X, aim.Y, aim.Z },
        };
        Console.WriteLine(JsonSerializer.Serialize(result));
        return 0;
    }
}

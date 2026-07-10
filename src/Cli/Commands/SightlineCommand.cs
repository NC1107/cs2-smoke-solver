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

public static class SightlineCommand
{
    public static int Run(Dictionary<string, string> options)
    {
        var from = ParseVec(Require(options, "from"));
        var to = ParseVec(Require(options, "to"));
        var restPoint = ParseVec(Require(options, "rest"));
        var (_, smoke, mesh, attributeFilter) = BuildAndFill(options, restPoint, from, to);

        var result = Occlusion.Test(smoke, from, to);
        var exactBlocked = new TriangleRaycaster(mesh, Vector3.Min(from, to) - Vector3.One, Vector3.Max(from, to) + Vector3.One, attributeFilter).Blocked(from, to);
        Console.WriteLine($"sightline: {result.SmokeCellsCrossed} smoke cells crossed, geometry blocked: voxel={result.GeometryBlocked} exact={exactBlocked}");
        if (result.FirstSolidHit is { } hit)
        {
            Console.WriteLine($"first solid voxel on ray: ({hit.X:F0},{hit.Y:F0},{hit.Z:F0})");
        }
        var blocked = result.SmokeBlocked(minSmokeCells: 3);
        Console.WriteLine(blocked ? "BLOCKED by smoke" : "NOT blocked by smoke");
        return blocked ? 0 : 2;
    }
}

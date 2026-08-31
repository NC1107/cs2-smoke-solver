using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json;
using SmokeSolver.Extraction;
using SmokeSolver.Sim;
using SmokeSolver.Solver;

using static SmokeSolver.Cli.CliParsing;
using static SmokeSolver.Cli.MeshSetup;
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

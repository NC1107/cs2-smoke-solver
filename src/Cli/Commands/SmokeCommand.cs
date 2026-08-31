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

public static class SmokeCommand
{
    public static int Run(Dictionary<string, string> options)
    {
        var restPoint = ParseVec(Require(options, "rest"));
        var (grid, smoke, _, _) = BuildAndFill(options, restPoint);

        if (options.TryGetValue("obj", out var objPath))
        {
            VoxelObj.Save(grid.VoxelSize, smoke.Cells.Select(grid.CellCenter), objPath);
            Console.WriteLine($"wrote {objPath}");
        }
        return smoke.Cells.Length > 0 ? 0 : 2;
    }
}

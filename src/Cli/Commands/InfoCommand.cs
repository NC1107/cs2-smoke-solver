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

public static class InfoCommand
{
    public static int Run(Dictionary<string, string> options)
    {
        var mesh = CollisionMesh.Load(Require(options, "geo"));
        var (min, max) = mesh.ComputeBounds();
        Console.WriteLine($"map: {mesh.MapName} (build {mesh.GameBuildId})");
        Console.WriteLine($"triangles: {mesh.TriangleCount}");
        Console.WriteLine($"bounds: min=({min.X:F0},{min.Y:F0},{min.Z:F0}) max=({max.X:F0},{max.Y:F0},{max.Z:F0})");
        var counts = new int[mesh.AttributeNames.Length];
        foreach (var a in mesh.TriangleAttributes)
        {
            counts[a]++;
        }
        for (var i = 0; i < mesh.AttributeNames.Length; i++)
        {
            Console.WriteLine($"attribute[{i}] {mesh.AttributeNames[i]}: {counts[i]} triangles");
        }
        return 0;
    }
}

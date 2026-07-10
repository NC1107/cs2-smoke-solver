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

public static class ExtractCommand
{
    public static int Run(Dictionary<string, string> options)
    {
        var gameDir = Require(options, "game");
        var map = Require(options, "map");
        var outDir = options.GetValueOrDefault("out", "data");
        Directory.CreateDirectory(outDir);

        var vpkPath = Path.Combine(gameDir, "game", "csgo", "maps", $"{map}.vpk");
        var buildId = ReadBuildId(gameDir);
        Console.WriteLine($"extracting {map} (build {buildId}) from {vpkPath}");

        var mesh = MapExtractor.ExtractWorldPhysics(vpkPath, map, buildId);
        var geoPath = Path.Combine(outDir, $"{map}.s2geo");
        mesh.Save(geoPath);

        var (min, max) = mesh.ComputeBounds();
        Console.WriteLine($"  {mesh.TriangleCount} triangles, {mesh.Vertices.Length / 3} vertices");
        Console.WriteLine($"  bounds min=({min.X:F0},{min.Y:F0},{min.Z:F0}) max=({max.X:F0},{max.Y:F0},{max.Z:F0})");
        Console.WriteLine($"  collision attributes: {string.Join(", ", mesh.AttributeNames)}");
        Console.WriteLine($"  wrote {geoPath}");

        var navData = MapExtractor.ExtractNavFile(vpkPath, map);
        var navPath = Path.Combine(outDir, $"{map}.nav");
        File.WriteAllBytes(navPath, navData);
        Console.WriteLine($"  wrote {navPath}");
        var navAreas = MapExtractor.ExtractNavAreas(navData);
        var navAreasPath = Path.Combine(outDir, $"{map}.navareas.json");
        File.WriteAllText(navAreasPath, JsonSerializer.Serialize(navAreas));
        Console.WriteLine($"  wrote {navAreasPath} ({navAreas.Count} walkable areas)");

        var entities = MapExtractor.ExtractEntities(vpkPath);
        var entitiesPath = Path.Combine(outDir, $"{map}.entities.json");
        File.WriteAllText(entitiesPath, JsonSerializer.Serialize(entities, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"  wrote {entitiesPath} ({entities.Count} entities)");

        if (options.ContainsKey("obj"))
        {
            var objPath = Path.Combine(outDir, $"{map}.obj");
            mesh.SaveObj(objPath);
            Console.WriteLine($"  wrote {objPath}");
        }
        return 0;
    }
}

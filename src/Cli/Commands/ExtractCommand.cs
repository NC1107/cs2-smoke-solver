using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json;
using SmokeSolver.Extraction;
using SmokeSolver.Sim;
using SmokeSolver.Solver;

using static SmokeSolver.Cli.CliParsing;
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

        if (options.TryGetValue("solid-classes", out var classes))
        {
            MapExtractor.SolidEntityClassOverride = classes.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            Console.WriteLine($"  solid entity classes: {string.Join(", ", MapExtractor.SolidEntityClassOverride)}");
        }
        if (options.ContainsKey("dump"))
        {
            MapExtractor.Diagnostics = line => Console.WriteLine("  " + line);
        }
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

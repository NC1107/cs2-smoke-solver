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

public static class ExportGltfCommand
{
    // Textured render-mesh export for the 3D viewer: VRF walks the world resource
    // (worldnodes, aggregates, entity models) and writes a GLB with materials and
    // textures resolved from the map VPK plus the game's mounted search paths.
    public static int Run(Dictionary<string, string> options)
    {
        var vpkPath = Path.GetFullPath(Require(options, "vpk"));
        var outPath = options.GetValueOrDefault("out", "data/de_dust2.glb");
        var package = new SteamDatabase.ValvePak.Package();
        package.Read(vpkPath);
        var entry = (package.Entries.TryGetValue("vwrld_c", out var worlds) ? worlds : [])
            .FirstOrDefault()
            ?? throw new FileNotFoundException($"no world resource (.vwrld_c) inside {vpkPath}");
        Console.WriteLine($"exporting {entry.GetFullPath()} with materials...");
        package.ReadEntry(entry, out var raw);
        var resource = new ValveResourceFormat.Resource { FileName = entry.GetFullPath() };
        resource.Read(new MemoryStream(raw));

        using var loader = new ValveResourceFormat.IO.GameFileLoader(package, vpkPath);
        var exporter = new ValveResourceFormat.IO.GltfModelExporter(loader)
        {
            ExportMaterials = true,
            AdaptTextures = true,
            ProgressReporter = new Progress<string>(s => Console.WriteLine($"  {s}")),
        };
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);
        exporter.Export(resource, Path.GetFullPath(outPath), default);
        Console.WriteLine($"wrote {outPath} ({new FileInfo(outPath).Length / 1e6:F0} MB)");
        return 0;
    }
}

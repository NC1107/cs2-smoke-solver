using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json;
using SmokeSolver.Extraction;
using SmokeSolver.Sim;
using SmokeSolver.Solver;

using static SmokeSolver.Cli.CliParsing;
namespace SmokeSolver.Cli;

public static class ExportGltfCommand
{
    // Textured render-mesh export for the 3D viewer: VRF walks the world resource
    // (worldnodes, aggregates, entity models) and writes a GLB with materials and
    // textures resolved from the map VPK plus the game's mounted search paths.
    public static int Run(Dictionary<string, string> options)
    {
        var vpkPath = Path.GetFullPath(Require(options, "vpk"));
        // Default names the output after the map itself (de_mirage.vpk -> de_mirage.glb)
        // rather than a fixed literal, so exporting a different map's --vpk doesn't
        // require remembering to override --out too.
        var outPath = options.GetValueOrDefault("out", $"data/{Path.GetFileNameWithoutExtension(vpkPath)}.glb");
        ExportGeometry(vpkPath, outPath, exportMaterials: true, adaptTextures: true, exportExtras: true,
            new Progress<string>(s => Console.WriteLine($"  {s}")));
        Console.WriteLine($"wrote {outPath} ({new FileInfo(outPath).Length / 1e6:F0} MB)");
        return 0;
    }

    /// <summary>
    /// Loads a map's render world (worldnodes, aggregates, entity models) via VRF
    /// and writes it out as GLB. Shared by exportgltf (the viewer's textured
    /// preview, materials + adapted textures) and meshdiff (render-vs-physics
    /// geometry comparison, materials only - no texture work) so both walk the
    /// exact same world-loading path instead of meshdiff re-deriving its own.
    /// </summary>
    public static void ExportGeometry(
        string vpkPath, string outGlbPath, bool exportMaterials, bool adaptTextures, bool exportExtras, IProgress<string> progress)
    {
        using var package = new SteamDatabase.ValvePak.Package();
        package.Read(vpkPath);
        var entry = (package.Entries?.TryGetValue("vwrld_c", out var worlds) == true ? worlds : [])
            .FirstOrDefault()
            ?? throw new FileNotFoundException($"no world resource (.vwrld_c) inside {vpkPath}");
        Console.WriteLine($"exporting {entry.GetFullPath()}{(exportMaterials ? " with materials" : "")}...");
        package.ReadEntry(entry, out var raw);
        using var resource = new ValveResourceFormat.Resource { FileName = entry.GetFullPath() };
        resource.Read(new MemoryStream(raw));

        using var loader = new ValveResourceFormat.IO.GameFileLoader(package, vpkPath);
        var exporter = new ValveResourceFormat.IO.GltfModelExporter(loader)
        {
            ExportMaterials = exportMaterials,
            AdaptTextures = adaptTextures,
            ExportExtras = exportExtras,
            ProgressReporter = progress,
        };
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outGlbPath))!);
        exporter.Export(resource, Path.GetFullPath(outGlbPath), default);
    }
}

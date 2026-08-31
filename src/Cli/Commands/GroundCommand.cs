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

public static class GroundCommand
{
    public static int Run(Dictionary<string, string> options)
    {
        var fromParts = Require(options, "from").Split(',');
        var toParts = Require(options, "to").Split(',');
        var from = new Vector2(float.Parse(fromParts[0], CultureInfo.InvariantCulture), float.Parse(fromParts[1], CultureInfo.InvariantCulture));
        var to = new Vector2(float.Parse(toParts[0], CultureInfo.InvariantCulture), float.Parse(toParts[1], CultureInfo.InvariantCulture));
        var steps = int.Parse(options.GetValueOrDefault("steps", "20"), CultureInfo.InvariantCulture);
        var zMax = float.Parse(options.GetValueOrDefault("zmax", "500"), CultureInfo.InvariantCulture);

        var (mesh, voxelSize, _, attributeFilter) = LoadCommon(options);
        var (meshMin, _) = mesh.ComputeBounds();
        var min = new Vector3(MathF.Min(from.X, to.X) - 32, MathF.Min(from.Y, to.Y) - 32, meshMin.Z);
        var max = new Vector3(MathF.Max(from.X, to.X) + 32, MathF.Max(from.Y, to.Y) + 32, zMax);
        var grid = BuildGrid(mesh, voxelSize, min, max, attributeFilter);

        Console.WriteLine("x\ty\tground_z");
        for (var i = 0; i <= steps; i++)
        {
            var t = (float)i / steps;
            var p = Vector2.Lerp(from, to, t);
            var (x, y, _) = grid.CellOf(new Vector3(p.X, p.Y, 0));
            var groundZ = float.NaN;
            for (var z = grid.Nz - 2; z >= 0; z--)
            {
                if (grid.IsSolid(grid.Index(x, y, z)) && !grid.IsSolid(grid.Index(x, y, z + 1)))
                {
                    groundZ = grid.CellCenter(x, y, z).Z + grid.VoxelSize / 2;
                    break;
                }
            }
            Console.WriteLine($"{p.X:F0}\t{p.Y:F0}\t{groundZ:F0}");
        }
        return 0;
    }
}

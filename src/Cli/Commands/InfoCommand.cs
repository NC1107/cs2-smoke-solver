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

        // --layers: the grenade-solidity decision behind every attribute group,
        // with each group's spatial extent - the diagnostic for "the sim thinks
        // this surface blocks a smoke that flies through it in-game". A group's
        // NAME is ambiguous; the interactAs layers and the resulting solid/pass
        // verdict are what matter, and the bounds say WHERE it is on the map.
        if (options.ContainsKey("layers"))
        {
            var grenadeSolid = mesh.GrenadeSolidFilter();
            var playerSolid = mesh.PlayerSolidFilter();
            var mins = new Vector3[mesh.AttributeNames.Length];
            var maxs = new Vector3[mesh.AttributeNames.Length];
            Array.Fill(mins, new Vector3(float.MaxValue));
            Array.Fill(maxs, new Vector3(float.MinValue));
            for (var t = 0; t < mesh.TriangleAttributes.Length; t++)
            {
                var a = mesh.TriangleAttributes[t];
                for (var k = 0; k < 3; k++)
                {
                    var vi = mesh.Indices[t * 3 + k] * 3;
                    var v = new Vector3(mesh.Vertices[vi], mesh.Vertices[vi + 1], mesh.Vertices[vi + 2]);
                    mins[a] = Vector3.Min(mins[a], v);
                    maxs[a] = Vector3.Max(maxs[a], v);
                }
            }
            Console.WriteLine();
            Console.WriteLine("layers (grenade-solid decision + extent):");
            for (var i = 0; i < mesh.AttributeNames.Length; i++)
            {
                if (counts[i] == 0)
                {
                    continue;
                }
                var layers = mesh.AttributeInteractAs[i];
                var layerStr = layers.Length == 0 ? "(none)" : string.Join(",", layers);
                Console.WriteLine(
                    $"  [{i}] {mesh.AttributeNames[i]} x{counts[i]}  interactAs={layerStr}  " +
                    $"grenade={(grenadeSolid((byte)i) ? "SOLID" : "pass")} player={(playerSolid((byte)i) ? "solid" : "pass")}");
                Console.WriteLine(
                    $"       extent ({mins[i].X:F0},{mins[i].Y:F0},{mins[i].Z:F0})..({maxs[i].X:F0},{maxs[i].Y:F0},{maxs[i].Z:F0})");
            }
        }
        // --ray x0,y0,z0,x1,y1,z1: every collision triangle a straight segment
        // crosses, with the attribute group and its grenade verdict - answers
        // "what is on this path" for a spot where a throw is unexpectedly
        // blocked. The smoke arcs rather than flies straight, so this is a
        // line-of-sight probe, not the throw itself, but it names the surfaces
        // sitting between two points.
        if (options.TryGetValue("ray", out var rayRaw))
        {
            var p = rayRaw.Split(',').Select(s => float.Parse(s, CultureInfo.InvariantCulture)).ToArray();
            if (p.Length != 6)
            {
                Console.Error.WriteLine("--ray needs x0,y0,z0,x1,y1,z1");
                return 1;
            }
            var from = new Vector3(p[0], p[1], p[2]);
            var to = new Vector3(p[3], p[4], p[5]);
            var dir = to - from;
            var grenadeSolid = mesh.GrenadeSolidFilter();
            var crossings = new List<(float T, Vector3 At, byte Attr)>();
            for (var t = 0; t < mesh.TriangleCount; t++)
            {
                var i0 = mesh.Indices[t * 3] * 3;
                var i1 = mesh.Indices[t * 3 + 1] * 3;
                var i2 = mesh.Indices[t * 3 + 2] * 3;
                var a = new Vector3(mesh.Vertices[i0], mesh.Vertices[i0 + 1], mesh.Vertices[i0 + 2]);
                var b = new Vector3(mesh.Vertices[i1], mesh.Vertices[i1 + 1], mesh.Vertices[i1 + 2]);
                var c = new Vector3(mesh.Vertices[i2], mesh.Vertices[i2 + 1], mesh.Vertices[i2 + 2]);
                // Moller-Trumbore, hit accepted only within the segment [0,1].
                var e1 = b - a;
                var e2 = c - a;
                var pv = Vector3.Cross(dir, e2);
                var det = Vector3.Dot(e1, pv);
                if (MathF.Abs(det) < 1e-6f)
                {
                    continue;
                }
                var inv = 1f / det;
                var tv = from - a;
                var u = Vector3.Dot(tv, pv) * inv;
                if (u is < 0f or > 1f)
                {
                    continue;
                }
                var qv = Vector3.Cross(tv, e1);
                var v = Vector3.Dot(dir, qv) * inv;
                if (v < 0f || u + v > 1f)
                {
                    continue;
                }
                var hit = Vector3.Dot(e2, qv) * inv;
                if (hit is >= 0f and <= 1f)
                {
                    crossings.Add((hit, from + dir * hit, mesh.TriangleAttributes[t]));
                }
            }
            crossings.Sort((x, y) => x.T.CompareTo(y.T));
            Console.WriteLine($"ray ({from.X:F0},{from.Y:F0},{from.Z:F0})->({to.X:F0},{to.Y:F0},{to.Z:F0}): {crossings.Count} triangle crossing(s)");
            foreach (var (t, at, attr) in crossings)
            {
                var layers = mesh.AttributeInteractAs[attr];
                Console.WriteLine(
                    $"  t={t:F3} ({at.X:F0},{at.Y:F0},{at.Z:F0})  [{attr}] {mesh.AttributeNames[attr]} " +
                    $"interactAs={(layers.Length == 0 ? "(none)" : string.Join(",", layers))} " +
                    $"grenade={(grenadeSolid(attr) ? "SOLID" : "pass")}");
            }
        }
        // --box x0,y0,z0,x1,y1,z1: which collision attribute groups have triangles
        // inside a volume, with per-group counts and extents. Pinpoints what is
        // sealing a specific gap (a doorway, a window) when a ray keeps clipping
        // the frame - answers "what collision is actually in this box".
        if (options.TryGetValue("box", out var boxRaw))
        {
            var b = boxRaw.Split(',').Select(s => float.Parse(s, CultureInfo.InvariantCulture)).ToArray();
            if (b.Length != 6)
            {
                Console.Error.WriteLine("--box needs x0,y0,z0,x1,y1,z1");
                return 1;
            }
            var lo = new Vector3(MathF.Min(b[0], b[3]), MathF.Min(b[1], b[4]), MathF.Min(b[2], b[5]));
            var hi = new Vector3(MathF.Max(b[0], b[3]), MathF.Max(b[1], b[4]), MathF.Max(b[2], b[5]));
            var grenadeSolid = mesh.GrenadeSolidFilter();
            var inBox = new int[mesh.AttributeNames.Length];
            var bmin = new Vector3[mesh.AttributeNames.Length];
            var bmax = new Vector3[mesh.AttributeNames.Length];
            Array.Fill(bmin, new Vector3(float.MaxValue));
            Array.Fill(bmax, new Vector3(float.MinValue));
            for (var t = 0; t < mesh.TriangleCount; t++)
            {
                var i0 = mesh.Indices[t * 3] * 3;
                var i1 = mesh.Indices[t * 3 + 1] * 3;
                var i2 = mesh.Indices[t * 3 + 2] * 3;
                var v0 = new Vector3(mesh.Vertices[i0], mesh.Vertices[i0 + 1], mesh.Vertices[i0 + 2]);
                var v1 = new Vector3(mesh.Vertices[i1], mesh.Vertices[i1 + 1], mesh.Vertices[i1 + 2]);
                var v2 = new Vector3(mesh.Vertices[i2], mesh.Vertices[i2 + 1], mesh.Vertices[i2 + 2]);
                var cen = (v0 + v1 + v2) / 3f;
                if (cen.X < lo.X || cen.X > hi.X || cen.Y < lo.Y || cen.Y > hi.Y || cen.Z < lo.Z || cen.Z > hi.Z)
                {
                    continue;
                }
                var attr = mesh.TriangleAttributes[t];
                inBox[attr]++;
                bmin[attr] = Vector3.Min(bmin[attr], Vector3.Min(v0, Vector3.Min(v1, v2)));
                bmax[attr] = Vector3.Max(bmax[attr], Vector3.Max(v0, Vector3.Max(v1, v2)));
            }
            Console.WriteLine($"box ({lo.X:F0},{lo.Y:F0},{lo.Z:F0})..({hi.X:F0},{hi.Y:F0},{hi.Z:F0}):");
            for (var i = 0; i < mesh.AttributeNames.Length; i++)
            {
                if (inBox[i] == 0)
                {
                    continue;
                }
                var layers = mesh.AttributeInteractAs[i];
                Console.WriteLine(
                    $"  [{i}] {mesh.AttributeNames[i]} x{inBox[i]}  interactAs={(layers.Length == 0 ? "(none)" : string.Join(",", layers))} " +
                    $"grenade={(grenadeSolid((byte)i) ? "SOLID" : "pass")}  " +
                    $"extent ({bmin[i].X:F0},{bmin[i].Y:F0},{bmin[i].Z:F0})..({bmax[i].X:F0},{bmax[i].Y:F0},{bmax[i].Z:F0})");
            }
        }
        return 0;
    }
}

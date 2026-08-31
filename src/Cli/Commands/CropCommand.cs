using System.Globalization;
using System.Numerics;
using SmokeSolver.Sim;

using static SmokeSolver.Cli.CliParsing;

namespace SmokeSolver.Cli;

/// <summary>
/// Writes a new .s2geo holding only the triangles inside a box. Built for test
/// fixtures: a solver query already builds its collider from a bounded region,
/// so a mesh cropped to that same region replays bit-identically while being
/// small enough to commit, which is what lets the golden replay test run in CI
/// instead of skipping itself on a checkout without the full map data.
/// </summary>
public static class CropCommand
{
    public static int Run(Dictionary<string, string> options)
    {
        var mesh = CollisionMesh.Load(Require(options, "geo"));
        var b = Require(options, "box").Split(',', StringSplitOptions.TrimEntries)
            .Select(v => float.Parse(v, CultureInfo.InvariantCulture)).ToArray();
        if (b.Length != 6)
        {
            Console.Error.WriteLine("--box needs x0,y0,z0,x1,y1,z1");
            return 1;
        }
        var lo = new Vector3(MathF.Min(b[0], b[3]), MathF.Min(b[1], b[4]), MathF.Min(b[2], b[5]));
        var hi = new Vector3(MathF.Max(b[0], b[3]), MathF.Max(b[1], b[4]), MathF.Max(b[2], b[5]));

        // A triangle is kept when its bounds overlap the box at all, matching
        // how TriangleCollider decides what a region contains - keeping the
        // crop and the collider's own inclusion rule identical is the whole
        // point, so a replay against the crop cannot diverge from the original.
        var vertices = new List<float>();
        var indices = new List<int>();
        var attributes = new List<byte>();
        var remap = new Dictionary<int, int>();
        int MapVertex(int original)
        {
            if (remap.TryGetValue(original, out var mapped))
            {
                return mapped;
            }
            mapped = vertices.Count / 3;
            vertices.Add(mesh.Vertices[original * 3]);
            vertices.Add(mesh.Vertices[original * 3 + 1]);
            vertices.Add(mesh.Vertices[original * 3 + 2]);
            remap[original] = mapped;
            return mapped;
        }
        for (var t = 0; t < mesh.TriangleCount; t++)
        {
            var (i0, i1, i2) = (mesh.Indices[t * 3], mesh.Indices[t * 3 + 1], mesh.Indices[t * 3 + 2]);
            var v0 = new Vector3(mesh.Vertices[i0 * 3], mesh.Vertices[i0 * 3 + 1], mesh.Vertices[i0 * 3 + 2]);
            var v1 = new Vector3(mesh.Vertices[i1 * 3], mesh.Vertices[i1 * 3 + 1], mesh.Vertices[i1 * 3 + 2]);
            var v2 = new Vector3(mesh.Vertices[i2 * 3], mesh.Vertices[i2 * 3 + 1], mesh.Vertices[i2 * 3 + 2]);
            var triMin = Vector3.Min(v0, Vector3.Min(v1, v2));
            var triMax = Vector3.Max(v0, Vector3.Max(v1, v2));
            if (triMax.X < lo.X || triMin.X > hi.X ||
                triMax.Y < lo.Y || triMin.Y > hi.Y ||
                triMax.Z < lo.Z || triMin.Z > hi.Z)
            {
                continue;
            }
            indices.Add(MapVertex(i0));
            indices.Add(MapVertex(i1));
            indices.Add(MapVertex(i2));
            attributes.Add(mesh.TriangleAttributes[t]);
        }

        var cropped = new CollisionMesh
        {
            MapName = mesh.MapName,
            GameBuildId = mesh.GameBuildId,
            Vertices = [.. vertices],
            Indices = [.. indices],
            TriangleAttributes = [.. attributes],
            // Attribute tables are kept whole so indices stay valid and the
            // grenade/player filters classify exactly as they do on the source.
            AttributeNames = mesh.AttributeNames,
            AttributeInteractAs = mesh.AttributeInteractAs,
        };
        var outPath = options.GetValueOrDefault("out", Path.ChangeExtension(Require(options, "geo"), null) + ".crop.s2geo");
        cropped.Save(outPath);
        Console.WriteLine($"cropped {mesh.TriangleCount} -> {cropped.TriangleCount} triangles ({vertices.Count / 3} vertices)");
        Console.WriteLine($"wrote {outPath} ({new FileInfo(outPath).Length / 1024.0:F0} KB)");
        return 0;
    }
}

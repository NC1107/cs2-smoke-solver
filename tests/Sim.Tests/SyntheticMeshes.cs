using SmokeSolver.Sim;

namespace SmokeSolver.Sim.Tests;

static class SyntheticMeshes
{
    /// <summary>
    /// Axis-aligned quad walls assembled into test scenes.
    /// Each quad is two triangles; all triangles get attribute 0 ("default").
    /// </summary>
    public static CollisionMesh FromQuads(IEnumerable<(float[] A, float[] B, float[] C, float[] D)> quads) =>
        FromQuads(
            quads.Select(q => (q.A, q.B, q.C, q.D, (byte)0)),
            ["default"],
            [[]]);

    /// <summary>
    /// Quad scene with per-quad attribute indices and explicit attribute groups,
    /// for tests exercising attribute filters and interaction layers.
    /// </summary>
    public static CollisionMesh FromQuads(
        IEnumerable<(float[] A, float[] B, float[] C, float[] D, byte Attribute)> quads,
        string[] attributeNames,
        string[][] attributeInteractAs)
    {
        var vertices = new List<float>();
        var indices = new List<int>();
        var attributes = new List<byte>();
        foreach (var (a, b, c, d, attribute) in quads)
        {
            var baseIndex = vertices.Count / 3;
            vertices.AddRange(a);
            vertices.AddRange(b);
            vertices.AddRange(c);
            vertices.AddRange(d);
            indices.AddRange([baseIndex, baseIndex + 1, baseIndex + 2]);
            indices.AddRange([baseIndex, baseIndex + 2, baseIndex + 3]);
            attributes.Add(attribute);
            attributes.Add(attribute);
        }
        return new CollisionMesh
        {
            MapName = "synthetic",
            GameBuildId = "test",
            Vertices = [.. vertices],
            Indices = [.. indices],
            TriangleAttributes = [.. attributes],
            AttributeNames = attributeNames,
            AttributeInteractAs = attributeInteractAs,
        };
    }

    /// <summary>Horizontal ground plane at z, spanning [min,max] in x and y.</summary>
    public static (float[], float[], float[], float[]) Ground(float min, float max, float z) =>
        ([min, min, z], [max, min, z], [max, max, z], [min, max, z]);

    /// <summary>Vertical wall in the xz plane at the given y, spanning [min,max] in x and [z0,z1].</summary>
    public static (float[], float[], float[], float[]) WallY(float min, float max, float y, float z0, float z1) =>
        ([min, y, z0], [max, y, z0], [max, y, z1], [min, y, z1]);

    /// <summary>Horizontal ceiling plane at z, spanning [min,max] in x and y.</summary>
    public static (float[], float[], float[], float[]) Ceiling(float min, float max, float z) => Ground(min, max, z);

    /// <summary>Vertical wall in the yz plane at the given x, spanning [minY,maxY] and [z0,z1].</summary>
    public static (float[], float[], float[], float[]) WallX(float x, float minY, float maxY, float z0, float z1) =>
        ([x, minY, z0], [x, maxY, z0], [x, maxY, z1], [x, minY, z1]);
}

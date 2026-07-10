using System.Numerics;

namespace SmokeSolver.Sim;

/// <summary>
/// Debug dump of voxel cells as OBJ boxes, loadable in Blender.
/// </summary>
public static class VoxelObj
{
    public static void Save(float voxelSize, IEnumerable<Vector3> cellCenters, string path)
    {
        using var writer = new StreamWriter(path);
        var half = voxelSize / 2;
        var vertexCount = 0;
        foreach (var c in cellCenters)
        {
            Span<Vector3> corners =
            [
                new(c.X - half, c.Y - half, c.Z - half), new(c.X + half, c.Y - half, c.Z - half),
                new(c.X + half, c.Y + half, c.Z - half), new(c.X - half, c.Y + half, c.Z - half),
                new(c.X - half, c.Y - half, c.Z + half), new(c.X + half, c.Y - half, c.Z + half),
                new(c.X + half, c.Y + half, c.Z + half), new(c.X - half, c.Y + half, c.Z + half),
            ];
            foreach (var v in corners)
            {
                writer.WriteLine($"v {v.X} {v.Y} {v.Z}");
            }
            Span<(int, int, int, int)> faces = [(0, 1, 2, 3), (4, 5, 6, 7), (0, 1, 5, 4), (2, 3, 7, 6), (0, 3, 7, 4), (1, 2, 6, 5)];
            foreach (var (a, b, c2, d) in faces)
            {
                writer.WriteLine($"f {vertexCount + a + 1} {vertexCount + b + 1} {vertexCount + c2 + 1} {vertexCount + d + 1}");
            }
            vertexCount += 8;
        }
    }
}

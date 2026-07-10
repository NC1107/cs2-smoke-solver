using System.Numerics;

namespace SmokeSolver.Sim;

public readonly record struct OcclusionResult(int SmokeCellsCrossed, bool GeometryBlocked, Vector3? FirstSolidHit)
{
    public bool SmokeBlocked(int minSmokeCells) => SmokeCellsCrossed >= minSmokeCells;
}

/// <summary>
/// Marches the segment between two eye positions through the voxel grid
/// (Amanatides-Woo DDA) counting smoke cells crossed and detecting solid hits.
/// </summary>
public static class Occlusion
{
    public static OcclusionResult Test(SmokeVolume smoke, Vector3 eyeA, Vector3 eyeB)
    {
        var grid = smoke.Grid;
        var direction = eyeB - eyeA;
        var length = direction.Length();
        if (length < 1e-4f)
        {
            return new OcclusionResult(0, false, null);
        }
        direction /= length;

        var (x, y, z) = grid.CellOf(eyeA);
        var (endX, endY, endZ) = grid.CellOf(eyeB);

        var stepX = Math.Sign(direction.X);
        var stepY = Math.Sign(direction.Y);
        var stepZ = Math.Sign(direction.Z);

        var tMaxX = TimeToBoundary(eyeA.X, grid.Origin.X, grid.VoxelSize, x, direction.X);
        var tMaxY = TimeToBoundary(eyeA.Y, grid.Origin.Y, grid.VoxelSize, y, direction.Y);
        var tMaxZ = TimeToBoundary(eyeA.Z, grid.Origin.Z, grid.VoxelSize, z, direction.Z);

        var tDeltaX = direction.X == 0 ? float.PositiveInfinity : grid.VoxelSize / MathF.Abs(direction.X);
        var tDeltaY = direction.Y == 0 ? float.PositiveInfinity : grid.VoxelSize / MathF.Abs(direction.Y);
        var tDeltaZ = direction.Z == 0 ? float.PositiveInfinity : grid.VoxelSize / MathF.Abs(direction.Z);

        var smokeCells = 0;
        var geometryBlocked = false;
        Vector3? firstSolidHit = null;

        while (true)
        {
            if (grid.InBounds(x, y, z))
            {
                var index = grid.Index(x, y, z);
                if (grid.IsSolid(index))
                {
                    geometryBlocked = true;
                    firstSolidHit ??= grid.CellCenter(x, y, z);
                }
                else if (smoke.CellSet.Contains(index))
                {
                    smokeCells++;
                }
            }
            if (x == endX && y == endY && z == endZ)
            {
                break;
            }
            if (tMaxX <= tMaxY && tMaxX <= tMaxZ)
            {
                if (tMaxX > length)
                {
                    break;
                }
                x += stepX;
                tMaxX += tDeltaX;
            }
            else if (tMaxY <= tMaxZ)
            {
                if (tMaxY > length)
                {
                    break;
                }
                y += stepY;
                tMaxY += tDeltaY;
            }
            else
            {
                if (tMaxZ > length)
                {
                    break;
                }
                z += stepZ;
                tMaxZ += tDeltaZ;
            }
        }

        return new OcclusionResult(smokeCells, geometryBlocked, firstSolidHit);
    }

    static float TimeToBoundary(float pos, float origin, float voxelSize, int cell, float dir)
    {
        if (dir > 0)
        {
            return (origin + (cell + 1) * voxelSize - pos) / dir;
        }
        if (dir < 0)
        {
            return (origin + cell * voxelSize - pos) / dir;
        }
        return float.PositiveInfinity;
    }
}

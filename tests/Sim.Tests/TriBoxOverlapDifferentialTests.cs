using System.Numerics;
using SmokeSolver.Sim;
using Xunit;

namespace SmokeSolver.Sim.Tests;

// TriangleCollider.BoxTriangleOverlap used to carry its own generic SAT
// implementation; it now delegates to TriBoxOverlap.Test (the voxelizer's
// hand-specialized one). This differential test pins that the two algorithms
// agree, using the deleted implementation as the reference - independently
// written SAT code classically disagrees exactly on degenerate/touching cases,
// so those are covered explicitly alongside a seeded random sweep.
public class TriBoxOverlapDifferentialTests
{
    // The generic separating-axis implementation TriangleCollider used to own,
    // kept verbatim as the reference semantics.
    static bool Reference(Vector3 center, Vector3 h, Vector3 va, Vector3 vb, Vector3 vc)
    {
        var a = va - center;
        var b = vb - center;
        var c = vc - center;

        bool Separated(Vector3 l)
        {
            if (l.LengthSquared() < 1e-10f)
            {
                return false;
            }
            var s0 = Vector3.Dot(a, l);
            var s1 = Vector3.Dot(b, l);
            var s2 = Vector3.Dot(c, l);
            var r = h.X * MathF.Abs(l.X) + h.Y * MathF.Abs(l.Y) + h.Z * MathF.Abs(l.Z);
            return MathF.Min(s0, MathF.Min(s1, s2)) > r || MathF.Max(s0, MathF.Max(s1, s2)) < -r;
        }

        if (Separated(Vector3.UnitX) || Separated(Vector3.UnitY) || Separated(Vector3.UnitZ))
        {
            return false;
        }
        var n = Vector3.Cross(b - a, c - a);
        if (Separated(n))
        {
            return false;
        }
        Span<Vector3> edges = [b - a, c - b, a - c];
        foreach (var e in edges)
        {
            if (Separated(Vector3.Cross(Vector3.UnitX, e)) ||
                Separated(Vector3.Cross(Vector3.UnitY, e)) ||
                Separated(Vector3.Cross(Vector3.UnitZ, e)))
            {
                return false;
            }
        }
        return true;
    }

    [Fact]
    public void AgreesWithReferenceOnRandomCases()
    {
        var rng = new Random(20260715);
        float Coord() => (float)(rng.NextDouble() * 400 - 200);
        var disagreements = 0;
        for (var i = 0; i < 200_000; i++)
        {
            var center = new Vector3(Coord(), Coord(), Coord());
            var half = new Vector3(
                (float)(rng.NextDouble() * 80 + 0.5),
                (float)(rng.NextDouble() * 80 + 0.5),
                (float)(rng.NextDouble() * 80 + 0.5));
            // Bias triangles toward the box so overlap cases are actually hit.
            var a = center + new Vector3(Coord() / 3, Coord() / 3, Coord() / 3);
            var b = a + new Vector3(Coord() / 4, Coord() / 4, Coord() / 4);
            var c = a + new Vector3(Coord() / 4, Coord() / 4, Coord() / 4);
            if (TriBoxOverlap.Test(center, half, a, b, c) != Reference(center, half, a, b, c))
            {
                disagreements++;
            }
        }
        Assert.Equal(0, disagreements);
    }

    // The random case above samples coordinates within +-200 of the origin,
    // which is flatgrass scale, not map scale: de_nuke reaches x=6528 and
    // de_vertigo's geometry sits at z~15368, where a float32 ULP is ~0.001u
    // rather than ~0.00002u. Voxelization runs at those magnitudes on every
    // real map, so the predicate has to agree with the reference there too.
    [Fact]
    public void AgreesWithReferenceAtRealMapCoordinates()
    {
        var rng = new Random(20260830);
        float Span() => (float)(rng.NextDouble() * 400 - 200);
        var disagreements = 0;
        for (var i = 0; i < 200_000; i++)
        {
            // A voxel-sized box anywhere in the extent real maps actually span.
            var center = new Vector3(
                (float)(rng.NextDouble() * 24000 - 12000),
                (float)(rng.NextDouble() * 24000 - 12000),
                (float)(rng.NextDouble() * 17000 - 1500));
            var half = new Vector3(8f);
            var a = center + new Vector3(Span() / 20, Span() / 20, Span() / 20);
            var b = a + new Vector3(Span() / 20, Span() / 20, Span() / 20);
            var c = a + new Vector3(Span() / 20, Span() / 20, Span() / 20);
            if (TriBoxOverlap.Test(center, half, a, b, c) != Reference(center, half, a, b, c))
            {
                disagreements++;
            }
        }
        Assert.Equal(0, disagreements);
    }

    // Map geometry is authored on 8/16u grids, so triangles sitting exactly on
    // a voxel boundary plane are the common case, not a corner case. Voxel
    // bucketing walks the cells CellOf(triMin)..CellOf(triMax) (floor-based, so
    // a boundary belongs to the cell above it) while this predicate is
    // inclusive on both sides, so the cell below a boundary-flush triangle is
    // never asked about it. That is sound because the cell that CONTAINS the
    // triangle is always asked - contact with the skipped cell is exactly the
    // shared face, which carries no volume - but the asymmetry is load-bearing
    // enough to pin: it is why a floor plane can mark the cell above it solid.
    [Fact]
    public void ATriangleFlushWithACellBoundaryOverlapsBothNeighbours()
    {
        var below = new Vector3(0, 0, -8);
        var above = new Vector3(0, 0, 8);
        var half = new Vector3(8f);
        var (a, b, c) = (new Vector3(-4, -4, 0), new Vector3(4, -4, 0), new Vector3(0, 4, 0));

        Assert.True(TriBoxOverlap.Test(below, half, a, b, c), "the cell under the plane overlaps it");
        Assert.True(TriBoxOverlap.Test(above, half, a, b, c), "the cell over the plane overlaps it");
        Assert.Equal(Reference(below, half, a, b, c), TriBoxOverlap.Test(below, half, a, b, c));
        Assert.Equal(Reference(above, half, a, b, c), TriBoxOverlap.Test(above, half, a, b, c));
    }

    [Fact]
    public void AgreesWithReferenceOnDegenerateAndTouchingCases()
    {
        var center = new Vector3(0, 0, 0);
        var half = new Vector3(10, 10, 10);
        (Vector3 A, Vector3 B, Vector3 C)[] cases =
        [
            // Zero-area: all three vertices identical, inside and outside.
            (new(1, 1, 1), new(1, 1, 1), new(1, 1, 1)),
            (new(50, 50, 50), new(50, 50, 50), new(50, 50, 50)),
            // Collinear (degenerate edge cross products).
            (new(-20, 0, 0), new(0, 0, 0), new(20, 0, 0)),
            (new(-20, 30, 0), new(0, 30, 0), new(20, 30, 0)),
            // Face-touching: triangle lying exactly on the +X box face plane.
            (new(10, -5, -5), new(10, 5, -5), new(10, 0, 5)),
            // Edge-touching: triangle grazing a box edge.
            (new(10, 10, -20), new(10, 10, 20), new(30, 30, 0)),
            // Corner-touching.
            (new(10, 10, 10), new(30, 10, 10), new(10, 30, 10)),
            // Axis-aligned triangle fully inside.
            (new(-5, -5, 0), new(5, -5, 0), new(0, 5, 0)),
            // Large triangle enclosing the whole box in its plane.
            (new(-500, -500, 0), new(500, -500, 0), new(0, 500, 0)),
        ];
        foreach (var (a, b, c) in cases)
        {
            Assert.Equal(Reference(center, half, a, b, c), TriBoxOverlap.Test(center, half, a, b, c));
        }
    }
}

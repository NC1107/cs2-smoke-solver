using System.Globalization;
using System.Numerics;
using SmokeSolver.Extraction;
using SmokeSolver.Sim;
using SmokeSolver.Solver;

using static SmokeSolver.Cli.CliParsing;
using static SmokeSolver.Cli.MeshSetup;
namespace SmokeSolver.Cli;

/// <summary>
/// Raw simulator throughput on a real map, for the cold-solve loop: the coarse
/// voxel simulator over a sweep-shaped lattice from one origin, and the exact
/// simulator over a verification-shaped window, with a checksum of every rest
/// point so a speed change that alters any result shows up as a different sum.
/// </summary>
public static class SimBenchCommand
{
    public static int Run(Dictionary<string, string> options)
    {
        var (mesh, _, _, attributeFilter) = LoadCommon(options);
        var origin = ParseVec(options.GetValueOrDefault("from", "-1936,1904,1.1"));
        var target = ParseVec(options.GetValueOrDefault("to", "-2000,1585,34"));
        var (meshMin, meshMax) = mesh.ComputeBounds();
        var min = new Vector3(meshMin.X, meshMin.Y, meshMin.Z);
        var max = new Vector3(meshMax.X, meshMax.Y, MathF.Min(meshMax.Z + 64, target.Z + 900));
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var grid = VoxelGrid.Build(mesh, 16f, min, max, attributeFilter);
        Console.WriteLine($"grid {grid.Nx}x{grid.Ny}x{grid.Nz} = {grid.CellCount / 1e6:F1}M cells, {grid.SolidCount / 1e6:F1}M solid, built in {sw.Elapsed.TotalSeconds:F1}s");
        sw.Restart();
        var collider = BuildGrenadeCollider(mesh, min, max);
        Console.WriteLine($"collider built in {sw.Elapsed.TotalSeconds:F1}s");

        var k = ThrowConstants.Default;
        var toTarget = target - origin;
        var yawCenter = MathF.Atan2(toTarget.Y, toTarget.X) * 180f / MathF.PI;
        var specs = new List<ThrowSpec>();
        foreach (var type in new[] { ThrowType.Stand, ThrowType.Crouch, ThrowType.JumpThrow, ThrowType.CrouchJumpThrow, ThrowType.RunJumpThrow })
        {
            var eye = origin + new Vector3(0, 0, GrenadeTrajectory.EyeHeight(type));
            foreach (var strength in new[] { 1f, 0.5f, 0f })
            {
                for (var yaw = yawCenter - 30f; yaw <= yawCenter + 30f; yaw += 3f)
                {
                    for (var pitch = -65f; pitch <= 0f; pitch += 4f)
                    {
                        specs.Add(new ThrowSpec(eye, yaw, pitch, type, strength, 0f));
                    }
                }
            }
        }
        var repeats = int.Parse(options.GetValueOrDefault("repeats", "3"), CultureInfo.InvariantCulture);
        var threads = options.ContainsKey("serial") ? 1 : Environment.ProcessorCount;

        // Warm the JIT past tiering before the clock starts: tier-0 code is
        // several times slower and the first rounds were measuring it.
        Bench(grid, specs, k, threads, 8, out _, out _);
        var rounds = int.Parse(options.GetValueOrDefault("rounds", "64"), CultureInfo.InvariantCulture);
        for (var r = 0; r < repeats; r++)
        {
            var seconds = Bench(grid, specs, k, threads, rounds, out var ticks, out var checksum);
            Console.WriteLine($"voxel: {specs.Count * rounds} sims in {seconds:F2}s on {threads} threads = {specs.Count * rounds / seconds / 1000:F1}k sims/s, {ticks / seconds / 1e6:F1}M ticks/s ({ticks / (double)(specs.Count * rounds):F0} ticks/sim), checksum {checksum:X8}");
        }

        // Exact: one verification window (5x5 aims at 0.6 degrees) per 40 candidates.
        var exactSpecs = specs.Where((_, i) => i % 40 == 0).SelectMany(s => Enumerable.Range(-2, 5).SelectMany(dy => Enumerable.Range(-2, 5).Select(dp =>
            new ThrowSpec(s.EyePosition, s.YawDeg + dy * 0.6f, s.PitchDeg + dp * 0.6f, s.Type, s.Strength, s.RunYawOffsetDeg)))).ToList();
        for (var r = 0; r < repeats; r++)
        {
            var sw2 = System.Diagnostics.Stopwatch.StartNew();
            var sum = 0L;
            Parallel.For(0, exactSpecs.Count, new ParallelOptions { MaxDegreeOfParallelism = threads }, i =>
            {
                var res = GrenadeTrajectory.SimulateExact(collider, exactSpecs[i], k);
                Interlocked.Add(ref sum, (long)(res.RestPoint.X * 16) + (long)(res.RestPoint.Y * 16) * 7 + res.Bounces);
            });
            var seconds = sw2.Elapsed.TotalSeconds;
            Console.WriteLine($"exact: {exactSpecs.Count} sims in {seconds:F2}s on {threads} threads = {exactSpecs.Count / seconds:F0} sims/s, checksum {sum:X8}");
        }
        return 0;
    }

    static double Bench(VoxelGrid grid, List<ThrowSpec> specs, ThrowConstants k, int threads, int rounds, out long ticks, out long checksum)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        long tickSum = 0, sum = 0;
        // Per-worker partial sums: two interlocked adds per simulation on one
        // shared cache line were most of what the first version measured.
        Parallel.For(0, specs.Count * rounds, new ParallelOptions { MaxDegreeOfParallelism = threads },
            () => (Ticks: 0L, Sum: 0L),
            (i, _, acc) =>
            {
                var res = GrenadeTrajectory.Simulate(grid, specs[i % specs.Count], k);
                acc.Ticks += (long)MathF.Round(res.FlightTime * 64f);
                acc.Sum += (long)(res.RestPoint.X * 16) + (long)(res.RestPoint.Y * 16) * 7 + (long)(res.RestPoint.Z * 16) * 13 + res.Bounces;
                return acc;
            },
            acc => { Interlocked.Add(ref tickSum, acc.Ticks); Interlocked.Add(ref sum, acc.Sum); });
        ticks = tickSum;
        checksum = sum;
        return sw.Elapsed.TotalSeconds;
    }
}

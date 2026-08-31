using System.Numerics;
using System.Text.Json;
using SmokeSolver.Sim;

namespace SmokeSolver.Sim.Tests;

/// <summary>
/// Replays real de_dust2 engine captures through the exact simulator and checks
/// the predicted rest position. The calibrated median error is ~1u; the 48u gate
/// catches gross physics regressions without flaking on the known slope cases.
/// </summary>
// This is the only test that measures the sim against the real game, so it has
// to actually run. It used to read the full extracted map and the raw capture
// log, both gitignored, and silently return on a checkout without them - which
// is every CI run, so it reported green having asserted nothing. The fixtures
// beside it are committed instead: the mesh is de_dust2 cropped (the `crop`
// command) to exactly the region these throws fly through, which is the same
// region the collider below would have queried anyway, so the replay is
// identical to one against the full map at a fraction of the size.
public class GoldenReplayTests
{
    const float RestTolerance = 48f;

    static string FixturePath(string name) =>
        Path.Combine(AppContext.BaseDirectory, "fixtures", name);

    [Fact]
    public void RealDust2CapturesReplayWithinTolerance()
    {
        var geoPath = FixturePath("dust2-golden.s2geo");
        var capturesPath = FixturePath("dust2-golden-captures.jsonl");
        Assert.True(File.Exists(geoPath), $"missing committed fixture {geoPath} - regenerate with `crop`");
        Assert.True(File.Exists(capturesPath), $"missing committed fixture {capturesPath}");

        var mesh = CollisionMesh.Load(geoPath);
        var filter = mesh.GrenadeSolidFilter();
        var replayed = 0;

        foreach (var text in File.ReadLines(capturesPath))
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }
            using var record = JsonDocument.Parse(text);
            var capture = record.RootElement;
            var start = ReadVector(capture.GetProperty("start"));
            var velocity = ReadVector(capture.GetProperty("velocity"));
            var rest = ReadVector(capture.GetProperty("rest"));

            var margin = new Vector3(600f);
            var collider = new TriangleCollider(
                mesh,
                Vector3.Min(start, rest) - margin,
                Vector3.Max(start, rest) + margin,
                filter);
            var result = GrenadeTrajectory.SimulateExactRaw(collider, start, velocity);

            Assert.False(result.Lost, $"capture {replayed}: replay lost the grenade (start {start}, recorded rest {rest})");
            var error = Vector3.Distance(result.RestPoint, rest);
            Assert.True(error <= RestTolerance,
                $"capture {replayed}: predicted rest {result.RestPoint} is {error:F1}u from recorded {rest}");
            replayed++;
        }
        Assert.True(replayed >= 2, $"expected the committed captures to replay, got {replayed}");
    }

    static Vector3 ReadVector(JsonElement element) =>
        new(element[0].GetSingle(), element[1].GetSingle(), element[2].GetSingle());
}

using System.Text.Json;
using SmokeSolver.Solver;

namespace SmokeSolver.Sim.Tests;

/// <summary>
/// Writes the C# HumanError's answers for a grid of inputs to a fixture the
/// viewer's copy is checked against (rig/check-viewer-logic.mjs).
/// </summary>
// The viewer keeps its own humanError() because it must rank results the
// server cached before the field existed. Two copies drift; this is the tie.
public class HumanErrorParityTests
{
    [Fact]
    public void FixtureMatchesTheModel()
    {
        var cases = new List<object>();
        foreach (var pin in new[] { 0, 1, 2 })
        {
            foreach (var band in new[] { 0, 1, 2, 3, 4, 5, 6 })
            {
                foreach (var distance in new[] { 100f, 600f, 1500f })
                {
                    foreach (var type in new[] { ThrowType.Stand, ThrowType.JumpThrow, ThrowType.RunJumpThrow })
                    {
                        foreach (var scatter in new[] { 0f, 40f })
                        {
                            foreach (var stability in new[] { 1f, 0.4f })
                            {
                                cases.Add(new
                                {
                                    pin, band, distance, type = type.ToString(), scatter, stability,
                                    expected = HumanError.Estimate(pin, band, distance, type, scatter, stability),
                                });
                            }
                        }
                    }
                }
            }
        }
        var path = Path.Combine(FixtureDir(), "human-error.json");
        var json = JsonSerializer.Serialize(cases, new JsonSerializerOptions { WriteIndented = false });
        // Rewritten only when it changed, so a clean tree stays clean.
        if (!File.Exists(path) || File.ReadAllText(path) != json)
        {
            File.WriteAllText(path, json);
        }
        Assert.Equal(cases.Count, JsonDocument.Parse(File.ReadAllText(path)).RootElement.GetArrayLength());
    }

    static string FixtureDir()
    {
        // Walk up from the test binary to the source tree's fixtures directory.
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 8 && dir is not null; i++)
        {
            var candidate = Path.Combine(dir, "tests", "Sim.Tests", "fixtures");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }
            dir = Path.GetDirectoryName(dir);
        }
        throw new DirectoryNotFoundException("tests/Sim.Tests/fixtures not found above " + AppContext.BaseDirectory);
    }
}

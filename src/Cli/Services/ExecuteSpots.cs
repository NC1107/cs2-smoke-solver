using System.Text.Json;

namespace SmokeSolver.Cli;

/// <summary>
/// Where one player can stand and throw every smoke of an execute.
/// </summary>
// Solving each target separately says where each smoke can come from. It does
// not answer the question a player building an execute actually starts from,
// which is where they can stand to throw ALL of them - because a set of throws
// from four different corners of the map is not an execute, it is four
// lineups. This intersects the per-target answers spatially: a spot survives
// only if every target has a throw from close enough to it that the player
// shuffles rather than walks.
public static class ExecuteSpots
{
    /// <summary>
    /// Spots that can throw every target, best first.
    /// </summary>
    // Anchored on the FIRST target's throws rather than a grid over the map:
    // any spot that works must already be one of them, so the candidate set is
    // exactly the right size and no resolution has to be invented.
    public static string Find(IReadOnlyList<List<JsonElement>> perTarget, float within, int keep)
    {
        if (perTarget.Count == 0 || perTarget.Any(t => t.Count == 0))
        {
            // One unreachable target means the execute as asked is impossible,
            // and saying which one is the difference between a dead end and a
            // hint to move that smoke.
            var missing = perTarget
                .Select((t, i) => (t, i))
                .Where(x => x.t.Count == 0)
                .Select(x => x.i)
                .ToList();
            return $"{{\"spots\":[],\"impossibleTargets\":[{string.Join(",", missing)}]}}";
        }

        var withinSq = within * within;
        var scored = new List<(float Score, float Worst, JsonElement[] Picks, float[] Feet)>();
        var seen = new HashSet<(int, int)>();

        foreach (var anchor in perTarget[0])
        {
            var feet = Feet(anchor);
            // A cheap first pass to bound the work: one candidate per coarse
            // cell, since a solve returns many throws from the same stance with
            // different aims. It is deliberately NOT the real deduplication -
            // cell boundaries split neighbours - which happens on the ranked
            // list below.
            var cell = ((int)MathF.Round(feet[0] / within), (int)MathF.Round(feet[1] / within));
            if (!seen.Add(cell))
            {
                continue;
            }

            var picks = new JsonElement[perTarget.Count];
            picks[0] = anchor;
            var ok = true;
            for (var i = 1; i < perTarget.Count && ok; i++)
            {
                // The most reproducible throw for this target from near here -
                // not merely the first one found, or the answer would depend on
                // solve order rather than on which throw is actually best.
                JsonElement? best = null;
                var bestRank = float.MaxValue;
                foreach (var candidate in perTarget[i])
                {
                    var f = Feet(candidate);
                    var dx = f[0] - feet[0];
                    var dy = f[1] - feet[1];
                    if (dx * dx + dy * dy > withinSq)
                    {
                        continue;
                    }
                    var rank = Reproducibility(candidate);
                    if (rank < bestRank)
                    {
                        bestRank = rank;
                        best = candidate;
                    }
                }
                if (best is null)
                {
                    ok = false;
                }
                else
                {
                    picks[i] = best.Value;
                }
            }
            if (!ok)
            {
                continue;
            }
            // A spot is only as good as its worst smoke: an execute with one
            // throw nobody can reproduce is not an execute, however easy the
            // other three are. Ranking on the sum would hide that behind them.
            var worst = picks.Max(Reproducibility);
            var total = picks.Sum(Reproducibility);
            scored.Add((worst * 100f + total, worst, picks, feet));
        }

        // Greedy spatial thinning on the RANKED list, not a grid over the
        // candidates: rounding feet into cells splits neighbours across a cell
        // boundary, and two spots 16u apart came back as separate answers -
        // twelve rows describing about five actual places to stand. Keeping the
        // best spot and then refusing anything within `within` of one already
        // kept is boundary-free, and it runs against the sorted list so the one
        // that survives each cluster is the best of it.
        var chosen = new List<(float Score, float Worst, JsonElement[] Picks, float[] Feet)>();
        foreach (var candidate in scored.OrderBy(x => x.Score))
        {
            if (chosen.Count >= keep)
            {
                break;
            }
            var tooClose = chosen.Any(c =>
            {
                var dx = c.Feet[0] - candidate.Feet[0];
                var dy = c.Feet[1] - candidate.Feet[1];
                return dx * dx + dy * dy < withinSq;
            });
            if (!tooClose)
            {
                chosen.Add(candidate);
            }
        }

        var rows = chosen
            .Select(x =>
                $"{{\"feet\":[{F(x.Feet[0])},{F(x.Feet[1])},{F(x.Feet[2])}]," +
                $"\"worst\":{(int)x.Worst}," +
                $"\"smokes\":[{string.Join(",", x.Picks.Select(p => p.GetRawText()))}]}}");

        return $"{{\"spots\":[{string.Join(",", rows)}],\"impossibleTargets\":[]}}";
    }

    static string F(float v) => v.ToString("F2", System.Globalization.CultureInfo.InvariantCulture);

    static float[] Feet(JsonElement lineup)
    {
        var f = lineup.GetProperty("feet");
        return [f[0].GetSingle(), f[1].GetSingle(), f[2].GetSingle()];
    }

    // The same measure the solver ranks by: how close the landmark sits to the
    // crosshair, with a position-chaos penalty. Read off the serialized lineup
    // so this cannot drift from what the rest of the app shows.
    static float Reproducibility(JsonElement lineup)
    {
        var band = lineup.TryGetProperty("aimRef", out var aim) && aim.TryGetProperty("band", out var b)
            ? b.GetInt32()
            : 3;
        var scatter = lineup.TryGetProperty("scatter", out var s) && s.ValueKind == JsonValueKind.Number
            ? s.GetSingle()
            : 0f;
        return band + (scatter > 16f ? 3 : 0);
    }
}

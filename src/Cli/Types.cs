using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json;
using SmokeSolver.Extraction;
using SmokeSolver.Sim;
using SmokeSolver.Solver;

namespace SmokeSolver.Cli;

public sealed record NavAreaJson(uint Id, float[][] Corners);

// The plugin claims request.json by rename, so it must only ever see a
// complete file: write to a temp sibling and rename into place (atomic on
// the same filesystem).
public static class RequestFile
{
    public static void WriteAtomic(string requestPath, string json)
    {
        var tmp = requestPath + ".tmp";
        File.WriteAllText(tmp, json);
        File.Move(tmp, requestPath, overwrite: true);
    }
}

// Tail captures.jsonl without re-reading the whole growing file: remember the
// byte offset, and only consume newline-terminated lines so a read that lands
// mid-append defers the partial line to the next poll instead of crashing.
public sealed class CaptureTailer(string path)
{
    long _offset;
    readonly List<string> _pending = [];

    public long InitializeAtEnd()
    {
        if (File.Exists(path))
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            _offset = fs.Length;
        }
        return _offset;
    }

    public IReadOnlyList<string> ReadNewLines()
    {
        _pending.Clear();
        if (!File.Exists(path))
        {
            return _pending;
        }
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        if (fs.Length < _offset)
        {
            _offset = 0; // the plugin rotated the file; start over on the fresh one
        }
        if (fs.Length == _offset)
        {
            return _pending;
        }
        fs.Seek(_offset, SeekOrigin.Begin);
        using var reader = new StreamReader(fs);
        var chunk = reader.ReadToEnd();
        var consumed = 0;
        int nl;
        while ((nl = chunk.IndexOf('\n', consumed)) >= 0)
        {
            var line = chunk[consumed..nl].Trim();
            if (line.Length > 0)
            {
                _pending.Add(line);
            }
            consumed = nl + 1;
        }
        _offset += System.Text.Encoding.UTF8.GetByteCount(chunk[..consumed]);
        return _pending;
    }
}

// PerturbU marks the one-tick-movement probes: the same aim re-thrown from
// feet shifted by that many units (0 = the lineup's exact spot).
public sealed record ValidatePlan(int Index, Lineup Lineup, Vector3 Pos, Vector3 Vel, Vector3 PredictedRest, int PredictedBounces, float PerturbU = 0f);

public sealed record ValidateRow(
    int Index,
    string Type,
    float Strength,
    float Stability,
    float[] Feet,
    float Yaw,
    float Pitch,
    // Movement-key direction of a running jump throw (ThrowSpec
    // .RunYawOffsetDeg); part of the throw's identity, so the dashboard can
    // mint a permalink that reopens this exact lineup in the viewer.
    float RunDeg,
    // Nonzero on one-tick-movement probes: how far (u) the feet were shifted
    // from the lineup's exact spot before this throw.
    float PerturbU,
    // The solver's predicted position-chaos score for this lineup (rest
    // displacement under a 0.25u foot shift), recorded so real-throw error
    // can be correlated against it.
    float Scatter,
    int PredictedBounces,
    int RealBounces,
    float[] Pos,
    float[] Vel,
    float[] PredictedRest,
    float[] RealRest,
    bool Detonated,
    float ErrPredicted,
    float ErrTarget,
    int DivergenceTick,
    string DivergenceClass);

public sealed record TargetSolve(
    Vector3 Target,
    int OriginCount,
    List<int[]> Coverage,
    List<Lineup> Lineups,
    TriangleCollider Collider,
    // Player-solid variant (clips included): pin classification must run
    // against what stops FEET, not what bounces grenades.
    TriangleCollider PlayerCollider);

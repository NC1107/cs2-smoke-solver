using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json;
using SmokeSolver.Extraction;
using SmokeSolver.Sim;
using SmokeSolver.Solver;
using static SmokeSolver.Cli.CliParsing;
using static SmokeSolver.Cli.MeshSetup;
using static SmokeSolver.Cli.LineupApi;
using static SmokeSolver.Cli.TargetSolver;
using static SmokeSolver.Cli.ExtractCommand;
using static SmokeSolver.Cli.InfoCommand;
using static SmokeSolver.Cli.SmokeCommand;
using static SmokeSolver.Cli.SightlineCommand;
using static SmokeSolver.Cli.SolveCommand;
using static SmokeSolver.Cli.GroundCommand;
using static SmokeSolver.Cli.LineupsCommand;
using static SmokeSolver.Cli.ViewerDataCommand;
using static SmokeSolver.Cli.ServeCommand;
using static SmokeSolver.Cli.ThrowCommand;
using static SmokeSolver.Cli.CalibrateCommand;
using static SmokeSolver.Cli.ValidateCommand;
using static SmokeSolver.Cli.ExportGltfCommand;
using static SmokeSolver.Cli.BestLineupCommand;
using static SmokeSolver.Cli.PointLineupCommand;

namespace SmokeSolver.Cli;

public static class CliParsing
{
    public static Dictionary<string, string> ParseOptions(ReadOnlySpan<string> args)
    {
        var options = new Dictionary<string, string>();
        for (var i = 0; i < args.Length; i++)
        {
            if (!args[i].StartsWith("--"))
            {
                throw new ArgumentException($"unexpected argument '{args[i]}'");
            }
            var key = args[i][2..];
            if (i + 1 < args.Length && !args[i + 1].StartsWith("--"))
            {
                options[key] = args[++i];
            }
            else
            {
                options[key] = "true";
            }
        }
        return options;
    }

    // "x,y" or "x,y,z" - the z-less form asks the solver to derive ground
    // height from nav data. Shared by every target-taking command.
    public static (System.Numerics.Vector3 Target, bool HasZ) ParseVec2or3(string raw)
    {
        var parts = raw.Split(',', StringSplitOptions.TrimEntries);
        if (parts.Length is < 2 or > 3)
        {
            throw new ArgumentException($"expected x,y or x,y,z but got '{raw}'");
        }
        var v = new System.Numerics.Vector3(
            float.Parse(parts[0], CultureInfo.InvariantCulture),
            float.Parse(parts[1], CultureInfo.InvariantCulture),
            parts.Length > 2 ? float.Parse(parts[2], CultureInfo.InvariantCulture) : 0);
        return (v, parts.Length > 2);
    }

    // User-editable JSON inputs (nav areas, markers, constants) get a named
    // error instead of a NullReferenceException three stack frames later.
    public static T LoadJson<T>(string path, string what)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"{what} file not found: {path}");
        }
        try
        {
            return JsonSerializer.Deserialize<T>(File.ReadAllText(path))
                ?? throw new InvalidDataException($"{path} is not a valid {what} file (null document)");
        }
        catch (JsonException e)
        {
            throw new InvalidDataException($"{path} is not a valid {what} file: {e.Message}");
        }
    }

    public static string Require(Dictionary<string, string> options, string key) =>
        options.TryGetValue(key, out var value) ? value : throw new ArgumentException($"missing required option --{key}");

    public static Vector3 ParseVec(string s)
    {
        var parts = s.Split(',', ' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length != 3)
        {
            throw new ArgumentException($"expected x,y,z but got '{s}'");
        }
        return new Vector3(
            float.Parse(parts[0], CultureInfo.InvariantCulture),
            float.Parse(parts[1], CultureInfo.InvariantCulture),
            float.Parse(parts[2], CultureInfo.InvariantCulture));
    }

    public static (Vector3 Eye, float Pitch, float Yaw) ParseGetPos(string line)
    {
        var m = System.Text.RegularExpressions.Regex.Match(
            line,
            @"setpos\s+(-?[\d.]+)\s+(-?[\d.]+)\s+(-?[\d.]+)\s*;?\s*(?:setang\s+(-?[\d.]+)\s+(-?[\d.]+))?");
        if (!m.Success)
        {
            throw new ArgumentException($"cannot parse getpos line: '{line}'");
        }
        float F(int g) => float.Parse(m.Groups[g].Value, CultureInfo.InvariantCulture);
        return (
            new Vector3(F(1), F(2), F(3)),
            m.Groups[4].Success ? F(4) : 0f,
            m.Groups[5].Success ? F(5) : 0f);
    }

    public static List<Vector3> ParseTargets(string s) =>
        [.. s.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(ParseVec)];

    // The canonical click label for a throw strength. Validation reports used
    // to say "mid" for the same band the lineup API called "left+right",
    // making a report and the API describe the identical throw differently.
    public static string ClickName(float strength) =>
        strength >= 0.99f ? "left" : strength >= 0.49f ? "left+right" : "right";

    // One switch for the CLI's --type values; three commands each carried a
    // copy, so adding a crouch variant to one surface silently skipped the rest.
    public static ThrowType ParseThrowType(string raw) => raw.ToLowerInvariant() switch
    {
        "stand" => ThrowType.Stand,
        "jump" => ThrowType.JumpThrow,
        "runjump" => ThrowType.RunJumpThrow,
        var other => throw new ArgumentException($"unknown throw type '{other}'"),
    };

    // The console command handed to players. Z + 1 keeps the teleport just above
    // the floor so the game settles the player down onto it instead of into it.
    public static string SetposCommand(Vector3 feet, float pitchDeg, float yawDeg) =>
        $"setpos {feet.X:F0} {feet.Y:F0} {feet.Z + 1:F0}; setang {pitchDeg:F1} {yawDeg:F1} 0";

    // The movement keys behind a running jump throw's carried velocity
    // direction. Bands rather than exact float matches so a value that went
    // through JSON and back still names its key.
    public static string RunKeys(float runYawOffsetDeg) =>
        runYawOffsetDeg > 67.5f ? "left (A)"
        : runYawOffsetDeg > 22.5f ? "forward-left (W+A)"
        : runYawOffsetDeg < -67.5f ? "right (D)"
        : runYawOffsetDeg < -22.5f ? "forward-right (W+D)"
        : "forward (W)";

    public static string Describe(ThrowType type, float strength = 1f, float runYawOffsetDeg = 0f)
    {
        // "jumpthrow bind" is dead advice: Valve disabled multi-input binds on
        // official servers (Aug 2024, cl_allow_multi_input_binds 0). What players
        // actually do post-subtick is hold the click and tap jump - the release
        // samples the same tick window a bind used to hit.
        var movement = type switch
        {
            ThrowType.Stand => "stand still",
            ThrowType.Crouch => "crouch (hold ctrl)",
            ThrowType.JumpThrow => "hold click, tap jump, release",
            ThrowType.CrouchJumpThrow => "crouch + hold click, tap jump, release",
            _ => $"run {RunKeys(runYawOffsetDeg)} + hold click, tap jump, release",
        };
        var buttons = strength switch
        {
            >= 0.99f => "left click",
            >= 0.49f => "left+right click",
            _ => "right click",
        };
        return $"{movement}, {buttons}";
    }

    public static string ReadBuildId(string gameDir)
    {
        var steamInf = Path.Combine(gameDir, "game", "csgo", "steam.inf");
        foreach (var line in File.ReadLines(steamInf))
        {
            if (line.StartsWith("ClientVersion=", StringComparison.Ordinal))
            {
                return line["ClientVersion=".Length..].Trim();
            }
        }
        throw new InvalidDataException($"no ClientVersion in {steamInf}");
    }
}

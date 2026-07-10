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

    public static string Describe(ThrowType type, float strength = 1f)
    {
        var movement = type switch
        {
            ThrowType.Stand => "stand still",
            ThrowType.Crouch => "crouch (hold ctrl)",
            ThrowType.JumpThrow => "jumpthrow bind",
            ThrowType.CrouchJumpThrow => "crouch + jumpthrow bind",
            _ => "run forward (W) + jumpthrow bind",
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

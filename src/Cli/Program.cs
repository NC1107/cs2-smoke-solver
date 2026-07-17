using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json;
using SmokeSolver.Extraction;
using SmokeSolver.Sim;
using SmokeSolver.Solver;
using SmokeSolver.Cli;
using static SmokeSolver.Cli.CliParsing;

var commands = new Dictionary<string, Func<Dictionary<string, string>, int>>
{
    ["extract"] = ExtractCommand.Run,
    ["info"] = InfoCommand.Run,
    ["smoke"] = SmokeCommand.Run,
    ["sightline"] = SightlineCommand.Run,
    ["solve"] = SolveCommand.Run,
    ["ground"] = GroundCommand.Run,
    ["lineups"] = LineupsCommand.Run,
    ["viewerdata"] = ViewerDataCommand.Run,
    ["serve"] = ServeCommand.Run,
    ["throw"] = ThrowCommand.Run,
    ["calibrate"] = CalibrateCommand.Run,
    ["validate"] = ValidateCommand.Run,
    ["batchvalidate"] = BatchValidateCommand.Run,
    ["exportgltf"] = ExportGltfCommand.Run,
    ["bestlineup"] = BestLineupCommand.Run,
    ["pointlineup"] = PointLineupCommand.Run,
};

if (args.Length == 0 || !commands.TryGetValue(args[0], out var command))
{
    // Generated from the table so a new command can never be forgotten here.
    await Console.Error.WriteLineAsync(
        $"""
        usage: smokesolver <{string.Join("|", commands.Keys)}> [--option value ...]
          extract    --game <cs2 dir> --map <name> --out <dir>
          info       --geo <file.s2geo>
          smoke      --geo <file.s2geo> --rest x,y,z [--conservative] [--voxel 16] [--obj out.obj]
          sightline  --geo <file.s2geo> --from x,y,z --to x,y,z --rest x,y,z
          solve      --geo <file.s2geo> --from x,y,z --to x,y,z [--jitter 16] [--json out]
          ground     --geo <file.s2geo> --from x,y --to x,y [--steps 20] [--zmax 500]
          lineups    --geo <file.s2geo> --from x,y,z --to "x,y,z;..." --origins x0,y0,x1,y1
          viewerdata --geo <file.s2geo> --entities <file.json> --region x0,y0,x1,y1
          serve      [--port 8137] [--bind localhost|any] [--attrs ...] (web viewer + lineup API; auto-discovers every data/*.s2geo)
          throw      --geo <file.s2geo> --pos x,y,z --ang pitch,yaw [--type ...] [--strength ...]
          calibrate  --geo <file.s2geo> --throws data/throws.json [--out data/throw-constants.json]
          validate   --geo ... --nav ... --target x,y[,z] | --markers <file> [--pass 1] [--limit N]
          batchvalidate --maps a,b,c [--targets-per-map 3] [--fuzz N] [--limit 50] [--perturb 0.25] [--batch label] (unattended accuracy sweep via the rig server)
          bestlineup --geo ... --nav ... --target x,y[,z] --near x,y,z (nearest practical lineup)
          pointlineup --geo ... --from x,y,z --target x,y,z [--mode quick|deep] (fixed-spot solve)
          exportgltf --vpk <map.vpk> [--out out.glb] (textured render mesh export)
        """);
    return 1;
}
return command(ParseOptions(args.AsSpan(1)));


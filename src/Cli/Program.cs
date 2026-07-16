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
    Console.Error.WriteLine($"usage: smokesolver <{string.Join("|", commands.Keys)}> [--option value ...]");
    Console.Error.WriteLine("  extract    --game <cs2 dir> --map <name> --out <dir>");
    Console.Error.WriteLine("  info       --geo <file.s2geo>");
    Console.Error.WriteLine("  smoke      --geo <file.s2geo> --rest x,y,z [--conservative] [--voxel 16] [--obj out.obj]");
    Console.Error.WriteLine("  sightline  --geo <file.s2geo> --from x,y,z --to x,y,z --rest x,y,z");
    Console.Error.WriteLine("  solve      --geo <file.s2geo> --from x,y,z --to x,y,z [--jitter 16] [--json out]");
    Console.Error.WriteLine("  ground     --geo <file.s2geo> --from x,y --to x,y [--steps 20] [--zmax 500]");
    Console.Error.WriteLine("  lineups    --geo <file.s2geo> --from x,y,z --to \"x,y,z;...\" --origins x0,y0,x1,y1");
    Console.Error.WriteLine("  viewerdata --geo <file.s2geo> --entities <file.json> --region x0,y0,x1,y1");
    Console.Error.WriteLine("  serve      [--port 8137] [--bind localhost|any] [--attrs ...] (web viewer + lineup API; auto-discovers every data/*.s2geo)");
    Console.Error.WriteLine("  throw      --geo <file.s2geo> --pos x,y,z --ang pitch,yaw [--type ...] [--strength ...]");
    Console.Error.WriteLine("  calibrate  --geo <file.s2geo> --throws data/throws.json [--out data/throw-constants.json]");
    Console.Error.WriteLine("  validate   --geo ... --nav ... --target x,y[,z] | --markers <file> [--pass 1] [--limit N]");
    Console.Error.WriteLine("  batchvalidate --maps a,b,c [--targets-per-map 3] [--fuzz N] [--limit 50] [--perturb 0.25] [--batch label] (unattended accuracy sweep via the rig server)");
    Console.Error.WriteLine("  bestlineup --geo ... --nav ... --target x,y[,z] --near x,y,z (nearest practical lineup)");
    Console.Error.WriteLine("  pointlineup --geo ... --from x,y,z --target x,y,z [--mode quick|deep] (fixed-spot solve)");
    Console.Error.WriteLine("  exportgltf --vpk <map.vpk> [--out out.glb] (textured render mesh export)");
    return 1;
}
return command(ParseOptions(args.AsSpan(1)));


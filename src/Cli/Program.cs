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
    ["exportgltf"] = ExportGltfCommand.Run,
    ["bestlineup"] = BestLineupCommand.Run,
    ["pointlineup"] = PointLineupCommand.Run,
};

if (args.Length == 0 || !commands.TryGetValue(args[0], out var command))
{
    Console.Error.WriteLine("usage: smokesolver <extract|info|smoke|sightline|solve> [--option value ...]");
    Console.Error.WriteLine("  extract   --game <cs2 dir> --map <name> --out <dir>");
    Console.Error.WriteLine("  info      --geo <file.s2geo>");
    Console.Error.WriteLine("  smoke     --geo <file.s2geo> --rest x,y,z [--conservative] [--voxel 16] [--obj out.obj]");
    Console.Error.WriteLine("  sightline --geo <file.s2geo> --from x,y,z --to x,y,z --rest x,y,z [--conservative] [--voxel 16]");
    Console.Error.WriteLine("  solve     --geo <file.s2geo> --from x,y,z --to x,y,z [--conservative] [--jitter 16] [--json out] [--obj out]");
    Console.Error.WriteLine("  ground    --geo <file.s2geo> --from x,y --to x,y [--steps 20] [--zmax 500] [--attrs ...]");
    Console.Error.WriteLine("  lineups   --geo <file.s2geo> --from x,y,z --to \"x,y,z;x,y,z\" --origins x0,y0,x1,y1[,z0,z1] [--anchor x,y,z[,r]] [--types stand,jump,runjump] [--top 12] [--json out]");
    Console.Error.WriteLine("  viewerdata --geo <file.s2geo> --entities <file.json> --region x0,y0,x1,y1 [--attrs ...] [--out data/viewer-map.json]");
    Console.Error.WriteLine("  serve     [--port 8137] (serves viewer/ and data/ from the current directory)");
    Console.Error.WriteLine("  throw     --geo <file.s2geo> --pos x,y,z --ang pitch,yaw [--type stand|jump|runjump] [--strength 1|0.5|0] [--feet] [--solve zone.json]");
    Console.Error.WriteLine("  calibrate --geo <file.s2geo> --throws data/throws.json [--attrs ...] [--out data/throw-constants.json]");
    Console.Error.WriteLine("  validate  --geo <file.s2geo> --nav <navareas.json> --target x,y[,z] [--calib <dir>] [--limit N] [--pass 3] [--dry-run] (throws every solved lineup on the live rig server and grades sim accuracy)");
    return 1;
}
return command(ParseOptions(args.AsSpan(1)));


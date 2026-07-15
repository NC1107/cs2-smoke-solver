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

public static class ViewerDataCommand
{
    public static int Run(Dictionary<string, string> options)
    {
        var (mesh, _, _, attributeFilter) = LoadCommon(options);
        var region = Require(options, "region").Split(',', StringSplitOptions.TrimEntries)
            .Select(v => float.Parse(v, CultureInfo.InvariantCulture)).ToArray();
        var (x0, y0, x1, y1) = (region[0], region[1], region[2], region[3]);
        // Namespaced by map so multiple maps' viewer data can sit side by side
        // under data/ - the viewer's map picker fetches data/{map}.viewer-map.json.
        var outPath = options.GetValueOrDefault("out", $"data/{mesh.MapName}.viewer-map.json");
        var imagePath = Path.ChangeExtension(outPath, ".png");

        // Radar-style rendering: a horizontal slice at chest height over the local
        // playable ground. Walls become solid strokes, doorways become gaps, and
        // geometry above head height never clutters the picture.
        var navAreasPath = options.GetValueOrDefault(
            "nav",
            DefaultNavAreasPath(options, mesh));
        var navAreas = LoadJson<List<NavAreaJson>>(navAreasPath, "nav areas");

        const float NavCell = 64f;
        var gw = (int)MathF.Ceiling((x1 - x0) / NavCell);
        var gh = (int)MathF.Ceiling((y1 - y0) / NavCell);
        var navZ = new float?[gw * gh];
        foreach (var area in navAreas)
        {
            var corners = area.Corners;
            var z = corners.Average(c => c[2]);
            var gx0 = Math.Max(0, (int)((corners.Min(c => c[0]) - x0) / NavCell));
            var gx1 = Math.Min(gw - 1, (int)((corners.Max(c => c[0]) - x0) / NavCell));
            var gy0 = Math.Max(0, (int)((corners.Min(c => c[1]) - y0) / NavCell));
            var gy1 = Math.Min(gh - 1, (int)((corners.Max(c => c[1]) - y0) / NavCell));
            for (var gy = gy0; gy <= gy1; gy++)
            {
                for (var gx = gx0; gx <= gx1; gx++)
                {
                    var i = gy * gw + gx;
                    if (navZ[i] == null || z < navZ[i])
                    {
                        navZ[i] = z;
                    }
                }
            }
        }

        // Exact-triangle radar: per-pixel vertical raycasts against the real
        // collision mesh at 2u pixels. The earlier 8u voxel slice rounded every
        // wall to voxel boundaries; ray-sampling the triangles renders edges,
        // arches, and props at their true footprint.
        var pixelSize = 2f;
        var (meshMin, meshMax) = mesh.ComputeBounds();
        var radarCollider = new TriangleCollider(
            mesh,
            new Vector3(x0, y0, meshMin.Z),
            new Vector3(x1, y1, MathF.Min(meshMax.Z, 800)),
            attributeFilter);
        Console.WriteLine("exact-triangle radar collider built");

        var navValues = navZ.Where(v => v != null).Select(v => v!.Value).ToList();
        var navLo = navValues.Min();
        var navHi = navValues.Max();

        var w = (int)((x1 - x0) / pixelSize);
        var h = (int)((y1 - y0) / pixelSize);
        var bitmap = new SkiaSharp.SKBitmap(w, h, SkiaSharp.SKColorType.Rgba8888, SkiaSharp.SKAlphaType.Unpremul);
        var pixels = new SkiaSharp.SKColor[w * h];

        // A vertical ray is parallel to vertical walls and never hits them; a
        // static box-vs-triangle test is orientation-free.
        bool HitBetween(float wx, float wy, float zLo, float zHi) =>
            radarCollider.BoxIntersects(
                new Vector3(wx, wy, (zLo + zHi) * 0.5f),
                new Vector3(pixelSize * 0.5f, pixelSize * 0.5f, (zHi - zLo) * 0.5f));

        Parallel.For(0, h, py =>
        {
            for (var px = 0; px < w; px++)
            {
                // Image row 0 is the north edge so the viewer can blit directly.
                var wx = x0 + (px + 0.5f) * pixelSize;
                var wy = y1 - (py + 0.5f) * pixelSize;
                var gx = (int)((wx - x0) / NavCell);
                var gy = (int)((wy - y0) / NavCell);
                var ground = gx >= 0 && gx < gw && gy >= 0 && gy < gh ? navZ[gy * gw + gx] : null;
                if (ground == null)
                {
                    pixels[py * w + px] = new SkiaSharp.SKColor(0, 0, 0, 0);
                    continue;
                }
                // Snap to the actual floor near the nav estimate before slicing.
                var floorZ = ground.Value;
                if (radarCollider.FirstHit(new Vector3(wx, wy, ground.Value + 40), new Vector3(wx, wy, ground.Value - 24), 0f) is { } fh)
                {
                    floorZ = ground.Value + 40 + fh.T * (-64f);
                }
                // R encodes the class (0 floor, 128 low cover, 255 wall);
                // G encodes map-level ground height for a subtle floor tint.
                byte cls = 0;
                if (HitBetween(wx, wy, floorZ + 44, floorZ + 76))
                {
                    cls = 255;
                }
                else if (HitBetween(wx, wy, floorZ + 12, floorZ + 44))
                {
                    cls = 128;
                }
                var tint = (byte)(255 * (ground.Value - navLo) / MathF.Max(1, navHi - navLo));
                pixels[py * w + px] = new SkiaSharp.SKColor(cls, tint, 0, 255);
            }
        });
        // Boundary pass: walls enclosing the playable space sit just outside nav
        // coverage; probe them from their covered neighbors so the map gets the
        // dark outline a radar needs.
        var isCovered = new bool[w * h];
        for (var i = 0; i < pixels.Length; i++)
        {
            isCovered[i] = pixels[i].Alpha != 0;
        }
        Parallel.For(0, h, py =>
        {
            for (var px = 0; px < w; px++)
            {
                if (isCovered[py * w + px])
                {
                    continue;
                }
                float? neighborGround = null;
                for (var dy = -1; dy <= 1 && neighborGround == null; dy++)
                {
                    for (var dx = -1; dx <= 1; dx++)
                    {
                        int nx = px + dx, ny = py + dy;
                        if (nx < 0 || nx >= w || ny < 0 || ny >= h || !isCovered[ny * w + nx])
                        {
                            continue;
                        }
                        var ngx = (int)((x0 + (nx + 0.5f) * pixelSize - x0) / NavCell);
                        var ngy = (int)((y1 - (ny + 0.5f) * pixelSize - y0) / NavCell);
                        if (ngx >= 0 && ngx < gw && ngy >= 0 && ngy < gh && navZ[ngy * gw + ngx] is { } nz2)
                        {
                            neighborGround = nz2;
                            break;
                        }
                    }
                }
                if (neighborGround == null)
                {
                    continue;
                }
                var wx = x0 + (px + 0.5f) * pixelSize;
                var wy = y1 - (py + 0.5f) * pixelSize;
                if (HitBetween(wx, wy, neighborGround.Value + 12, neighborGround.Value + 76))
                {
                    pixels[py * w + px] = new SkiaSharp.SKColor(255, 0, 0, 255);
                }
            }
        });
        // Thicken the enclosing outline: at 2u pixels a one-pixel wall stroke
        // disappears when the browser minifies the overview; dilating the boundary
        // walls into adjacent uncovered pixels keeps the radar outline readable at
        // every zoom while leaving interior detail crisp.
        for (var pass = 0; pass < 2; pass++)
        {
            var snapshot = (SkiaSharp.SKColor[])pixels.Clone();
            Parallel.For(0, h, py =>
            {
                for (var px = 0; px < w; px++)
                {
                    var i = py * w + px;
                    if (snapshot[i].Alpha != 0)
                    {
                        continue;
                    }
                    for (var dy = -1; dy <= 1; dy++)
                    {
                        for (var dx = -1; dx <= 1; dx++)
                        {
                            int nx = px + dx, ny = py + dy;
                            if (nx < 0 || nx >= w || ny < 0 || ny >= h)
                            {
                                continue;
                            }
                            var np = snapshot[ny * w + nx];
                            if (np.Alpha != 0 && np.Red == 255 && np.Green == 0)
                            {
                                pixels[i] = new SkiaSharp.SKColor(255, 0, 0, 255);
                                dy = 2;
                                break;
                            }
                        }
                    }
                }
            });
        }
        bitmap.Pixels = pixels;

        using (var image = SkiaSharp.SKImage.FromBitmap(bitmap))
        using (var encoded = image.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100))
        using (var stream = File.Create(imagePath))
        {
            encoded.SaveTo(stream);
        }

        var callouts = new List<object[]>();
        if (options.TryGetValue("entities", out var entitiesPath))
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(entitiesPath));
            var places = new Dictionary<string, List<(float X, float Y)>>();
            foreach (var e in doc.RootElement.EnumerateArray())
            {
                if (e.GetProperty("ClassName").GetString() != "env_cs_place")
                {
                    continue;
                }
                var place = e.GetProperty("Place").GetString();
                if (string.IsNullOrEmpty(place))
                {
                    continue;
                }
                var o = e.GetProperty("Origin");
                var (ex, ey) = (o[0].GetSingle(), o[1].GetSingle());
                if (ex < x0 || ex > x1 || ey < y0 || ey > y1)
                {
                    continue;
                }
                places.TryAdd(place, []);
                places[place].Add((ex, ey));
            }
            foreach (var (name, pts) in places)
            {
                callouts.Add([name, (int)pts.Average(pt => pt.X), (int)pts.Average(pt => pt.Y)]);
            }
        }

        var payload = new
        {
            map = mesh.MapName,
            build = mesh.GameBuildId,
            region = new[] { (int)x0, (int)y0, (int)x1, (int)y1 },
            image = Path.GetFileName(imagePath),
            pixelSize = (int)pixelSize,
            callouts,
        };
        File.WriteAllText(outPath, JsonSerializer.Serialize(payload));
        Console.WriteLine($"wrote {imagePath} ({w}x{h}) and {outPath} ({callouts.Count} callouts)");
        return 0;
    }
}

using System.Globalization;
using System.IO.Compression;
using System.Numerics;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
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

public static class ServeCommand
{
    // A cache miss triggers a map-wide solve that pegs the CPU for minutes.
    // Cap concurrent solves at two so cached lookups, mesh, and static file
    // requests keep flowing while solves are in flight.
    static readonly SemaphoreSlim SolveGate = new(2);

    // Lineup query bodies are a handful of numbers; anything bigger is abuse.
    const int MaxLineupBodyBytes = 4096;

    // One mesh/nav/payload set per discovered map, fixed for the process
    // lifetime just like the old single-map fields were - just keyed by map
    // name now so the viewer can switch maps without restarting the server.
    record MapEntry(
        CollisionMesh Mesh, Func<byte, bool>? AttributeFilter, List<NavAreaJson>? NavAreas,
        ThrowConstants Constants, byte[] MeshPayload, byte[] MeshPayloadGzip, string BuildETag)
    {
        // Brotli is ~26% smaller than gzip on this payload, but at the quality
        // level that buys is far too slow to sit on the startup path (de_inferno's
        // 49MB takes ~4.5 minutes). So it is never computed on a request or during
        // startup: it is read back from data/cache/ when an earlier run left it
        // there, and otherwise filled in by a background thread while the server is
        // already up and serving gzip. Reference assignment is atomic, so a request
        // sees either null (and serves gzip) or the finished blob, never a partial
        // one.
        public volatile byte[]? MeshPayloadBrotli;

        // Built on the first trajectory request for this map and kept: it indexes
        // the mesh arrays rather than copying them, so the cost is the cell index
        // alone, and rebuilding it per click would put a grid build over millions
        // of triangles in front of the user every time they pick a lineup.
        public Lazy<TriangleCollider> Collider { get; } = new(() =>
        {
            var (min, max) = Mesh.ComputeBounds();
            return new TriangleCollider(Mesh, min, max, Mesh.GrenadeSolidFilter());
        }, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public static int Run(Dictionary<string, string> options)
    {
        var port = int.Parse(options.GetValueOrDefault("port", "8137"), CultureInfo.InvariantCulture);
        var root = Path.GetFullPath(options.GetValueOrDefault("root", "."));
        var attrs = options.GetValueOrDefault("attrs", "");
        var bind = options.GetValueOrDefault("bind", "localhost");
        if (bind is not ("localhost" or "any"))
        {
            Console.Error.WriteLine($"error: --bind must be 'localhost' or 'any', got '{bind}'");
            return 1;
        }

        // Every extracted map (`extract --map <name>`) leaves a self-describing
        // data/<name>.s2geo behind (MapName/GameBuildId are baked into the
        // file itself - see CollisionMesh.Load), so the full map list is just
        // whatever is sitting in data/, no separate registry to keep in sync.
        var maps = new Dictionary<string, MapEntry>(StringComparer.OrdinalIgnoreCase);
        var dataDir = Path.Combine(root, "data");
        if (Directory.Exists(dataDir))
        {
            foreach (var geoPath in Directory.EnumerateFiles(dataDir, "*.s2geo").OrderBy(p => p, StringComparer.Ordinal))
            {
                var mapOptions = new Dictionary<string, string>(options) { ["geo"] = geoPath };
                var (mesh, _, _, attributeFilter) = LoadCommon(mapOptions);
                var constants = LoadConstants(mapOptions);
                List<NavAreaJson>? navAreas = null;
                var navPath = Path.Combine(dataDir, $"{mesh.MapName}.navareas.json");
                if (File.Exists(navPath))
                {
                    navAreas = JsonSerializer.Deserialize<List<NavAreaJson>>(File.ReadAllText(navPath));
                }
                var payload = MeshPayload(mesh, attributeFilter);
                // Raw vertex/index floats and ints compress well (measured
                // ~55% smaller with plain gzip) but this is application/
                // octet-stream, which neither Cloudflare's edge nor the
                // already-Draco-compressed .glb exports get automatic
                // compression for - so it's compressed once here, at load
                // time, and served pre-compressed rather than paying that
                // cost on every request.
                var payloadGzip = Gzip(payload);
                var entry = new MapEntry(mesh, attributeFilter, navAreas, constants, payload, payloadGzip, $"\"{mesh.GameBuildId}\"");
                // Keyed by the game build the mesh came from, so a CS2 update
                // simply misses the old blob rather than serving stale geometry.
                var brotliPath = BrotliCachePath(dataDir, mesh.MapName, mesh.GameBuildId);
                if (File.Exists(brotliPath))
                {
                    entry.MeshPayloadBrotli = File.ReadAllBytes(brotliPath);
                }
                maps[mesh.MapName] = entry;
                Console.WriteLine($"map loaded: {mesh.MapName} ({navAreas?.Count ?? 0} nav areas)");
            }
        }
        if (maps.Count == 0)
        {
            Console.WriteLine("no maps found under data/*.s2geo - run `extract --map <name>` first; static file serving still works");
        }
        StartBrotliPrecompress(maps, dataDir);

        var builder = WebApplication.CreateSlimBuilder();
        // Keep CLI output as quiet as the old server: warnings and errors only.
        // Host startup failures are muted entirely because the bind-failure
        // catch below already prints a friendlier one-liner.
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.Logging.AddFilter("Microsoft.Extensions.Hosting.Internal.Host", LogLevel.None);
        // Loopback by default: this server exposes local files and an expensive
        // solver, so a routable interface is opt-in only, via --bind any (e.g.
        // inside a container reachable solely through a reverse proxy on the
        // same Docker network, never through a routable host interface directly).
        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            if (bind == "any")
            {
                kestrel.ListenAnyIP(port);
            }
            else
            {
                kestrel.ListenLocalhost(port);
            }
        });
        using var app = builder.Build();

        app.MapGet("/api/maps", () => Results.Json(maps
            .OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => new { map = kv.Key, hasLineups = kv.Value.NavAreas != null })));

        app.MapGet("/api/mesh", (HttpContext context, string? map) =>
        {
            if (map == null || !maps.TryGetValue(map, out var entry))
            {
                return Results.NotFound();
            }
            context.Response.Headers.ETag = entry.BuildETag;
            context.Response.Headers.CacheControl = "public, max-age=604800";
            // Three different bodies share this URL. Without this a cache (the
            // browser's, or Cloudflare's now that it holds these) can hand a
            // Brotli body to a client that only asked for gzip.
            context.Response.Headers.Vary = "Accept-Encoding";
            if (context.Request.Headers.IfNoneMatch.ToString().Contains(entry.BuildETag, StringComparison.Ordinal))
            {
                return Results.StatusCode(StatusCodes.Status304NotModified);
            }
            var accepted = context.Request.Headers.AcceptEncoding.ToString();
            var brotli = entry.MeshPayloadBrotli;
            if (brotli != null && accepted.Contains("br", StringComparison.OrdinalIgnoreCase))
            {
                context.Response.Headers.ContentEncoding = "br";
                return Results.Bytes(brotli, "application/octet-stream");
            }
            if (accepted.Contains("gzip", StringComparison.OrdinalIgnoreCase))
            {
                context.Response.Headers.ContentEncoding = "gzip";
                return Results.Bytes(entry.MeshPayloadGzip, "application/octet-stream");
            }
            return Results.Bytes(entry.MeshPayload, "application/octet-stream");
        });

        // The flight path of one lineup, fetched when it is selected rather than
        // shipped with every result: a map-wide solve returns hundreds of lineups
        // and only ever one is drawn.
        app.MapGet("/api/trajectory", (HttpContext context, string? map,
            float x, float y, float z, string? type, float pitch, float yaw, float strength) =>
        {
            if (map == null || !maps.TryGetValue(map, out var entry))
            {
                return Results.NotFound();
            }
            if (!Enum.TryParse<ThrowType>(type, ignoreCase: true, out var throwType))
            {
                return Results.BadRequest($"unknown throw type '{type}'");
            }
            if (!float.IsFinite(x) || !float.IsFinite(y) || !float.IsFinite(z) ||
                !float.IsFinite(pitch) || !float.IsFinite(yaw) || !float.IsFinite(strength))
            {
                return Results.BadRequest("non-finite throw parameter");
            }
            var eye = new Vector3(x, y, z + GrenadeTrajectory.EyeHeight(throwType));
            var spec = new ThrowSpec(eye, yaw, pitch, throwType, strength);
            var payload = TrajectoryPayload(entry.Collider.Value, spec, entry.Constants);
            // Deterministic for a given throw on a given build, so it never needs
            // recomputing for a lineup the viewer has already drawn.
            context.Response.Headers.ETag = entry.BuildETag;
            context.Response.Headers.CacheControl = "public, max-age=604800";
            return Results.Bytes(payload, "application/json");
        });

        app.MapPost("/api/lineup", async (HttpContext context) =>
        {
            if (maps.Count == 0)
            {
                context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                context.Response.ContentType = "application/json";
                await context.Response.WriteAsync("{\"error\":\"no maps extracted yet - run extract --map <name> first\"}");
                return;
            }
            if (context.Request.ContentLength is > MaxLineupBodyBytes)
            {
                await WriteApiError(context, StatusCodes.Status400BadRequest, "request body too large");
                return;
            }
            // Chunked uploads carry no Content-Length, so enforce the cap while reading.
            var buffer = new byte[MaxLineupBodyBytes + 1];
            var read = 0;
            int n;
            while ((n = await context.Request.Body.ReadAsync(buffer.AsMemory(read), context.RequestAborted)) > 0)
            {
                read += n;
                if (read > MaxLineupBodyBytes)
                {
                    await WriteApiError(context, StatusCodes.Status400BadRequest, "request body too large");
                    return;
                }
            }
            JsonDocument body;
            try
            {
                body = JsonDocument.Parse(buffer.AsMemory(0, read));
            }
            catch (JsonException)
            {
                await WriteApiError(context, StatusCodes.Status400BadRequest, "body must be valid JSON");
                return;
            }
            using (body)
            {
                if (!body.RootElement.TryGetProperty("map", out var mapEl) || mapEl.ValueKind != JsonValueKind.String ||
                    !maps.TryGetValue(mapEl.GetString() ?? "", out var entry) || entry.NavAreas == null)
                {
                    await WriteApiError(context, StatusCodes.Status400BadRequest, "'map' must name a map with nav data (see /api/maps)");
                    return;
                }
                var (mesh, attributeFilter, navAreas, serveConstants) = (entry.Mesh, entry.AttributeFilter, entry.NavAreas, entry.Constants);

                if (ValidateLineupQuery(body.RootElement, mesh) is { } validationError)
                {
                    await WriteApiError(context, StatusCodes.Status400BadRequest, validationError);
                    return;
                }

                // Repeat clicks are free: results are cached on disk keyed by build,
                // constants, and the quantized query. A new game build or recalibration
                // changes the key, so stale answers cannot leak through.
                var cacheKey = QueryCacheKey(mesh, serveConstants, body.RootElement, attrs);
                var cachePath = Path.Combine("data", "cache", cacheKey + ".json");

                // Progress streams as NDJSON so the viewer can paint each evaluated
                // origin live: phase lines, then batches of checked [x, y, hits]
                // cells, then a final result line. Cache files keep the bare result
                // JSON, so cached replies are just that single last line.
                context.Response.ContentType = "application/x-ndjson";
                var clientGone = false;
                async Task WriteLine(string line)
                {
                    if (clientGone)
                    {
                        return;
                    }
                    try
                    {
                        await context.Response.WriteAsync(line + "\n", CancellationToken.None);
                        await context.Response.Body.FlushAsync(CancellationToken.None);
                    }
                    catch
                    {
                        // The solve keeps running so its result still lands in the
                        // cache; a reload after cancel then answers instantly.
                        clientGone = true;
                    }
                }

                if (File.Exists(cachePath))
                {
                    var cached = await File.ReadAllTextAsync(cachePath, context.RequestAborted);
                    await WriteLine("{\"result\":" + cached + "}");
                    return;
                }

                await SolveGate.WaitAsync(context.RequestAborted);
                try
                {
                    var events = new System.Collections.Concurrent.ConcurrentQueue<(string Kind, int[] Data)>();
                    var solveTask = Task.Run(() => RunTargetQuery(
                        mesh, attributeFilter, navAreas, body.RootElement, serveConstants,
                        onPhase: (phase, count) => events.Enqueue((phase, [count])),
                        onOrigin: (feet, hits) => events.Enqueue(("origin", [(int)MathF.Round(feet.X), (int)MathF.Round(feet.Y), hits])),
                        onCandidate: (feet, ok) => events.Enqueue(("cand", [(int)MathF.Round(feet.X), (int)MathF.Round(feet.Y), ok ? 1 : 0]))));
                    while (!solveTask.IsCompleted)
                    {
                        await Task.WhenAny(solveTask, Task.Delay(100));
                        foreach (var line in DrainProgress(events))
                        {
                            await WriteLine(line);
                        }
                    }
                    string response;
                    try
                    {
                        response = await solveTask;
                    }
                    catch (Exception e)
                    {
                        // The 200 header is already on the wire, so failures must
                        // travel in-band as an error line.
                        Console.Error.WriteLine($"lineup solve failed: {e.Message}");
                        await WriteLine("{\"error\":\"solver failure - check server log\"}");
                        return;
                    }
                    Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
                    await File.WriteAllTextAsync(cachePath, response);
                    foreach (var line in DrainProgress(events))
                    {
                        await WriteLine(line);
                    }
                    await WriteLine("{\"result\":" + response + "}");
                }
                finally
                {
                    SolveGate.Release();
                }
            }
        });

        app.MapGet("/", (HttpContext context) => ServeStatic(context, root, "viewer/index.html"));
        app.MapGet("/viewer/{**rest}", (HttpContext context, string? rest) => ServeStatic(context, root, "viewer/" + (rest ?? "")));
        app.MapGet("/data/{**rest}", (HttpContext context, string? rest) => ServeStatic(context, root, "data/" + (rest ?? "")));

        try
        {
            app.Start();
        }
        catch (IOException e)
        {
            Console.Error.WriteLine($"error: cannot listen on port {port} ({e.Message}) - is another serve instance running? Use --port to pick a different one.");
            return 1;
        }
        Console.WriteLine($"serving {root} at http://localhost:{port}/  (ctrl-c to stop)");
        // The host intercepts SIGINT/ctrl-c, drains in-flight requests, and
        // returns here instead of throwing.
        app.WaitForShutdown();
        return 0;
    }

    // Consecutive origin events collapse into one checked-batch line per drain
    // (~100ms), keeping the stream at a handful of lines per second regardless
    // of how fast the parallel sweep completes origins.
    static List<string> DrainProgress(System.Collections.Concurrent.ConcurrentQueue<(string Kind, int[] Data)> events)
    {
        var lines = new List<string>();
        var batch = new List<int[]>();
        string? batchKind = null;
        void FlushBatch()
        {
            if (batch.Count > 0)
            {
                var field = batchKind == "origin" ? "checked" : "verified";
                lines.Add($"{{\"{field}\":" + JsonSerializer.Serialize(batch) + "}");
                batch = [];
            }
        }
        while (events.TryDequeue(out var e))
        {
            if (e.Kind is "origin" or "cand")
            {
                if (batchKind != e.Kind)
                {
                    FlushBatch();
                    batchKind = e.Kind;
                }
                batch.Add(e.Data);
                continue;
            }
            FlushBatch();
            lines.Add($"{{\"phase\":\"{e.Kind}\",\"count\":{e.Data[0]}}}");
        }
        FlushBatch();
        return lines;
    }

    static Task WriteApiError(HttpContext context, int status, string message)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json";
        return context.Response.WriteAsync(JsonSerializer.Serialize(new { error = message }));
    }

    static IResult ServeStatic(HttpContext context, string root, string relative)
    {
        // Kestrel already collapses dot segments, but canonicalize and check
        // anyway: nothing outside the viewer/ and data/ subtrees is servable.
        var full = Path.GetFullPath(Path.Combine(root, relative));
        var viewerRoot = Path.Combine(root, "viewer") + Path.DirectorySeparatorChar;
        var dataRoot = Path.Combine(root, "data") + Path.DirectorySeparatorChar;
        if (!full.StartsWith(viewerRoot, StringComparison.Ordinal) && !full.StartsWith(dataRoot, StringComparison.Ordinal))
        {
            return Results.NotFound();
        }
        if (!File.Exists(full))
        {
            return Results.NotFound();
        }
        var contentType = Path.GetExtension(full) switch
        {
            ".html" => "text/html; charset=utf-8",
            ".json" => "application/json",
            ".js" or ".mjs" => "text/javascript",
            ".css" => "text/css",
            ".svg" => "image/svg+xml",
            _ => "application/octet-stream",
        };

        // index.html and data/*.json both revalidate on every load, keyed to
        // their own content+mtime - it used to share one game-build ETag
        // across every map's data file, which meant editing index.html
        // without changing the map build (i.e. every routine viewer edit)
        // left browsers serving a stale cached copy indefinitely, and now
        // that multiple maps' JSON coexist under data/ there is no single
        // "the" build id to share anyway. Vendored libs are stable for a
        // day, and the rest of viewer/ revalidates because those files
        // change during development.
        string? etag = null;
        if (relative == "viewer/index.html" || (relative.StartsWith("data/", StringComparison.Ordinal) && contentType == "application/json"))
        {
            context.Response.Headers.CacheControl = "no-cache";
            etag = FileETag(full);
        }
        else if (relative.StartsWith("viewer/lib/", StringComparison.Ordinal))
        {
            context.Response.Headers.CacheControl = "max-age=86400";
        }
        else if (relative.StartsWith("viewer/", StringComparison.Ordinal))
        {
            context.Response.Headers.CacheControl = "no-cache";
        }
        else if (relative.StartsWith("data/", StringComparison.Ordinal))
        {
            // The textured GLBs (tens of MB each) and radar PNGs had no
            // Cache-Control at all, which left Cloudflare treating every
            // request as uncacheable ("DYNAMIC") - every viewer visit's
            // multi-map textured 3D load was hitting this server directly,
            // full size, every time. These only change when a map gets
            // re-processed, so a week is safe; the ETag still catches it if
            // that happens before the week is up.
            context.Response.Headers.CacheControl = "public, max-age=604800";
            etag = FileETag(full);
        }
        if (etag != null)
        {
            context.Response.Headers.ETag = etag;
            if (context.Request.Headers.IfNoneMatch.ToString().Contains(etag, StringComparison.Ordinal))
            {
                return Results.StatusCode(StatusCodes.Status304NotModified);
            }
        }
        // Results.File streams from disk: data/ can contain multi-hundred-MB
        // exports, and buffering them spiked process memory per request.
        return Results.File(full, contentType);
    }

    // For serve without --geo there is no build id; fall back to file identity.
    static string FileETag(string path)
    {
        var info = new FileInfo(path);
        return $"\"{info.LastWriteTimeUtc.Ticks:x}-{info.Length:x}\"";
    }

    static string BrotliCachePath(string dataDir, string mapName, string gameBuildId) =>
        Path.Combine(dataDir, "cache", $"{mapName}-{gameBuildId}.mesh.br");

    // Compresses whatever the previous run did not already leave on disk, on a
    // dedicated below-normal thread rather than the ThreadPool: this is minutes of
    // solid CPU on the larger maps, and the ThreadPool is what the solver's
    // Parallel.ForEach draws its workers from. Serving is already live throughout,
    // handing out gzip until each blob lands.
    static void StartBrotliPrecompress(Dictionary<string, MapEntry> maps, string dataDir)
    {
        var pending = maps.Where(kv => kv.Value.MeshPayloadBrotli == null).ToList();
        if (pending.Count == 0)
        {
            return;
        }
        var thread = new Thread(() =>
        {
            foreach (var (name, entry) in pending)
            {
                try
                {
                    var blob = Brotli(entry.MeshPayload);
                    var path = BrotliCachePath(dataDir, name, entry.Mesh.GameBuildId);
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    // Via a temp file so a kill mid-write cannot leave a truncated
                    // blob that the next startup would happily serve as a mesh.
                    var temp = path + ".tmp";
                    File.WriteAllBytes(temp, blob);
                    File.Move(temp, path, overwrite: true);
                    entry.MeshPayloadBrotli = blob;
                    Console.WriteLine($"brotli ready: {name} ({entry.MeshPayloadGzip.Length / 1_000_000.0:F1}MB gzip -> {blob.Length / 1_000_000.0:F1}MB)");
                }
                catch (Exception ex)
                {
                    // Nothing here is load-bearing - gzip keeps being served.
                    Console.Error.WriteLine($"brotli precompress failed for {name}: {ex.Message}");
                }
            }
        })
        {
            IsBackground = true,
            Priority = ThreadPriority.BelowNormal,
            Name = "brotli-precompress",
        };
        thread.Start();
    }

    static byte[] Brotli(byte[] data)
    {
        using var output = new MemoryStream();
        using (var brotli = new BrotliStream(output, CompressionLevel.SmallestSize))
        {
            brotli.Write(data);
        }
        return output.ToArray();
    }

    static byte[] Gzip(byte[] data)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Optimal))
        {
            gzip.Write(data);
        }
        return output.ToArray();
    }
}

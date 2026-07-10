using System.Globalization;
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

    public static int Run(Dictionary<string, string> options)
    {
        var port = int.Parse(options.GetValueOrDefault("port", "8137"), CultureInfo.InvariantCulture);
        var root = Path.GetFullPath(options.GetValueOrDefault("root", "."));

        // With --geo and --nav the server also answers interactive lineup queries.
        CollisionMesh? mesh = null;
        Func<byte, bool>? attributeFilter = null;
        List<NavAreaJson>? navAreas = null;
        var serveConstants = ThrowConstants.Default;
        if (options.ContainsKey("geo"))
        {
            (mesh, _, _, attributeFilter) = LoadCommon(options);
            serveConstants = LoadConstants(options);
            if (options.TryGetValue("nav", out var navPath))
            {
                navAreas = JsonSerializer.Deserialize<List<NavAreaJson>>(File.ReadAllText(navPath));
            }
            Console.WriteLine($"lineup API enabled for {mesh.MapName} ({navAreas?.Count ?? 0} nav areas)");
        }
        var attrs = options.GetValueOrDefault("attrs", "");

        // Mesh and filter are fixed for the process lifetime, so the binary
        // payload is built exactly once here and captured by the handler.
        var meshPayload = mesh != null ? MeshPayload(mesh, attributeFilter) : null;
        var buildETag = mesh != null ? $"\"{mesh.GameBuildId}\"" : null;

        var builder = WebApplication.CreateSlimBuilder();
        // Keep CLI output as quiet as the old server: warnings and errors only.
        // Host startup failures are muted entirely because the bind-failure
        // catch below already prints a friendlier one-liner.
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.Logging.AddFilter("Microsoft.Extensions.Hosting.Internal.Host", LogLevel.None);
        // Loopback only: this server exposes local files and an expensive
        // solver, so it must never bind a routable interface.
        builder.WebHost.ConfigureKestrel(kestrel => kestrel.ListenLocalhost(port));
        using var app = builder.Build();

        app.MapGet("/api/mesh", (HttpContext context) =>
        {
            if (meshPayload == null || buildETag == null)
            {
                return Results.NotFound();
            }
            context.Response.Headers.ETag = buildETag;
            if (context.Request.Headers.IfNoneMatch.ToString().Contains(buildETag, StringComparison.Ordinal))
            {
                return Results.StatusCode(StatusCodes.Status304NotModified);
            }
            return Results.Bytes(meshPayload, "application/octet-stream");
        });

        app.MapPost("/api/lineup", async (HttpContext context) =>
        {
            if (mesh == null || navAreas == null)
            {
                context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                context.Response.ContentType = "application/json";
                await context.Response.WriteAsync("{\"error\":\"start serve with --geo, --nav (and --attrs) to enable lineup queries\"}");
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
                string response;
                if (File.Exists(cachePath))
                {
                    response = await File.ReadAllTextAsync(cachePath, context.RequestAborted);
                }
                else
                {
                    await SolveGate.WaitAsync(context.RequestAborted);
                    try
                    {
                        response = RunTargetQuery(mesh, attributeFilter, navAreas, body.RootElement, serveConstants);
                    }
                    finally
                    {
                        SolveGate.Release();
                    }
                    Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
                    await File.WriteAllTextAsync(cachePath, response);
                }
                context.Response.ContentType = "application/json";
                await context.Response.WriteAsync(response);
            }
        });

        app.MapGet("/", (HttpContext context) => ServeStatic(context, root, "viewer/index.html", buildETag));
        app.MapGet("/viewer/{**rest}", (HttpContext context, string? rest) => ServeStatic(context, root, "viewer/" + (rest ?? ""), buildETag));
        app.MapGet("/data/{**rest}", (HttpContext context, string? rest) => ServeStatic(context, root, "data/" + (rest ?? ""), buildETag));

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

    static Task WriteApiError(HttpContext context, int status, string message)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json";
        return context.Response.WriteAsync(JsonSerializer.Serialize(new { error = message }));
    }

    static IResult ServeStatic(HttpContext context, string root, string relative, string? buildETag)
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
            _ => "application/octet-stream",
        };

        // index.html and data JSON revalidate on every load (ETag keyed to the
        // game build so a re-extract busts them), vendored libs are stable for
        // a day, and the rest of viewer/ revalidates because those files change
        // during development.
        string? etag = null;
        if (relative == "viewer/index.html" ||
            (relative.StartsWith("data/", StringComparison.Ordinal) && contentType == "application/json"))
        {
            context.Response.Headers.CacheControl = "no-cache";
            etag = buildETag ?? FileETag(full);
        }
        else if (relative.StartsWith("viewer/lib/", StringComparison.Ordinal))
        {
            context.Response.Headers.CacheControl = "max-age=86400";
        }
        else if (relative.StartsWith("viewer/", StringComparison.Ordinal))
        {
            context.Response.Headers.CacheControl = "no-cache";
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
}

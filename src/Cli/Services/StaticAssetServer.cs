using Microsoft.AspNetCore.Http;

namespace SmokeSolver.Cli;

/// <summary>
/// Static file serving for the viewer and data trees: path containment,
/// content types, and the cache-control/ETag revalidation policy.
/// </summary>
public static class StaticAssetServer
{
    /// <summary>
    /// True when the request's If-None-Match already names this ETag, i.e. the
    /// client's stored copy is current and a 304 should be returned.
    /// </summary>
    public static bool IsNotModified(HttpContext context, string etag) =>
        context.Request.Headers.IfNoneMatch.ToString().Contains(etag, StringComparison.Ordinal);

    public static IResult ServeStatic(HttpContext context, string root, string relative)
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
            // The textured GLBs (tens of MB each) and radar PNGs must revalidate
            // rather than cache blind for a week: re-processing a map (e.g. an
            // extraction fix) changes these files without any URL change, and a
            // week-long fresh window left browsers showing the old geometry
            // (giant props, deleted brushes) until it expired. no-cache still
            // lets the browser STORE the file - it just reconditions each load
            // against the ETag, so an unchanged file comes back as a 304 with no
            // re-download and only a changed file pays for a fresh transfer.
            context.Response.Headers.CacheControl = "no-cache";
            etag = FileETag(full);
        }
        if (etag != null)
        {
            context.Response.Headers.ETag = etag;
            if (IsNotModified(context, etag))
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

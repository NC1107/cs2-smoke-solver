using System.Text.Json;
using SmokeSolver.Extraction;
using SmokeSolver.Sim;
using static SmokeSolver.Cli.LineupApi;
using static SmokeSolver.Cli.MeshSetup;
using static SmokeSolver.Cli.HttpCompression;

namespace SmokeSolver.Cli;

// One mesh/nav/payload set per discovered map, fixed for the process
// lifetime just like the old single-map fields were - just keyed by map
// name so the viewer can switch maps without restarting the server.
public sealed record MapEntry(
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

    // Player-solid twin of the collider above (clips included), for the slack
    // ring's "can the player actually slide/stand there" probes.
    public Lazy<TriangleCollider> PlayerCollider { get; } = new(() =>
    {
        var (min, max) = Mesh.ComputeBounds();
        return new TriangleCollider(Mesh, min, max, Mesh.PlayerSolidFilter());
    }, LazyThreadSafetyMode.ExecutionAndPublication);
}

/// <summary>
/// Discovers, loads, and version-stamps every extracted map under data/, and
/// owns the background brotli precompression of their mesh payloads. Map
/// lifecycle only - no HTTP concerns.
/// </summary>
public static class MapRegistry
{
    /// <summary>
    /// Every extracted map (`extract --map &lt;name&gt;`) leaves a self-describing
    /// data/&lt;name&gt;.s2geo behind (MapName/GameBuildId are baked into the file
    /// itself - see CollisionMesh.Load), so the full map list is just whatever
    /// is sitting in data/, no separate registry to keep in sync.
    /// </summary>
    public static Dictionary<string, MapEntry> LoadMaps(string root, Dictionary<string, string> options)
    {
        var maps = new Dictionary<string, MapEntry>(StringComparer.OrdinalIgnoreCase);
        var dataDir = Path.Combine(root, "data");
        if (!Directory.Exists(dataDir))
        {
            return maps;
        }
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
            // The 3D view shows exactly what the grenade sim collides with (see
            // MeshPayloadSolid), not the --attrs subset the voxel sweep uses -
            // so movement clips stop reading as walls and the invisible blockers
            // (grenade-clips, glass) become visible.
            var payload = MeshPayloadSolid(mesh);
            // Raw vertex/index floats and ints compress well (measured ~55%
            // smaller with plain gzip) but this is application/octet-stream,
            // which neither Cloudflare's edge nor the already-Draco-compressed
            // .glb exports get automatic compression for - so it's compressed
            // once here, at load time, and served pre-compressed rather than
            // paying that cost on every request.
            var payloadGzip = Gzip(payload);
            // Identify the mesh by the content it actually serves, not just
            // the game build: extraction changes (e.g. excluding a game
            // mode's brushes) alter the geometry without bumping the CS2
            // build, and a build-only ETag then leaves browsers on the old
            // mesh for the full cache week. The payload hash changes whenever
            // the bytes do, so both the client ETag and the precompressed
            // brotli cache below invalidate exactly when the mesh does.
            var meshVersion = $"{mesh.GameBuildId}-{Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(payload))[..12].ToLowerInvariant()}";
            var entry = new MapEntry(mesh, attributeFilter, navAreas, constants, payload, payloadGzip, $"\"{meshVersion}\"");
            var brotliPath = BrotliCachePath(dataDir, mesh.MapName, meshVersion);
            if (File.Exists(brotliPath))
            {
                entry.MeshPayloadBrotli = File.ReadAllBytes(brotliPath);
            }
            maps[mesh.MapName] = entry;
            Console.WriteLine($"map loaded: {mesh.MapName} ({navAreas?.Count ?? 0} nav areas)");
        }
        return maps;
    }

    static string BrotliCachePath(string dataDir, string mapName, string version) =>
        Path.Combine(dataDir, "cache", $"{mapName}-{version}.mesh.br");

    /// <summary>
    /// Drops cache files nothing will read again: query results older than 30
    /// days (their keys rotate with every solver/mesh change, so old files are
    /// never referenced, only accumulated) and brotli mesh blobs whose
    /// content-versioned name no longer matches any loaded map.
    /// </summary>
    public static void PruneCache(string root, Dictionary<string, MapEntry> maps)
    {
        var cacheDir = Path.Combine(root, "data", "cache");
        if (!Directory.Exists(cacheDir))
        {
            return;
        }
        var live = maps
            .Select(kv => Path.GetFileName(BrotliCachePath("", kv.Key, kv.Value.BuildETag.Trim('"'))))
            .ToHashSet(StringComparer.Ordinal);
        var cutoff = DateTime.UtcNow.AddDays(-30);
        var pruned = 0;
        try
        {
            foreach (var file in Directory.EnumerateFiles(cacheDir))
            {
                var name = Path.GetFileName(file);
                var stale =
                    (name.EndsWith(".json", StringComparison.Ordinal) && File.GetLastWriteTimeUtc(file) < cutoff) ||
                    (name.EndsWith(".mesh.br", StringComparison.Ordinal) && !live.Contains(name));
                if (stale)
                {
                    File.Delete(file);
                    pruned++;
                }
            }
        }
        catch (Exception e)
        {
            // Best-effort housekeeping; a locked or foreign-owned file is not
            // worth failing startup over.
            Console.Error.WriteLine($"cache prune stopped early: {e.Message}");
        }
        if (pruned > 0)
        {
            Console.WriteLine($"cache: pruned {pruned} stale file(s)");
        }
    }

    // Compresses whatever the previous run did not already leave on disk, on a
    // dedicated below-normal thread rather than the ThreadPool: this is minutes of
    // solid CPU on the larger maps, and the ThreadPool is what the solver's
    // Parallel.ForEach draws its workers from. Serving is already live throughout,
    // handing out gzip until each blob lands.
    public static void StartBrotliPrecompress(Dictionary<string, MapEntry> maps, string root)
    {
        var dataDir = Path.Combine(root, "data");
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
                    // Same content-versioned key the entry's ETag carries, so
                    // the on-disk brotli blob tracks the served mesh exactly.
                    var path = BrotliCachePath(dataDir, name, entry.BuildETag.Trim('"'));
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
}

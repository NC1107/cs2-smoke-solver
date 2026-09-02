using System.Numerics;
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
    ThrowConstants Constants, byte[] MeshPayload, byte[] MeshPayloadGzip, string BuildETag,
    // Precomputed by the standspots command: every position the player hull can
    // actually reach and stand on, including the crates and ledges the nav mesh
    // (authored for bots, which never jump) leaves out. Null when the map has
    // not been through that step, in which case the solver falls back to
    // sampling nav areas directly.
    IReadOnlyList<StandSpotOrigin>? StandSpots = null)
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

    // Colliders for "broken" world states (glass shot out, doors open): the
    // named groups are knocked out of the grenade filter. Built lazily per
    // distinct state and kept - there are at most three beyond the default.
    readonly System.Collections.Concurrent.ConcurrentDictionary<string, Lazy<TriangleCollider>> _brokenColliders = new();

    public TriangleCollider ColliderExcluding(IReadOnlyList<string> excludedGroups)
    {
        if (excludedGroups.Count == 0)
        {
            return Collider.Value;
        }
        var key = string.Join(",", excludedGroups.OrderBy(g => g, StringComparer.Ordinal));
        return _brokenColliders.GetOrAdd(key, _ => new Lazy<TriangleCollider>(() =>
        {
            var (min, max) = Mesh.ComputeBounds();
            var baseFilter = Mesh.GrenadeSolidFilter();
            var excluded = Mesh.GroupMask(excludedGroups);
            return new TriangleCollider(Mesh, min, max, a => baseFilter(a) && !excluded[a]);
        }, LazyThreadSafetyMode.ExecutionAndPublication)).Value;
    }
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
            // A corrupt derived artifact (a truncated navareas/standspots file
            // from an interrupted precompute) must degrade that one map to its
            // documented fallback, not throw out of LoadMaps and take every
            // map on the server down with it.
            List<NavAreaJson>? navAreas = null;
            var navPath = Path.Combine(dataDir, $"{mesh.MapName}.navareas.json");
            if (File.Exists(navPath))
            {
                try
                {
                    navAreas = JsonSerializer.Deserialize<List<NavAreaJson>>(File.ReadAllText(navPath));
                }
                catch (Exception e) when (e is JsonException or IOException)
                {
                    Console.Error.WriteLine($"navareas unreadable for {mesh.MapName} ({e.Message}) - lineup solving disabled for this map");
                }
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
            var meshVersion = $"{mesh.GameBuildId}-{Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(payload))[..12].ToLowerInvariant()}";
            var standSpots = LoadStandSpots(dataDir, mesh.MapName);
            var entry = new MapEntry(mesh, attributeFilter, navAreas, constants, payload, payloadGzip, $"\"{meshVersion}\"", standSpots);
            var brotliPath = BrotliCachePath(dataDir, mesh.MapName, meshVersion);
            if (File.Exists(brotliPath))
            {
                entry.MeshPayloadBrotli = File.ReadAllBytes(brotliPath);
            }
            maps[mesh.MapName] = entry;
            var standNote = standSpots is null ? "no stand spots" : $"{standSpots.Count} stand spots";
            Console.WriteLine($"map loaded: {mesh.MapName} ({navAreas?.Count ?? 0} nav areas, {standNote})");
        }
        return maps;
    }

    static IReadOnlyList<StandSpotOrigin>? LoadStandSpots(string dataDir, string mapName)
    {
        var path = Path.Combine(dataDir, $"{mapName}.standspots.json");
        if (!File.Exists(path))
        {
            return null;
        }
        try
        {
            var file = JsonSerializer.Deserialize<StandSpotsCommand.StandSpotFile>(File.ReadAllText(path));
            return file?.Spots
                .Select(s => new StandSpotOrigin(
                    new System.Numerics.Vector3(s.Feet[0], s.Feet[1], s.Feet[2]),
                    string.Equals(s.Stance, "Crouching", StringComparison.OrdinalIgnoreCase)))
                .ToList();
        }
        catch (Exception e) when (e is JsonException or IOException or NullReferenceException or IndexOutOfRangeException)
        {
            Console.Error.WriteLine($"standspots unreadable for {mapName} ({e.Message}) - falling back to nav-area sampling");
            return null;
        }
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
        foreach (var file in Directory.EnumerateFiles(cacheDir))
        {
            var name = Path.GetFileName(file);
            var stale =
                (name.EndsWith(".json", StringComparison.Ordinal) && File.GetLastWriteTimeUtc(file) < cutoff) ||
                (name.EndsWith(".mesh.br", StringComparison.Ordinal) && !live.Contains(name));
            if (!stale)
            {
                continue;
            }
            try
            {
                File.Delete(file);
                pruned++;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Best-effort housekeeping; a locked or foreign-owned file is
                // not worth failing startup over - but it must not stop the
                // rest of the directory from being swept either.
                Console.Error.WriteLine($"cache prune skipped {name}: {e.Message}");
            }
        }
        if (pruned > 0)
        {
            Console.WriteLine($"cache: pruned {pruned} stale file(s)");
        }
    }

    // Per-map entity data (spawns, named places) parsed from
    // data/<map>.entities.json. Same load-once-and-cache shape as LoadStandSpots
    // above; it lived in the routing file, which meant two copies of the same
    // idiom for adjacent per-map data and two places to fix when it changed.

    public static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (List<float[]> T, List<float[]> Ct)?> SpawnCache = new(StringComparer.Ordinal);

    // Named place volumes (env_cs_place) with their positions: the callout a
    // player would use for a spot. Parsed once per map like the spawns above.
    public static readonly System.Collections.Concurrent.ConcurrentDictionary<string, List<(string Name, float[] Origin)>> PlaceCache = new(StringComparer.Ordinal);

    public static List<(string Name, float[] Origin)> LoadPlaces(string root, string mapName)
    {
        var places = new List<(string, float[])>();
        var path = Path.Combine(root, "data", $"{mapName}.entities.json");
        if (!File.Exists(path))
        {
            return places;
        }
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            foreach (var e in doc.RootElement.EnumerateArray())
            {
                if (e.GetProperty("ClassName").GetString() != "env_cs_place" ||
                    !e.TryGetProperty("Place", out var placeEl) ||
                    placeEl.GetString() is not { Length: > 0 } name)
                {
                    continue;
                }
                var o = e.GetProperty("Origin");
                places.Add((name, [o[0].GetSingle(), o[1].GetSingle(), o[2].GetSingle()]));
            }
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or KeyNotFoundException or IndexOutOfRangeException or IOException)
        {
            Console.Error.WriteLine($"places unreadable for {mapName}: {e.Message}");
        }
        return places;
    }

    // Every spawn on the map, for a search scoped to them.
    public static IReadOnlyList<Vector3> SpawnPoints(string root, string mapName)
    {
        var spawns = SpawnCache.GetOrAdd(mapName, name => LoadSpawns(root, name));
        return spawns is { } s
            ? s.T.Concat(s.Ct).Select(p => new Vector3(p[0], p[1], p[2])).ToList()
            : [];
    }

    // One representative point per side, for the sweep's extra flood fronts.
    // The whole spawn cluster would be a dozen seeds a few feet apart flooding
    // the same corridor; one is all the ordering needs.
    public static IReadOnlyList<Vector3> SpawnFronts(string root, string mapName)
    {
        var spawns = SpawnCache.GetOrAdd(mapName, name => LoadSpawns(root, name));
        if (spawns is not { } s)
        {
            return [];
        }
        var fronts = new List<Vector3>();
        foreach (var side in new[] { s.T, s.Ct })
        {
            if (side.Count == 0)
            {
                continue;
            }
            var mid = side[side.Count / 2];
            fronts.Add(new Vector3(mid[0], mid[1], mid[2]));
        }
        return fronts;
    }

    public static (List<float[]> T, List<float[]> Ct)? LoadSpawns(string root, string mapName)
    {
        var path = Path.Combine(root, "data", $"{mapName}.entities.json");
        var t = new List<float[]>();
        var ct = new List<float[]>();
        if (!File.Exists(path))
        {
            return (t, ct);
        }
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            foreach (var e in doc.RootElement.EnumerateArray())
            {
                var bucket = e.GetProperty("ClassName").GetString() switch
                {
                    "info_player_terrorist" => t,
                    "info_player_counterterrorist" => ct,
                    _ => null,
                };
                if (bucket == null)
                {
                    continue;
                }
                // Skip Wingman (2v2) spawns - they belong to the smaller 2v2
                // layout (enabled=0 in Defusal) and sit in walled-off areas.
                // Valve tags them targetname "[PR#]spawnpoints.2v2".
                var name = e.TryGetProperty("Name", out var n) ? n.GetString() ?? "" : "";
                if (name.Contains("2v2", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                var o = e.GetProperty("Origin");
                bucket.Add([o[0].GetSingle(), o[1].GetSingle(), o[2].GetSingle()]);
            }
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or KeyNotFoundException or IndexOutOfRangeException or IOException)
        {
            Console.Error.WriteLine($"spawns unreadable for {mapName}: {e.Message}");
            return null;
        }
        return (t, ct);
    }

    /// <summary>
    /// Ceiling on the solve cache, enforced while the server runs.
    /// </summary>
    // PruneCache only runs at startup and only drops results older than 30
    // days, so a long-lived container never prunes at all. Each solve result is
    // megabytes and the cache key is the target to a tenth of a unit, so a
    // caller walking that space writes without limit onto a disk this process
    // shares with everything else on the host. Oldest-first, which for a result
    // cache is also least-recently-written.
    const long CacheBudgetBytes = 4L * 1024 * 1024 * 1024;

    // Sweeping is a directory scan, so amortize it: only look once a meaningful
    // amount has been written since the last check.
    const long CacheCheckIntervalBytes = 256L * 1024 * 1024;
    static long cacheBytesSinceSweep;

    public static void NoteCacheWrite(string root, long bytes)
    {
        if (Interlocked.Add(ref cacheBytesSinceSweep, bytes) < CacheCheckIntervalBytes)
        {
            return;
        }
        Interlocked.Exchange(ref cacheBytesSinceSweep, 0);
        try
        {
            EnforceCacheBudget(root);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"cache budget sweep failed: {e.Message}");
        }
    }

    public static void EnforceCacheBudget(string root, long budgetBytes = CacheBudgetBytes)
    {
        var cacheDir = Path.Combine(root, "data", "cache");
        if (!Directory.Exists(cacheDir))
        {
            return;
        }
        // Only solve results. The .mesh.br blobs beside them are the maps
        // themselves - evicting one costs minutes of recompression and they are
        // already bounded by the number of maps.
        var results = Directory.EnumerateFiles(cacheDir, "*.json")
            .Select(f => new FileInfo(f))
            .OrderBy(f => f.LastWriteTimeUtc)
            .ToList();
        var total = results.Sum(f => f.Length);
        var evicted = 0;
        foreach (var file in results)
        {
            if (total <= budgetBytes)
            {
                break;
            }
            var size = file.Length;
            try
            {
                file.Delete();
                total -= size;
                evicted++;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Locked or foreign-owned: skip it and keep going, exactly as
                // the startup pruner does.
                Console.Error.WriteLine($"cache evict skipped {file.Name}: {e.Message}");
            }
        }
        if (evicted > 0)
        {
            Console.WriteLine($"cache: evicted {evicted} result(s) to stay under {budgetBytes / (1024 * 1024)} MB");
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

/// <summary>A precomputed reachable stand position and whether it needs a crouch.</summary>
public readonly record struct StandSpotOrigin(System.Numerics.Vector3 Feet, bool Crouched);

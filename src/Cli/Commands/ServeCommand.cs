using System.Globalization;
using System.Numerics;
using System.Text.Json;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using SmokeSolver.Sim;

using static SmokeSolver.Cli.LineupApi;
using static SmokeSolver.Cli.MapRegistry;
using static SmokeSolver.Cli.StaticAssetServer;
namespace SmokeSolver.Cli;

public static class ServeCommand
{
    // A cache miss triggers a map-wide solve that pegs the CPU for minutes.
    // Cap concurrent solves at two so cached lookups, mesh, and static file
    // requests keep flowing while solves are in flight.
    static readonly SemaphoreSlim SolveGate = new(2);

    // How many requests may WAIT for one of those two slots. The gate bounds
    // concurrency but not arrival rate, so without this an attacker's queue
    // grows without limit and every waiter holds a connection and a solved
    // request's worth of state. Past this, say so with a 429 instead.
    const int MaxQueuedSolves = 16;
    static int queuedSolves;

    // Lineup query bodies are a handful of numbers; anything bigger is abuse.
    const int MaxLineupBodyBytes = 4096;

    // An execute carries several targets, so it gets more room - but not much:
    // it is still only a list of coordinates.
    const int MaxExecuteBodyBytes = 8192;
    // A round has time for a handful of smokes, not a library of them, and each
    // one is a real solve.
    const int MaxExecuteTargets = 6;
    // Only the best few per smoke. An execute is "which throw do I use for
    // this one", not a catalogue - and 400 lineups per target across six
    // targets would be a payload nobody reads.
    const int MaxExecuteLineupsPerSmoke = 8;
    // How far from the pinned spot a throw may be found. An execute means "from
    // HERE", so this is a shuffle, not a search.
    const float DefaultExecuteReach = 96f;
    // "Where can I stand to throw all of these" runs a full map-wide solve per
    // target, so it is capped tighter than the from-here endpoint.
    const int MaxExecuteSpotTargets = 4;
    const int MaxExecuteSpotResults = 12;
    // How far apart two throws' feet may be and still be one stance.
    const float DefaultSameSpot = 96f;

    // Per-IP budget for the solve endpoint. A human clicking targets sends one
    // request per click; this allows a burst of 20 and 10 per 30s after that.
    // The point is not to inconvenience a fast clicker but to make walking the
    // cache key space pointless: the key is the target to a tenth of a unit
    // (deliberately, so two nearby clicks never replay each other's answer),
    // so `x += 0.1` yields unlimited guaranteed cache misses, each one minutes
    // of CPU and megabytes of disk. Concurrency limits alone do not stop that.
    const string SolvePolicy = "solve";
    const int SolveBurst = 20;
    const int SolveRefill = 10;
    static readonly TimeSpan SolveRefillPeriod = TimeSpan.FromSeconds(30);

    // The physics GETs (/api/trajectory, /api/lineup-one, /api/slack) are far
    // cheaper than a solve but were not gated at all, while the solve they
    // compete with for CPU is capped at two. One /api/slack call runs up to 84
    // simulations, and a throw aimed at open sky runs the full 640-tick budget
    // rather than settling early - so a burst of them costs real CPU on a box
    // whose whole point is having some spare for solves. Generous enough that
    // the viewer drawing a page of lineups never notices.
    const string PhysicsPolicy = "physics";
    const int PhysicsBurst = 240;
    const int PhysicsRefill = 120;
    static readonly TimeSpan PhysicsRefillPeriod = TimeSpan.FromSeconds(30);

    // Sign-in and per-account endpoints. Cheap, but a login callback makes a
    // server-side call to Steam, so it must not be hammerable.
    const string AccountPolicy = "account";
    const int AccountBurst = 30;
    const int AccountRefill = 15;
    static readonly TimeSpan AccountRefillPeriod = TimeSpan.FromSeconds(30);

    // A saved set is a few kilobytes; anything bigger is not a set of lineups.
    const int MaxSavedSetBytes = 256 * 1024;
    // Fewer flooded cells than this is not a smoke, it is a pocket: a full
    // bloom at 16u voxels is over a thousand.
    const int MinPlausibleBloomCells = 48;

    // How close a target may be to a named spot and still vote under its name.
    // Matches the viewer's click snap, so what you clicked and what you voted
    // on are the same spot.
    const float VoteSnapRadius = 64f;

    // Behind the reverse proxy every request arrives from the same socket
    // address, so a forwarded header is the only thing that tells one caller
    // from another - but only a header the client cannot write itself will do.
    //
    // X-Forwarded-For is NOT that header. Both Cloudflare and traefik APPEND to
    // it rather than replacing it, so a client-supplied value survives as the
    // first entry: sending a fresh X-Forwarded-For per request bought a fresh
    // rate-limit bucket every time and defeated the limiter completely. That
    // was measured against production, not reasoned about.
    //
    // CF-Connecting-IP is written by Cloudflare on every proxied request and
    // overwrites whatever the client sent, so it cannot be forged from outside.
    // With no Cloudflare in front (local runs, direct traefik), this falls back
    // to the socket address, which over-limits rather than under-limits - the
    // right way round for a fallback.
    public static string ClientKey(HttpContext context) =>
        context.Request.Headers["CF-Connecting-IP"].ToString() is { Length: > 0 } cloudflare
            ? cloudflare.Trim()
            : context.Connection.RemoteIpAddress?.ToString() ?? "unknown";

    // Spawn lists parsed from data/<map>.entities.json, once per map for the
    // process lifetime. Null marks a file that exists but would not parse, so
    // a corrupt file answers with one clean 500 instead of a stack trace per
    // request.
    // How far above a floor to start looking for a ceiling, and how far up to
    // look. Starting a little clear of the surface keeps the ray from striking
    // the floor it starts on; a smoke is 64u across, so a gap smaller than this
    // cannot hold one anyway.
    const float LevelFloorClearance = 8f;
    const float LevelHeadroom = 64f;
    // How far above and below a nav level to look for the real floor. A step is
    // 18u in Source and a nav polygon can average out to about that much off;
    // 32 either way finds the surface without reaching the floor above or below.
    const float LevelSnapUp = 32f;
    const float LevelSnapDown = 32f;


    const string JsonContentType = "application/json";
    const string UnknownMapError = "unknown map (see /api/maps)";


    // The solve cache, in the two operations both the streaming lineup endpoint
    // and the execute endpoints need. Kept together so the "write to a temp
    // sibling and rename" rule - which is what stops a kill mid-write leaving a
    // truncated file that a later hit splices into its NDJSON stream as garbage
    // - cannot be remembered in one place and forgotten in the other.
    static string SolveCachePath(string root, string cacheKey) =>
        Path.Combine(root, "data", "cache", cacheKey + ".json");

    static async Task<string?> ReadSolveCacheAsync(string path, CancellationToken ct) =>
        File.Exists(path) ? await File.ReadAllTextAsync(path, ct) : null;

    static async Task WriteSolveCacheAsync(string root, string path, string json)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + "." + Environment.CurrentManagedThreadId + ".tmp";
        await File.WriteAllTextAsync(temp, json);
        File.Move(temp, path, overwrite: true);
        NoteCacheWrite(root, json.Length);
    }


    // The origin the browser reached us at, for Steam's return_to and realm.
    // Behind traefik the scheme arrives in X-Forwarded-Proto and the host in
    // Host; locally it is plain http. The realm must match return_to's origin
    // exactly or Steam refuses the assertion.
    static string PublicOrigin(HttpContext context)
    {
        var proto = context.Request.Headers["X-Forwarded-Proto"].ToString() is { Length: > 0 } p ? p.Split(',')[0].Trim() : context.Request.Scheme;
        return $"{proto}://{context.Request.Host}";
    }

    static string? SignedInSteamId(HttpContext context, byte[] secret) =>
        SteamAuth.ReadSession(secret, context.Request.Cookies[SteamAuth.CookieName], DateTimeOffset.UtcNow);

    static string SavedSetPath(string root, string steamId) =>
        Path.Combine(root, "data", "users", steamId + ".json");

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

        var maps = LoadMaps(root, options);
        if (maps.Count == 0)
        {
            Console.WriteLine("no maps found under data/*.s2geo - run `extract --map <name>` first; static file serving still works");
        }
        StartBrotliPrecompress(maps, root);
        PruneCache(root, maps);
        var sessionSecret = SteamAuth.LoadOrCreateSecret(root);
        using var votes = new VoteStore(Path.Combine(root, "data", "votes.db"));
        using var steamHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };

        // A solve occupies one pool thread per core (see Cpu.Bound). The pool's
        // default floor is exactly that many, so everything else the request
        // pipeline needs - the progress pump's 100ms timer, and every other
        // user's request - had to wait for the pool's thread-injection hill
        // climb, which adds threads about once a second. Progress arrived in
        // multi-second lumps and concurrent requests hung for the duration of
        // somebody else's solve. Headroom above the solve's own ceiling means
        // those threads are there the moment they are needed.
        ThreadPool.GetMinThreads(out _, out var minIo);
        ThreadPool.SetMinThreads(Environment.ProcessorCount + 4, minIo);

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
            // Don't advertise the server software; it's noise that only helps a
            // scanner fingerprint the stack.
            kestrel.AddServerHeader = false;
            if (bind == "any")
            {
                kestrel.ListenAnyIP(port);
            }
            else
            {
                kestrel.ListenLocalhost(port);
            }
        });
        builder.Services.AddRateLimiter(limiter =>
        {
            limiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            limiter.AddPolicy(SolvePolicy, context => RateLimitPartition.GetTokenBucketLimiter(
                ClientKey(context),
                _ => new TokenBucketRateLimiterOptions
                {
                    TokenLimit = SolveBurst,
                    TokensPerPeriod = SolveRefill,
                    ReplenishmentPeriod = SolveRefillPeriod,
                    // Queueing a rejected solve helps nobody: the caller would
                    // rather be told to slow down than hold a connection open.
                    QueueLimit = 0,
                    AutoReplenishment = true,
                }));
            limiter.AddPolicy(AccountPolicy, context => RateLimitPartition.GetTokenBucketLimiter(
                ClientKey(context),
                _ => new TokenBucketRateLimiterOptions
                {
                    TokenLimit = AccountBurst,
                    TokensPerPeriod = AccountRefill,
                    ReplenishmentPeriod = AccountRefillPeriod,
                    QueueLimit = 0,
                    AutoReplenishment = true,
                }));
            limiter.AddPolicy(PhysicsPolicy, context => RateLimitPartition.GetTokenBucketLimiter(
                ClientKey(context),
                _ => new TokenBucketRateLimiterOptions
                {
                    TokenLimit = PhysicsBurst,
                    TokensPerPeriod = PhysicsRefill,
                    ReplenishmentPeriod = PhysicsRefillPeriod,
                    QueueLimit = 0,
                    AutoReplenishment = true,
                }));
        });
        using var app = builder.Build();
        app.UseRateLimiter();

        // Baseline hardening headers on every response: block MIME sniffing,
        // deny framing (the viewer is never meant to be embedded), and keep
        // referrers off cross-origin navigations. Cheap, and the origin should
        // set them rather than relying on a proxy in front.
        app.Use(async (context, next) =>
        {
            var headers = context.Response.Headers;
            headers["X-Content-Type-Options"] = "nosniff";
            headers["X-Frame-Options"] = "DENY";
            headers["Referrer-Policy"] = "same-origin";
            await next();
        });

        app.MapGet("/api/maps", () => Results.Json(maps
            .OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => new { map = kv.Key, hasLineups = kv.Value.NavAreas != null })));

        // Player spawn positions, read straight from the extracted entity lump
        // (info_player_terrorist / _counterterrorist). Lets the viewer answer
        // "what can I smoke from spawn?" - e.g. a T insta-window on mirage -
        // without the target-first sweep. No radar regen: it reads the same
        // entities.json ViewerDataCommand already leaves in data/.
        app.MapGet("/api/spawns", (string? map) =>
        {
            if (map == null || !maps.TryGetValue(map, out var entry))
            {
                return ApiError(StatusCodes.Status404NotFound, UnknownMapError);
            }
            // Keyed and pathed by the map's own name, not the (case-insensitive)
            // query spelling, and parsed once: the file never changes while the
            // server runs, and this handler shares its ThreadPool with the solver.
            var spawns = SpawnCache.GetOrAdd(entry.Mesh.MapName, name => LoadSpawns(root, name));
            return spawns is { } s
                ? Results.Json(new { t = s.T, ct = s.Ct })
                : ApiError(StatusCodes.Status500InternalServerError, "spawn data unreadable - re-extract the map's entities");
        });

        // The map's own callout names with the position of each place volume.
        // Players think in callouts ("B site", "heaven"), not coordinates, so
        // this is what a name-first search resolves a target against.
        app.MapGet("/api/callouts", (string? map) =>
        {
            if (map == null || !maps.TryGetValue(map, out var entry))
            {
                return ApiError(StatusCodes.Status404NotFound, UnknownMapError);
            }
            var places = PlaceCache.GetOrAdd(entry.Mesh.MapName, name => LoadPlaces(root, name));
            // One entry per name: a callout can be several volumes (nuke has
            // multiple "Hut" markers), and a search result list wants the place,
            // not each brush that spells it.
            var merged = places
                .GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
                .Select(g => new
                {
                    name = g.Key,
                    pos = new[]
                    {
                        g.Average(p => p.Origin[0]),
                        g.Average(p => p.Origin[1]),
                        g.Average(p => p.Origin[2]),
                    },
                    parts = g.Count(),
                })
                .OrderBy(c => c.name, StringComparer.OrdinalIgnoreCase);
            return Results.Json(new { callouts = merged });
        });

        // Which walkable levels are stacked over one 2D point. A top-down click
        // on a map like de_nuke can mean the roof, the bombsite under it, or the
        // site below that; the solver has to pick one (the lowest) and the
        // viewer uses this to let the user say which they meant instead.
        // The volume a smoke placed here would actually fill, so the question
        // "does this target cover what I want it to" can be answered before
        // solving anything. Not a circle: a smoke against a wall does not
        // cover the far side of it, and one in a stairwell climbs instead of
        // spreading - which is exactly what the flood fill already models and
        // a radius drawn on a map cannot say.
        app.MapGet("/api/smoke", (HttpContext context, string? map, float x, float y, float z, bool full = false) =>
        {
            if (map == null || !maps.TryGetValue(map, out var entry))
            {
                return ApiError(StatusCodes.Status404NotFound, UnknownMapError);
            }
            if (!float.IsFinite(x) || !float.IsFinite(y) || !float.IsFinite(z))
            {
                return ApiError(StatusCodes.Status400BadRequest, "non-finite coordinate");
            }
            var mesh = entry.Mesh;
            var (meshMin, meshMax) = mesh.ComputeBounds();
            var at = new Vector3(x, y, z);
            // Outside the map there is nothing to flood through, and the grid
            // build below would be a large allocation for an empty answer.
            if (at.X < meshMin.X - 512 || at.X > meshMax.X + 512 ||
                at.Y < meshMin.Y - 512 || at.Y > meshMax.Y + 512)
            {
                return ApiError(StatusCodes.Status400BadRequest, "point is outside the map bounds");
            }
            var p = full ? SmokeParams.FullReach : SmokeParams.Coverage;
            const float voxel = 16f;
            // Only the region one bloom can reach, padded so the fill is never
            // clipped by the edge of its own grid.
            var pad = new Vector3(p.MaxRadius + 4 * voxel);
            var grid = VoxelGrid.Build(mesh, voxel, at - pad, at + pad, entry.AttributeFilter);
            var (gx, gy, gz) = grid.CellOf(at);
            if (!grid.InBounds(gx, gy, gz))
            {
                return ApiError(StatusCodes.Status400BadRequest, "point is outside the map bounds");
            }
            // A target can sit inside geometry: the canonical targets are pro
            // landing centroids, and landings on both sides of a crate average
            // to a point inside it. The flood then starts in a sealed pocket
            // and stops at a handful of cells - measured 4 at the dust2 xbox
            // spot against 1,330 from 16u higher. A real bloom is never that
            // small, so a tiny result means "started inside something"; lift
            // the start until the volume is a smoke's worth, up to a smoke's
            // own height above the point asked for.
            var smoke = SmokeFloodFill.Fill(grid, at, p);
            for (var lift = voxel; smoke.Cells.Length < MinPlausibleBloomCells && lift <= SmokeParams.CoverageRadius; lift += voxel)
            {
                var lifted = SmokeFloodFill.Fill(grid, at + new Vector3(0, 0, lift), p);
                if (lifted.Cells.Length > smoke.Cells.Length)
                {
                    smoke = lifted;
                }
            }
            // Flat triples rather than nested arrays: a full bloom is a few
            // thousand cells and the nesting roughly doubles the payload for
            // nothing the client needs.
            var cells = new float[smoke.Cells.Length * 3];
            for (var i = 0; i < smoke.Cells.Length; i++)
            {
                var c = grid.CellCenter(smoke.Cells[i]);
                cells[i * 3] = c.X;
                cells[i * 3 + 1] = c.Y;
                cells[i * 3 + 2] = c.Z;
            }
            context.Response.Headers.ETag = entry.BuildETag;
            context.Response.Headers.CacheControl = "public, max-age=604800";
            return Results.Json(new
            {
                voxel = grid.VoxelSize,
                radius = p.MaxRadius,
                // What the same throw would reach at the grenade's documented
                // maximum, so the viewer can draw the optimistic edge faintly
                // around the one it is promising.
                fullRadius = SmokeParams.GameRadius,
                cells,
            });
        }).RequireRateLimiting(PhysicsPolicy);

        // ---- sign in with Steam, and what an account holds ----

        app.MapGet("/auth/steam", (HttpContext context) =>
            Results.Redirect(SteamAuth.LoginUrl(PublicOrigin(context))))
            .RequireRateLimiting(AccountPolicy);

        app.MapGet("/auth/steam/callback", async (HttpContext context) =>
        {
            var query = context.Request.Query.ToDictionary(kv => kv.Key, kv => kv.Value.ToString(), StringComparer.Ordinal);
            var steamId = await SteamAuth.VerifyCallbackAsync(
                query,
                PublicOrigin(context) + "/auth/steam/callback",
                form => SteamAuth.PostToSteamAsync(steamHttp, form));
            if (steamId is null)
            {
                return Results.Redirect("/?signin=failed");
            }
            context.Response.Cookies.Append(SteamAuth.CookieName, SteamAuth.MintSession(sessionSecret, steamId, DateTimeOffset.UtcNow), new CookieOptions
            {
                HttpOnly = true,
                Secure = context.Request.IsHttps || context.Request.Headers["X-Forwarded-Proto"].ToString().StartsWith("https", StringComparison.OrdinalIgnoreCase),
                // Lax: sent on top-level navigation (so the redirect back from
                // Steam lands signed in) but never on a cross-site POST, which
                // is what keeps the write endpoints below CSRF-safe.
                SameSite = SameSiteMode.Lax,
                MaxAge = SteamAuth.SessionLength,
                Path = "/",
            });
            return Results.Redirect("/?signin=ok");
        }).RequireRateLimiting(AccountPolicy);

        app.MapGet("/auth/me", async (HttpContext context) =>
        {
            if (SignedInSteamId(context, sessionSecret) is not { } id)
            {
                return Results.StatusCode(StatusCodes.Status401Unauthorized);
            }
            var profile = await SteamAuth.ProfileAsync(steamHttp, id);
            context.Response.Headers.CacheControl = "no-store";
            return Results.Json(new { steamId = profile.SteamId, name = profile.Name, avatar = profile.Avatar });
        }).RequireRateLimiting(AccountPolicy);

        app.MapPost("/auth/logout", (HttpContext context) =>
        {
            context.Response.Cookies.Delete(SteamAuth.CookieName, new CookieOptions { Path = "/" });
            return Results.NoContent();
        }).RequireRateLimiting(AccountPolicy);

        // The account's saved lineups: one JSON file per SteamID, replaced whole.
        // A set is a few kilobytes and changes when a person clicks a star, so
        // replace-whole with temp-then-rename is both simpler and safer than
        // patching, and the client always holds the full set anyway.
        app.MapGet("/api/me/lineups", async (HttpContext context) =>
        {
            if (SignedInSteamId(context, sessionSecret) is not { } id)
            {
                return Results.StatusCode(StatusCodes.Status401Unauthorized);
            }
            var path = SavedSetPath(root, id);
            context.Response.Headers.CacheControl = "no-store";
            return File.Exists(path)
                ? Results.Text(await File.ReadAllTextAsync(path, context.RequestAborted), JsonContentType)
                : Results.Text("{\"lineups\":[]}", JsonContentType);
        }).RequireRateLimiting(AccountPolicy);

        app.MapPut("/api/me/lineups", async (HttpContext context) =>
        {
            if (SignedInSteamId(context, sessionSecret) is not { } id)
            {
                return Results.StatusCode(StatusCodes.Status401Unauthorized);
            }
            // JSON-only, like every write here: a browser cannot send this
            // content type cross-site without a preflight, and the Lax cookie
            // would not travel on such a request anyway.
            if (context.Request.ContentType is not { } ct || !ct.StartsWith("application/json", StringComparison.OrdinalIgnoreCase))
            {
                return ApiError(StatusCodes.Status415UnsupportedMediaType, "Content-Type must be application/json");
            }
            if (context.Request.ContentLength is > MaxSavedSetBytes)
            {
                return ApiError(StatusCodes.Status413PayloadTooLarge, "saved set too large");
            }
            var buffer = new byte[MaxSavedSetBytes + 1];
            var read = 0;
            int n;
            while ((n = await context.Request.Body.ReadAsync(buffer.AsMemory(read), context.RequestAborted)) > 0)
            {
                read += n;
                if (read > MaxSavedSetBytes)
                {
                    return ApiError(StatusCodes.Status413PayloadTooLarge, "saved set too large");
                }
            }
            string body;
            try
            {
                // Parse, then store the parsed form: what lands on disk is
                // known-valid JSON of the expected shape, not whatever arrived.
                using var doc = JsonDocument.Parse(buffer.AsMemory(0, read));
                if (doc.RootElement.ValueKind != JsonValueKind.Object ||
                    !doc.RootElement.TryGetProperty("lineups", out var ls) || ls.ValueKind != JsonValueKind.Array)
                {
                    return ApiError(StatusCodes.Status400BadRequest, "body must be {\"lineups\":[...]}");
                }
                body = JsonSerializer.Serialize(new { lineups = ls, updated = DateTimeOffset.UtcNow.ToUnixTimeSeconds() });
            }
            catch (JsonException)
            {
                return ApiError(StatusCodes.Status400BadRequest, "body must be valid JSON");
            }
            var path = SavedSetPath(root, id);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temp = path + "." + Environment.CurrentManagedThreadId + ".tmp";
            await File.WriteAllTextAsync(temp, body, context.RequestAborted);
            File.Move(temp, path, overwrite: true);
            return Results.NoContent();
        }).RequireRateLimiting(AccountPolicy);

        // ---- votes: the community's opinion, kept apart from the solver's score ----

        app.MapGet("/api/votes", async (HttpContext context, string? map, float x, float y, float z) =>
        {
            if (map == null || !maps.TryGetValue(map, out var entry))
            {
                return ApiError(StatusCodes.Status404NotFound, UnknownMapError);
            }
            if (!float.IsFinite(x) || !float.IsFinite(y) || !float.IsFinite(z))
            {
                return ApiError(StatusCodes.Status400BadRequest, "non-finite coordinate");
            }
            var key = VoteStore.TargetKey(new Vector3(x, y, z), NamedTargets(root, entry.Mesh.MapName), VoteSnapRadius);
            var me = SignedInSteamId(context, sessionSecret);
            var (tallies, mine) = await votes.AtSpotAsync(entry.Mesh.MapName, key, me, context.RequestAborted);
            context.Response.Headers.CacheControl = "no-store";
            return Results.Json(new
            {
                target = key,
                tallies = tallies.ToDictionary(kv => kv.Key, kv => new { up = kv.Value.Up, down = kv.Value.Down, score = kv.Value.Score }),
                mine,
            });
        }).RequireRateLimiting(AccountPolicy);

        app.MapPost("/api/vote", async (HttpContext context) =>
        {
            if (SignedInSteamId(context, sessionSecret) is not { } steamId)
            {
                return ApiError(StatusCodes.Status401Unauthorized, "sign in with Steam to vote");
            }
            if (context.Request.ContentType is not { } ct || !ct.StartsWith("application/json", StringComparison.OrdinalIgnoreCase))
            {
                return ApiError(StatusCodes.Status415UnsupportedMediaType, "Content-Type must be application/json");
            }
            JsonDocument doc;
            try
            {
                doc = await JsonDocument.ParseAsync(context.Request.Body, cancellationToken: context.RequestAborted);
            }
            catch (JsonException)
            {
                return ApiError(StatusCodes.Status400BadRequest, "body must be valid JSON");
            }
            using (doc)
            {
                var r = doc.RootElement;
                if (r.ValueKind != JsonValueKind.Object ||
                    !r.TryGetProperty("map", out var mEl) || mEl.ValueKind != JsonValueKind.String ||
                    !maps.TryGetValue(mEl.GetString() ?? "", out var entry))
                {
                    return ApiError(StatusCodes.Status404NotFound, UnknownMapError);
                }
                if (!r.TryGetProperty("target", out var tEl) || tEl.ValueKind != JsonValueKind.Array || tEl.GetArrayLength() < 2 ||
                    tEl.EnumerateArray().Any(e => e.ValueKind != JsonValueKind.Number || !float.IsFinite(e.GetSingle())))
                {
                    return ApiError(StatusCodes.Status400BadRequest, "target must be [x,y] or [x,y,z]");
                }
                if (!r.TryGetProperty("lineupId", out var lEl) || lEl.ValueKind != JsonValueKind.String ||
                    lEl.GetString() is not { Length: 16 } lineupId || !lineupId.All(char.IsAsciiHexDigitLower))
                {
                    return ApiError(StatusCodes.Status400BadRequest, "lineupId must be a 16-character lineup id");
                }
                if (!r.TryGetProperty("vote", out var vEl) || vEl.ValueKind != JsonValueKind.Number ||
                    !vEl.TryGetInt32(out var vote) || vote is not (-1 or 0 or 1))
                {
                    return ApiError(StatusCodes.Status400BadRequest, "vote must be 1, -1, or 0 to withdraw");
                }
                var target = new Vector3(tEl[0].GetSingle(), tEl[1].GetSingle(), tEl.GetArrayLength() > 2 ? tEl[2].GetSingle() : 0f);
                var key = VoteStore.TargetKey(target, NamedTargets(root, entry.Mesh.MapName), VoteSnapRadius);
                await votes.CastAsync(entry.Mesh.MapName, key, lineupId, steamId, vote, context.RequestAborted);
                var (tallies, mine) = await votes.AtSpotAsync(entry.Mesh.MapName, key, steamId, context.RequestAborted);
                return Results.Json(new
                {
                    target = key,
                    tallies = tallies.ToDictionary(kv => kv.Key, kv => new { up = kv.Value.Up, down = kv.Value.Down, score = kv.Value.Score }),
                    mine,
                });
            }
        }).RequireRateLimiting(AccountPolicy);

        // The named spots a smoke goes on this map. A click near one snaps to
        // it, which is what gives a target a durable identity: two people who
        // both mean "B doors" get the same coordinate instead of two that
        // differ by a tenth of a unit and never agree on anything after.
        app.MapGet("/api/targets", (HttpContext context, string? map) =>
        {
            if (map == null || !maps.TryGetValue(map, out var entry))
            {
                return ApiError(StatusCodes.Status404NotFound, UnknownMapError);
            }
            var json = TargetsCache.GetOrAdd(entry.Mesh.MapName, name => LoadTargetsJson(root, name));
            context.Response.Headers.CacheControl = "no-cache";
            return Results.Text(json, JsonContentType);
        });

        app.MapGet("/api/levels", (string? map, float x, float y) =>
        {
            if (map == null || !maps.TryGetValue(map, out var entry))
            {
                return ApiError(StatusCodes.Status404NotFound, UnknownMapError);
            }
            if (entry.NavAreas == null)
            {
                return ApiError(StatusCodes.Status400BadRequest, "map has no nav data (see /api/maps)");
            }
            if (!float.IsFinite(x) || !float.IsFinite(y))
            {
                return ApiError(StatusCodes.Status400BadRequest, "non-finite coordinate");
            }
            float[][][] corners = [.. entry.NavAreas.Select(a => a.Corners)];
            // Strict: only floors the click actually lands on, not the ones
            // beside it. A second floor offered for a spot that has none is a
            // question with no right answer.
            var levels = SmokeSolver.Solver.LineupSolver.NavGroundLevels(corners, x, y, strict: true);
            // And only floors with room above them. The nav mesh draws mid on
            // de_dust2 as one polygon that runs under the Xbox, so a click on
            // top of the crate is inside two areas: the crate top and the floor
            // sealed beneath it. Nothing can be thrown into that second one -
            // a smoke needs somewhere to be - so asking about it is asking a
            // question with no answer. Kept when it is the only level, since
            // then the alternative is offering nothing at all.
            var collider = entry.Collider.Value;
            if (levels.Count > 1)
            {
                var open = levels
                    .Where(z => collider.FirstHit(
                        new Vector3(x, y, z + LevelFloorClearance),
                        new Vector3(x, y, z + LevelHeadroom)) == null)
                    .ToList();
                if (open.Count > 0)
                {
                    levels = open;
                }
            }
            // Snapped onto the surface a player would actually stand on. A nav
            // level is the average height of a polygon's corners, so on a slope
            // or a stepped area it sits several units off the real floor - and
            // this height goes out as a setpos, which teleports the player to
            // exactly it. Below the floor is inside the world.
            levels = levels
                .Select(z => SmokeSolver.Solver.LineupSolver.FloorUnderHull(
                    entry.PlayerCollider.Value, new Vector3(x, y, z), LevelSnapUp, LevelSnapDown) ?? z)
                .ToList();
            // Name each level by the callout a player standing on it would be
            // in, so the choice reads "Bombsite A" and not "z -168". Matching
            // at eye height rather than the floor because a place volume's
            // origin sits inside the room, not on its floor.
            var places = PlaceCache.GetOrAdd(entry.Mesh.MapName, name => LoadPlaces(root, name));
            return Results.Json(new
            {
                levels = levels.Select(z =>
                {
                    var best = places
                        .Select(p => (p.Name, D: MathF.Sqrt(
                            (p.Origin[0] - x) * (p.Origin[0] - x) +
                            (p.Origin[1] - y) * (p.Origin[1] - y) +
                            (p.Origin[2] - (z + 64f)) * (p.Origin[2] - (z + 64f)))))
                        .Where(p => p.D < 900f)
                        .OrderBy(p => p.D)
                        .FirstOrDefault();
                    return new { z, name = best.Name };
                }),
            });
        });

        app.MapGet("/api/mesh", (HttpContext context, string? map) =>
        {
            if (map == null || !maps.TryGetValue(map, out var entry))
            {
                return ApiError(StatusCodes.Status404NotFound, UnknownMapError);
            }
            context.Response.Headers.ETag = entry.BuildETag;
            // Revalidate rather than cache blind for a week: the ETag is now the
            // mesh content hash, so re-extracting a map (e.g. removing the Retake
            // tape) must reach clients at once. no-cache still stores the body
            // and returns a 304 when the hash matches, so unchanged meshes cost
            // only a conditional request, not a re-download.
            context.Response.Headers.CacheControl = "no-cache";
            // Three different bodies share this URL. Without this a cache (the
            // browser's, or Cloudflare's now that it holds these) can hand a
            // Brotli body to a client that only asked for gzip.
            context.Response.Headers.Vary = "Accept-Encoding";
            if (IsNotModified(context, entry.BuildETag))
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
            float x, float y, float z, string? type, float pitch, float yaw, float strength, float runDeg = 0f, string? broken = null) =>
        {
            if (map == null || !maps.TryGetValue(map, out var entry))
            {
                return ApiError(StatusCodes.Status404NotFound, UnknownMapError);
            }
            if (!Enum.TryParse<ThrowType>(type, ignoreCase: true, out var throwType))
            {
                return ApiError(StatusCodes.Status400BadRequest, $"unknown throw type '{type}'");
            }
            if (!float.IsFinite(x) || !float.IsFinite(y) || !float.IsFinite(z) ||
                !float.IsFinite(pitch) || !float.IsFinite(yaw) || !float.IsFinite(strength) || !float.IsFinite(runDeg))
            {
                return ApiError(StatusCodes.Status400BadRequest, "non-finite throw parameter");
            }
            if (ParseBroken(broken) is not { Error: null } brokenState)
            {
                return ApiError(StatusCodes.Status400BadRequest, ParseBroken(broken).Error!);
            }
            var eye = new Vector3(x, y, z + GrenadeTrajectory.EyeHeight(throwType));
            var spec = new ThrowSpec(eye, yaw, pitch, throwType, strength, runDeg);
            // Same revalidation the mesh endpoint does. These set an ETag and a
            // week of cache but never answered If-None-Match, so a client doing
            // the right thing paid for the whole payload to be recomputed.
            if (IsNotModified(context, entry.BuildETag))
            {
                return Results.StatusCode(StatusCodes.Status304NotModified);
            }
            var payload = TrajectoryPayload(entry.ColliderExcluding(brokenState.Groups), spec, entry.Constants);
            // Deterministic for a given throw on a given build, so it never needs
            // recomputing for a lineup the viewer has already drawn.
            context.Response.Headers.ETag = entry.BuildETag;
            context.Response.Headers.CacheControl = "public, max-age=604800";
            return Results.Bytes(payload, JsonContentType);
        }).RequireRateLimiting(PhysicsPolicy);

        // One fully-analyzed lineup from its physical spec alone - the shape a
        // map sweep returns per lineup, plus its flight path inline. A shared
        // dashboard link carries exactly this spec, so the viewer can render the
        // single throw the user clicked without solving the rest of the map.
        app.MapGet("/api/lineup-one", (HttpContext context, string? map,
            float x, float y, float z, string? type, float pitch, float yaw, float strength,
            float tx, float ty, float tz, float runDeg = 0f, string? broken = null) =>
        {
            if (map == null || !maps.TryGetValue(map, out var entry))
            {
                return ApiError(StatusCodes.Status404NotFound, UnknownMapError);
            }
            if (!Enum.TryParse<ThrowType>(type, ignoreCase: true, out var throwType))
            {
                return ApiError(StatusCodes.Status400BadRequest, $"unknown throw type '{type}'");
            }
            float[] numbers = [x, y, z, pitch, yaw, strength, tx, ty, tz, runDeg];
            if (numbers.Any(v => !float.IsFinite(v)))
            {
                return ApiError(StatusCodes.Status400BadRequest, "non-finite lineup parameter");
            }
            if (ParseBroken(broken) is not { Error: null } brokenState)
            {
                return ApiError(StatusCodes.Status400BadRequest, ParseBroken(broken).Error!);
            }
            // Same revalidation the mesh endpoint does. These set an ETag and a
            // week of cache but never answered If-None-Match, so a client doing
            // the right thing paid for the whole payload to be recomputed.
            if (IsNotModified(context, entry.BuildETag))
            {
                return Results.StatusCode(StatusCodes.Status304NotModified);
            }
            var payload = LineupOnePayload(
                entry.ColliderExcluding(brokenState.Groups), entry.PlayerCollider.Value, new Vector3(x, y, z), new Vector3(tx, ty, tz),
                throwType, strength, pitch, yaw, runDeg, entry.Constants);
            context.Response.Headers.ETag = entry.BuildETag;
            context.Response.Headers.CacheControl = "public, max-age=604800";
            return Results.Bytes(payload, JsonContentType);
        }).RequireRateLimiting(PhysicsPolicy);

        // The positional slack ring for one lineup: how far the feet can drift
        // per direction before the same aim misses the `within` radius. Fetched
        // on "Go to", so it shares the trajectory endpoint's shape and caching.
        app.MapGet("/api/slack", (HttpContext context, string? map,
            float x, float y, float z, string? type, float pitch, float yaw, float strength,
            float tx, float ty, float tz, float within, float runDeg = 0f, string? broken = null) =>
        {
            if (map == null || !maps.TryGetValue(map, out var entry))
            {
                return ApiError(StatusCodes.Status404NotFound, UnknownMapError);
            }
            if (!Enum.TryParse<ThrowType>(type, ignoreCase: true, out var throwType))
            {
                return ApiError(StatusCodes.Status400BadRequest, $"unknown throw type '{type}'");
            }
            float[] numbers = [x, y, z, pitch, yaw, strength, tx, ty, tz, within, runDeg];
            if (numbers.Any(v => !float.IsFinite(v)))
            {
                return ApiError(StatusCodes.Status400BadRequest, "non-finite slack parameter");
            }
            if (within is < 1f or > 512f)
            {
                return ApiError(StatusCodes.Status400BadRequest, "within must be between 1 and 512");
            }
            if (ParseBroken(broken) is not { Error: null } brokenState)
            {
                return ApiError(StatusCodes.Status400BadRequest, ParseBroken(broken).Error!);
            }
            // Grenade paths honor the broken state; the player-side probes keep
            // the intact world (feet cannot stand where a door swings anyway).
            // Same revalidation the mesh endpoint does. These set an ETag and a
            // week of cache but never answered If-None-Match, so a client doing
            // the right thing paid for the whole payload to be recomputed.
            if (IsNotModified(context, entry.BuildETag))
            {
                return Results.StatusCode(StatusCodes.Status304NotModified);
            }
            var payload = PositionSlackPayload(
                entry.ColliderExcluding(brokenState.Groups), entry.PlayerCollider.Value, new Vector3(x, y, z), throwType, strength,
                pitch, yaw, runDeg, new Vector3(tx, ty, tz), within, entry.Constants);
            context.Response.Headers.ETag = entry.BuildETag;
            context.Response.Headers.CacheControl = "public, max-age=604800";
            return Results.Bytes(payload, JsonContentType);
        }).RequireRateLimiting(PhysicsPolicy);

        // An execute: one throw position, several smokes. "From outside B I want
        // B doors and B window" is the shape players actually plan in, and doing
        // it as separate searches loses the thing that makes it an execute -
        // that every smoke comes from the SAME spot, so they can be thrown in
        // one go without repositioning.
        //
        // Deliberately origin-scoped. A map-wide sweep per target would be
        // minutes each; from a fixed spot a solve is a couple of seconds, which
        // is what makes several of them in one request reasonable at all.
        // The question a player building an execute actually starts from:
        // "where can I stand to throw ALL of these?" /api/execute answers the
        // follow-up once you know the spot; this one finds the spot.
        //
        // Each target gets a full map-wide solve - minutes on a cold map - so
        // the results are cached exactly like a normal search, and the second
        // execute over the same site is nearly free. The solve gate is taken
        // and released per target rather than held for the whole request: an
        // execute over four targets would otherwise lock everyone else out for
        // the best part of ten minutes.
        app.MapPost("/api/execute/spots", async (HttpContext context) =>
        {
            if (maps.Count == 0)
            {
                await WriteApiError(context, StatusCodes.Status503ServiceUnavailable, "no maps extracted yet - run extract --map <name> first");
                return;
            }
            if (context.Request.ContentType is not { } ct2 ||
                !ct2.StartsWith("application/json", StringComparison.OrdinalIgnoreCase))
            {
                await WriteApiError(context, StatusCodes.Status415UnsupportedMediaType, "Content-Type must be application/json");
                return;
            }
            var buf = new byte[MaxExecuteBodyBytes + 1];
            var got = 0;
            int k;
            while ((k = await context.Request.Body.ReadAsync(buf.AsMemory(got), context.RequestAborted)) > 0)
            {
                got += k;
                if (got > MaxExecuteBodyBytes)
                {
                    await WriteApiError(context, StatusCodes.Status400BadRequest, "request body too large");
                    return;
                }
            }
            JsonDocument doc;
            try
            {
                doc = JsonDocument.Parse(buf.AsMemory(0, got));
            }
            catch (JsonException)
            {
                await WriteApiError(context, StatusCodes.Status400BadRequest, "body must be valid JSON");
                return;
            }
            using (doc)
            {
                var root2 = doc.RootElement;
                if (root2.ValueKind != JsonValueKind.Object ||
                    !root2.TryGetProperty("map", out var mEl) || mEl.ValueKind != JsonValueKind.String ||
                    !maps.TryGetValue(mEl.GetString() ?? "", out var entry) || entry.NavAreas == null)
                {
                    await WriteApiError(context, StatusCodes.Status404NotFound, UnknownMapError);
                    return;
                }
                if (!root2.TryGetProperty("targets", out var tEl) || tEl.ValueKind != JsonValueKind.Array ||
                    tEl.GetArrayLength() < 2)
                {
                    await WriteApiError(context, StatusCodes.Status400BadRequest,
                        "give at least two targets - with one, an ordinary search already answers this");
                    return;
                }
                if (tEl.GetArrayLength() > MaxExecuteSpotTargets)
                {
                    await WriteApiError(context, StatusCodes.Status400BadRequest,
                        $"at most {MaxExecuteSpotTargets} targets - each one is a full map-wide solve");
                    return;
                }
                // How far apart two throws' feet may be and still count as "the
                // same spot". A player shuffles; they do not walk across the map
                // between smokes of one execute.
                var within = root2.TryGetProperty("within", out var wEl) && wEl.ValueKind == JsonValueKind.Number
                    ? Math.Clamp(wEl.GetSingle(), 0f, 256f)
                    : DefaultSameSpot;

                var perTarget = new List<List<JsonElement>>();
                var docs = new List<JsonDocument>();
                try
                {
                    foreach (var t in tEl.EnumerateArray())
                    {
                        if (t.ValueKind != JsonValueKind.Array || t.GetArrayLength() < 2)
                        {
                            await WriteApiError(context, StatusCodes.Status400BadRequest, "each target must be [x,y] or [x,y,z]");
                            return;
                        }
                        var q = $"{{\"target\":{JsonSerializer.Serialize(t)}}}";
                        using var probe = JsonDocument.Parse(q);
                        if (ValidateLineupQuery(probe.RootElement, entry.Mesh) is { } bad)
                        {
                            await WriteApiError(context, StatusCodes.Status400BadRequest, bad);
                            return;
                        }
                        var key = QueryCacheKey(entry.Mesh, entry.BuildETag.Trim('"'), entry.Constants, probe.RootElement, attrs);
                        var path = SolveCachePath(root, key);
                        var json = await ReadSolveCacheAsync(path, context.RequestAborted);
                        if (json is null)
                        {
                            if (Interlocked.Increment(ref queuedSolves) > MaxQueuedSolves)
                            {
                                Interlocked.Decrement(ref queuedSolves);
                                await WriteApiError(context, StatusCodes.Status429TooManyRequests, "too many solves queued - try again in a moment");
                                return;
                            }
                            try
                            {
                                await SolveGate.WaitAsync(context.RequestAborted);
                            }
                            finally
                            {
                                Interlocked.Decrement(ref queuedSolves);
                            }
                            try
                            {
                                json = await Task.Run(() => RunTargetQuery(
                                    entry.Mesh, entry.AttributeFilter, entry.NavAreas!, probe.RootElement, entry.Constants,
                                    standSpots: entry.StandSpots,
                                    spawnFronts: SpawnFronts(root, entry.Mesh.MapName),
                                    spawnPoints: SpawnPoints(root, entry.Mesh.MapName)), context.RequestAborted);
                            }
                            finally
                            {
                                SolveGate.Release();
                            }
                            await WriteSolveCacheAsync(root, path, json);
                        }
                        var solved = JsonDocument.Parse(json);
                        docs.Add(solved);
                        perTarget.Add([.. solved.RootElement.GetProperty("lineups").EnumerateArray()]);
                    }

                    var spots = ExecuteSpots.Find(perTarget, within, MaxExecuteSpotResults);
                    context.Response.ContentType = JsonContentType;
                    await context.Response.WriteAsync(spots, context.RequestAborted);
                }
                catch (OperationCanceledException)
                {
                }
                catch (Exception e)
                {
                    Console.Error.WriteLine($"execute/spots failed: {e}");
                    await WriteApiError(context, StatusCodes.Status500InternalServerError, "solver failure - check server log");
                }
                finally
                {
                    foreach (var d in docs)
                    {
                        d.Dispose();
                    }
                }
            }
        }).RequireRateLimiting(SolvePolicy);

        app.MapPost("/api/execute", async (HttpContext context) =>
        {
            if (maps.Count == 0)
            {
                await WriteApiError(context, StatusCodes.Status503ServiceUnavailable, "no maps extracted yet - run extract --map <name> first");
                return;
            }
            if (context.Request.ContentType is not { } ct ||
                !ct.StartsWith("application/json", StringComparison.OrdinalIgnoreCase))
            {
                await WriteApiError(context, StatusCodes.Status415UnsupportedMediaType, "Content-Type must be application/json");
                return;
            }
            var buffer = new byte[MaxExecuteBodyBytes + 1];
            var read = 0;
            int n;
            while ((n = await context.Request.Body.ReadAsync(buffer.AsMemory(read), context.RequestAborted)) > 0)
            {
                read += n;
                if (read > MaxExecuteBodyBytes)
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
                var root = body.RootElement;
                if (root.ValueKind != JsonValueKind.Object ||
                    !root.TryGetProperty("map", out var mapEl) || mapEl.ValueKind != JsonValueKind.String ||
                    !maps.TryGetValue(mapEl.GetString() ?? "", out var entry) || entry.NavAreas == null)
                {
                    await WriteApiError(context, StatusCodes.Status404NotFound, UnknownMapError);
                    return;
                }
                if (!root.TryGetProperty("origin", out var originEl) || originEl.ValueKind != JsonValueKind.Array ||
                    originEl.GetArrayLength() < 2)
                {
                    await WriteApiError(context, StatusCodes.Status400BadRequest, "an execute needs an origin - the one spot every smoke is thrown from");
                    return;
                }
                if (!root.TryGetProperty("targets", out var targetsEl) || targetsEl.ValueKind != JsonValueKind.Array ||
                    targetsEl.GetArrayLength() == 0)
                {
                    await WriteApiError(context, StatusCodes.Status400BadRequest, "targets must be a non-empty array of [x,y] or [x,y,z]");
                    return;
                }
                if (targetsEl.GetArrayLength() > MaxExecuteTargets)
                {
                    await WriteApiError(context, StatusCodes.Status400BadRequest,
                        $"an execute holds at most {MaxExecuteTargets} smokes - that is more than a round has time for");
                    return;
                }

                // Each smoke is an ordinary origin-scoped query, so it goes
                // through exactly the same validation, solver and ranking as a
                // single search rather than a parallel implementation that can
                // drift from it.
                var originJson = JsonSerializer.Serialize(originEl);
                var shared = new List<string>();
                foreach (var key in new[] { "originReach", "tolerance", "minStability", "fineScan", "types", "strengths", "broken" })
                {
                    if (root.TryGetProperty(key, out var el))
                    {
                        shared.Add($"\"{key}\":{JsonSerializer.Serialize(el)}");
                    }
                }
                if (!root.TryGetProperty("originReach", out _))
                {
                    shared.Add($"\"originReach\":{DefaultExecuteReach.ToString(CultureInfo.InvariantCulture)}");
                }
                var sharedJson = shared.Count > 0 ? "," + string.Join(",", shared) : "";

                var queries = new List<string>();
                foreach (var t in targetsEl.EnumerateArray())
                {
                    if (t.ValueKind != JsonValueKind.Array || t.GetArrayLength() < 2)
                    {
                        await WriteApiError(context, StatusCodes.Status400BadRequest, "each target must be [x,y] or [x,y,z]");
                        return;
                    }
                    var q = $"{{\"target\":{JsonSerializer.Serialize(t)},\"origin\":{originJson}{sharedJson}}}";
                    using var probe = JsonDocument.Parse(q);
                    if (ValidateLineupQuery(probe.RootElement, entry.Mesh) is { } invalid)
                    {
                        await WriteApiError(context, StatusCodes.Status400BadRequest, invalid);
                        return;
                    }
                    queries.Add(q);
                }

                // One gate for the whole execute, not one per smoke: half a
                // finished execute is not a useful answer, and letting the
                // smokes queue separately would interleave them with other
                // people's solves and take far longer in wall time.
                if (Interlocked.Increment(ref queuedSolves) > MaxQueuedSolves)
                {
                    Interlocked.Decrement(ref queuedSolves);
                    await WriteApiError(context, StatusCodes.Status429TooManyRequests, "too many solves queued - try again in a moment");
                    return;
                }
                try
                {
                    await SolveGate.WaitAsync(context.RequestAborted);
                }
                finally
                {
                    Interlocked.Decrement(ref queuedSolves);
                }
                var smokes = new List<string>();
                try
                {
                    foreach (var q in queries)
                    {
                        context.RequestAborted.ThrowIfCancellationRequested();
                        using var doc = JsonDocument.Parse(q);
                        var solved = await Task.Run(() => RunTargetQuery(
                            entry.Mesh, entry.AttributeFilter, entry.NavAreas!, doc.RootElement, entry.Constants,
                            standSpots: entry.StandSpots), context.RequestAborted);
                        smokes.Add(TrimToBest(solved, MaxExecuteLineupsPerSmoke));
                    }
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception e)
                {
                    Console.Error.WriteLine($"execute failed: {e}");
                    await WriteApiError(context, StatusCodes.Status500InternalServerError, "solver failure - check server log");
                    return;
                }
                finally
                {
                    SolveGate.Release();
                }
                context.Response.ContentType = JsonContentType;
                await context.Response.WriteAsync(
                    $"{{\"origin\":{originJson},\"smokes\":[{string.Join(",", smokes)}]}}",
                    context.RequestAborted);
            }
        }).RequireRateLimiting(SolvePolicy);

        app.MapPost("/api/lineup", async (HttpContext context) =>
        {
            if (maps.Count == 0)
            {
                await WriteApiError(context, StatusCodes.Status503ServiceUnavailable, "no maps extracted yet - run extract --map <name> first");
                return;
            }
            // A browser may send text/plain, multipart, or form-encoded across
            // origins without asking permission first; application/json is not
            // on that list, so requiring it means a cross-origin caller has to
            // pass a preflight this server never answers. Without the check any
            // page open in a visitor's browser can start solves here.
            if (context.Request.ContentType is not { } contentType ||
                !contentType.StartsWith("application/json", StringComparison.OrdinalIgnoreCase))
            {
                await WriteApiError(context, StatusCodes.Status415UnsupportedMediaType, "Content-Type must be application/json");
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
                var cacheKey = QueryCacheKey(mesh, entry.BuildETag.Trim('"'), serveConstants, body.RootElement, attrs);
                // Rooted like every other data path: a relative "data" here would
                // split the cache from the directory PruneCache sweeps whenever
                // --root is not the working directory.
                var cachePath = SolveCachePath(root, cacheKey);

                // Progress streams as NDJSON so the viewer can paint each evaluated
                // origin live: phase lines, then batches of checked [x, y, z, hits]
                // cells, then a final result line. Z is carried so the 3D view can
                // stand each dot on the ground it belongs to rather than flattening
                // the sweep onto one plane. Cache files keep the bare result
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
                    catch (Exception e)
                    {
                        // The solve keeps running so its result still lands in the
                        // cache; a reload after cancel then answers instantly.
                        clientGone = true;
                        // A disconnect is the expected case and says nothing worth
                        // logging. Anything else - a serialization fault, a full
                        // disk - would otherwise be silently filed as "they left"
                        // and leave a truncated stream with no trace at all.
                        if (e is not (OperationCanceledException or IOException or ObjectDisposedException))
                        {
                            Console.Error.WriteLine($"lineup stream write failed: {e}");
                        }
                    }
                }

                if (await ReadSolveCacheAsync(cachePath, context.RequestAborted) is { } cached)
                {
                    await WriteLine("{\"result\":" + cached + "}");
                    return;
                }

                // Nothing has been written yet on this path (the cache hit above
                // returns before here), so a refusal can still travel as a real
                // status code rather than in-band.
                if (Interlocked.Increment(ref queuedSolves) > MaxQueuedSolves)
                {
                    Interlocked.Decrement(ref queuedSolves);
                    await WriteApiError(context, StatusCodes.Status429TooManyRequests,
                        "too many solves queued - try again in a moment");
                    return;
                }
                try
                {
                    await SolveGate.WaitAsync(context.RequestAborted);
                }
                finally
                {
                    Interlocked.Decrement(ref queuedSolves);
                }
                try
                {
                    // A double-submit of the same query (two tabs, a re-click)
                    // may have solved and cached while this request waited at
                    // the gate; answering from that file skips a redundant solve.
                    if (await ReadSolveCacheAsync(cachePath, context.RequestAborted) is { } raced)
                    {
                        await WriteLine("{\"result\":" + raced + "}");
                        return;
                    }
                    var events = new System.Collections.Concurrent.ConcurrentQueue<(string Kind, int[] Data)>();
                    var solveTask = Task.Run(() => RunTargetQuery(
                        mesh, attributeFilter, navAreas, body.RootElement, serveConstants,
                        onPhase: (phase, count) => events.Enqueue((phase, [count])),
                        onOrigin: (feet, hits) => events.Enqueue(("origin", [(int)MathF.Round(feet.X), (int)MathF.Round(feet.Y), (int)MathF.Round(feet.Z), hits])),
                        onCandidate: (feet, ok) => events.Enqueue(("cand", [(int)MathF.Round(feet.X), (int)MathF.Round(feet.Y), (int)MathF.Round(feet.Z), ok ? 1 : 0])),
                        standSpots: entry.StandSpots,
                        spawnFronts: SpawnFronts(root, entry.Mesh.MapName),
                        spawnPoints: SpawnPoints(root, entry.Mesh.MapName)));
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
                    await WriteSolveCacheAsync(root, cachePath, response);
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
        }).RequireRateLimiting(SolvePolicy);

        app.MapGet("/", (HttpContext context) => ServeStatic(context, root, "viewer/index.html"));
        app.MapGet("/viewer/{**rest}", (HttpContext context, string? rest) => ServeStatic(context, root, "viewer/" + (rest ?? "")));
        // GET and HEAD: a client that only needs to know whether a large data
        // file exists (the multi-megabyte mesh diff) should not have to
        // download it to find out.
        app.MapMethods("/data/{**rest}", ["GET", "HEAD"], (HttpContext context, string? rest) =>
        {
            var r = rest ?? "";
            // The viewer only ever fetches map JSON/PNG/GLB and the validation
            // reports. Everything else under data/ is a dev artifact - the
            // calibration tree, the mesh cache, run logs (which can carry local
            // paths), OBJ dumps - and has no business at a public URL.
            if (r.EndsWith(".log", StringComparison.OrdinalIgnoreCase) ||
                r.EndsWith(".obj", StringComparison.OrdinalIgnoreCase) ||
                r.StartsWith("calib/", StringComparison.OrdinalIgnoreCase) ||
                r.StartsWith("cache/", StringComparison.OrdinalIgnoreCase))
            {
                return Results.NotFound();
            }
            return ServeStatic(context, root, "data/" + r);
        });

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

    // The world-state toggle on the GET endpoints, mirroring the lineup body's
    // "broken" array: csv tokens from {glass, doors} mapped to the collision
    // groups extraction gave those entities, sorted so every spelling of the
    // same state shares one collider cache entry.
    static (List<string> Groups, string? Error) ParseBroken(string? broken)
    {
        var groups = new List<string>();
        if (string.IsNullOrEmpty(broken))
        {
            return (groups, null);
        }
        foreach (var token in broken.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (token is not ("glass" or "doors"))
            {
                return (groups, "broken must be a comma list drawn from glass, doors");
            }
            var group = token == "glass" ? "EntityBreakable" : "EntityDoor";
            if (!groups.Contains(group))
            {
                groups.Add(group);
            }
        }
        groups.Sort(StringComparer.Ordinal);
        return (groups, null);
    }

    // Every API error is the same {"error": "..."} object, whichever endpoint
    // produced it - clients used to have to special-case three body shapes.
    static IResult ApiError(int status, string message) =>
        Results.Json(new { error = message }, statusCode: status);

    static Task WriteApiError(HttpContext context, int status, string message)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = JsonContentType;
        return context.Response.WriteAsync(JsonSerializer.Serialize(new { error = message }));
    }
}

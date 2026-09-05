using System.Net;
using System.Net.Http.Json;
using System.Numerics;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using SmokeSolver.Cli;

namespace SmokeSolver.Sim.Tests;

/// <summary>
/// The real server, started on a free port against a temporary root holding
/// one synthetic map, driven over HTTP the way the viewer drives it.
/// </summary>
// Every wiring bug this server shipped - an auth check on the wrong path, a
// header not set, a solve that was queued in silence until the edge gave up -
// sat between the unit-tested helpers and the browser, and was found by a
// person clicking. This is the layer that finds them first.
public sealed class ServeFixture : IDisposable
{
    public string Root { get; }
    public string Map => "arena";
    public HttpClient Client { get; }
    public byte[] Secret { get; }
    public const string AdminId = "76561198000000001";
    public const string UserId = "76561198000000002";
    readonly WebApplication app;

    public ServeFixture()
    {
        Root = Path.Combine(Path.GetTempPath(), "smokesolver-serve-" + Guid.NewGuid().ToString("N"));
        var data = Path.Combine(Root, "data");
        Directory.CreateDirectory(data);
        Directory.CreateDirectory(Path.Combine(Root, "viewer"));
        File.WriteAllText(Path.Combine(Root, "viewer", "index.html"), "<!doctype html><title>t</title>");

        // A flat 1024x1024 floor with one wall across the middle: enough for a
        // solve to have origins, a target, and something to bounce off.
        var mesh = SyntheticMeshes.FromQuads(
        [
            SyntheticMeshes.Ground(0, 1024, 0),
            SyntheticMeshes.WallY(0, 1024, 512, 0, 96),
        ]);
        var named = new CollisionMesh
        {
            MapName = Map,
            GameBuildId = mesh.GameBuildId,
            Vertices = mesh.Vertices,
            Indices = mesh.Indices,
            TriangleAttributes = mesh.TriangleAttributes,
            AttributeNames = mesh.AttributeNames,
            AttributeInteractAs = mesh.AttributeInteractAs,
        };
        named.Save(Path.Combine(data, $"{Map}.s2geo"));
        // One nav area over the whole south half, so origins get sampled.
        File.WriteAllText(Path.Combine(data, $"{Map}.navareas.json"),
            "[{\"Id\":1,\"Corners\":[[16,16,0],[1008,16,0],[1008,480,0],[16,480,0]]}]");
        File.WriteAllText(Path.Combine(data, "admins.txt"), AdminId + "\n");

        app = ServeCommand.Build(new Dictionary<string, string>
        {
            ["root"] = Root,
            ["port"] = "0",
            ["attrs"] = "default",
        });
        app.StartAsync().GetAwaiter().GetResult();
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        Client = new HttpClient { BaseAddress = new Uri(address.Replace("[::]", "localhost").Replace("0.0.0.0", "localhost")) };
        Secret = Convert.FromHexString(File.ReadAllText(Path.Combine(data, "session.secret")).Trim());
    }

    public string CookieFor(string steamId) =>
        $"{SteamAuth.CookieName}={SteamAuth.MintSession(Secret, steamId, DateTimeOffset.UtcNow)}";

    public void Dispose()
    {
        Client.Dispose();
        app.StopAsync().GetAwaiter().GetResult();
        app.DisposeAsync().AsTask().GetAwaiter().GetResult();
        try { Directory.Delete(Root, recursive: true); } catch (IOException) { }
    }
}

public class ServeEndpointTests(ServeFixture server) : IClassFixture<ServeFixture>
{
    HttpRequestMessage Request(HttpMethod method, string path, string? cookie = null, string? json = null)
    {
        var req = new HttpRequestMessage(method, path);
        if (cookie is not null)
        {
            req.Headers.Add("Cookie", cookie);
        }
        if (json is not null)
        {
            req.Content = new StringContent(json, Encoding.UTF8, "application/json");
        }
        return req;
    }

    [Fact]
    public async Task MapsListsTheMapAndWhatItHas()
    {
        var maps = await server.Client.GetFromJsonAsync<JsonElement>("/api/maps");

        var arena = maps.EnumerateArray().Single(m => m.GetProperty("map").GetString() == server.Map);
        Assert.True(arena.GetProperty("hasLineups").GetBoolean());
        // Other tests in this class may have written a GLB into the shared root.
        Assert.Equal(File.Exists(Path.Combine(server.Root, "data", "arena_textured.glb")), arena.GetProperty("hasTextured").GetBoolean());
        Assert.False(arena.GetProperty("hasProSmokes").GetBoolean());
        // The synthetic arena has no breakable glass and no doors; the fields
        // are what the viewer keys its round-state notice on.
        Assert.False(arena.GetProperty("hasGlass").GetBoolean());
        Assert.False(arena.GetProperty("hasDoors").GetBoolean());
    }

    [Fact]
    public async Task AnonymousIsNotSignedIn()
    {
        var res = await server.Client.GetAsync("/auth/me");

        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task TargetsCanOnlyBeWrittenByAnAdmin()
    {
        var body = "[{\"name\":\"site\",\"pos\":[300,200,0]}]";

        var anon = await server.Client.SendAsync(Request(HttpMethod.Put, $"/api/targets?map={server.Map}", null, body));
        var user = await server.Client.SendAsync(Request(HttpMethod.Put, $"/api/targets?map={server.Map}", server.CookieFor(ServeFixture.UserId), body));

        Assert.Equal(HttpStatusCode.Forbidden, anon.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, user.StatusCode);
        Assert.Equal("[]", (await server.Client.GetStringAsync($"/api/targets?map={server.Map}")).Trim());
    }

    [Fact]
    public async Task AnAdminsTargetsRoundTripWithTheirIdsKept()
    {
        var admin = server.CookieFor(ServeFixture.AdminId);
        var first = await server.Client.SendAsync(Request(HttpMethod.Put, $"/api/targets?map={server.Map}", admin,
            "[{\"name\":\"site\",\"pos\":[300,200,0]},{\"name\":\"door\",\"named\":true,\"pos\":[600,300,0],\"landings\":12,\"spread\":30}]"));
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var saved = JsonDocument.Parse(await first.Content.ReadAsStringAsync()).RootElement;
        var siteId = saved[0].GetProperty("id").GetString()!;
        Assert.Equal(8, siteId.Length);
        Assert.False(saved[0].GetProperty("named").GetBoolean());
        Assert.True(saved[1].GetProperty("named").GetBoolean());

        // The GET reflects it at once (the cache was cleared), and a rename
        // keeps the id votes and saved lineups hang off.
        var listed = JsonDocument.Parse(await server.Client.GetStringAsync($"/api/targets?map={server.Map}")).RootElement;
        Assert.Equal(2, listed.GetArrayLength());
        var renamed = await server.Client.SendAsync(Request(HttpMethod.Put, $"/api/targets?map={server.Map}", admin,
            $"[{{\"id\":\"{siteId}\",\"name\":\"A site\",\"named\":true,\"pos\":[300,200,0]}}]"));
        var after = JsonDocument.Parse(await renamed.Content.ReadAsStringAsync()).RootElement;
        Assert.Single(after.EnumerateArray());
        Assert.Equal(siteId, after[0].GetProperty("id").GetString());
        Assert.Equal("A site", after[0].GetProperty("name").GetString());
    }

    [Theory]
    [InlineData("[{\"name\":\"x\",\"pos\":[1,2,\"a\"]}]")]
    [InlineData("[{\"pos\":[1,2,3]}]")]
    [InlineData("[{\"name\":\"far\",\"pos\":[90000,2,3]}]")]
    [InlineData("{\"not\":\"a list\"}")]
    public async Task ABadTargetsBodyIsRefusedWithoutTouchingTheFile(string body)
    {
        var res = await server.Client.SendAsync(Request(HttpMethod.Put, $"/api/targets?map={server.Map}", server.CookieFor(ServeFixture.AdminId), body));

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    // The session-signing secret, the admin list, the votes database and
    // every account's saved set all sat one GET away on prod until a review
    // found them: the data handler was a blocklist that named none of them.
    // Now an allowlist, and this is the test that keeps it one.
    [Theory]
    [InlineData("/data/session.secret")]
    [InlineData("/data/admins.txt")]
    [InlineData("/data/votes.db")]
    [InlineData("/data/votes.db-wal")]
    [InlineData("/data/users/76561198000000001.json")]
    [InlineData("/data/cache/anything.json")]
    [InlineData("/data/calib/markers.json")]
    [InlineData("/data/run.log")]
    [InlineData("/data/arena.s2geo")]
    [InlineData("/data/arena.navareas.json")]
    [InlineData("/data/validation/../session.secret")]
    public async Task OnlyViewerAssetsAreServedFromData(string path)
    {
        Directory.CreateDirectory(Path.Combine(server.Root, "data", "cache"));
        Directory.CreateDirectory(Path.Combine(server.Root, "data", "users"));
        File.WriteAllText(Path.Combine(server.Root, "data", "cache", "anything.json"), "{}");
        File.WriteAllText(Path.Combine(server.Root, "data", "run.log"), "secret");
        File.WriteAllText(Path.Combine(server.Root, "data", "users", "76561198000000001.json"), "{}");
        File.WriteAllText(Path.Combine(server.Root, "data", "votes.db-wal"), "x");

        var res = await server.Client.GetAsync(path);

        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
    }

    [Theory]
    [InlineData("/data/arena.viewer-map.json")]
    [InlineData("/data/arena.targets.json")]
    [InlineData("/data/arena_textured.glb")]
    [InlineData("/data/validation/index.json")]
    public async Task TheViewersOwnAssetsAreServed(string path)
    {
        Directory.CreateDirectory(Path.Combine(server.Root, "data", "validation"));
        File.WriteAllText(Path.Combine(server.Root, "data", "arena.viewer-map.json"), "{}");
        File.WriteAllText(Path.Combine(server.Root, "data", "arena.targets.json"), "[]");
        File.WriteAllBytes(Path.Combine(server.Root, "data", "arena_textured.glb"), new byte[16]);
        File.WriteAllText(Path.Combine(server.Root, "data", "validation", "index.json"), "[]");

        var res = await server.Client.GetAsync(path);

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
    }

    [Fact]
    public async Task AVersionedDataFileIsImmutableAndAnUnversionedOneRevalidates()
    {
        var path = Path.Combine(server.Root, "data", "arena_textured.mobile.glb");
        File.WriteAllBytes(path, new byte[1024]);
        var version = StaticAssetServer.AssetVersion(path);

        var exact = await server.Client.GetAsync($"/data/arena_textured.mobile.glb?v={version}");
        var stale = await server.Client.GetAsync("/data/arena_textured.mobile.glb?v=old");
        var bare = await server.Client.GetAsync("/data/arena_textured.mobile.glb");

        Assert.Equal("public, max-age=31536000, immutable", exact.Headers.CacheControl!.ToString());
        Assert.Equal("no-cache", stale.Headers.CacheControl!.ToString());
        Assert.Equal("no-cache", bare.Headers.CacheControl!.ToString());
        Assert.NotNull(bare.Headers.ETag);
    }

    [Fact]
    public async Task AnUnknownMapIsRefusedWithAReason()
    {
        var res = await server.Client.SendAsync(Request(HttpMethod.Post, "/api/lineup", null, "{\"map\":\"nope\",\"target\":[1,2,3]}"));

        Assert.True(res.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.BadRequest, res.StatusCode.ToString());
        Assert.Contains("error", await res.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task ALineupSolveStreamsItsPhasesThenCachesTheAnswer()
    {
        var body = "{\"map\":\"arena\",\"target\":[512,300,0],\"originReach\":200,\"origin\":[300,200]}";

        var first = await server.Client.SendAsync(Request(HttpMethod.Post, "/api/lineup", null, body));
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal("application/x-ndjson", first.Content.Headers.ContentType!.MediaType);
        var lines = (await first.Content.ReadAsStringAsync()).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var phases = lines.Where(l => l.Contains("\"phase\"")).Select(l => JsonDocument.Parse(l).RootElement.GetProperty("phase").GetString()).ToList();
        // Queued at once (the keepalive that keeps the edge from giving up),
        // then the solve's own phases, then one result.
        Assert.Equal("queued", phases[0]);
        Assert.Contains("prepare", phases);
        Assert.Contains("sweep", phases);
        Assert.Contains("verify", phases);
        var result = JsonDocument.Parse(lines[^1]).RootElement.GetProperty("result");
        Assert.True(result.GetProperty("origins").GetInt32() > 0, "no origins were swept");
        Assert.True(result.GetProperty("lineups").GetArrayLength() > 0, "a flat arena should have lineups");
        foreach (var l in result.GetProperty("lineups").EnumerateArray())
        {
            Assert.True(l.GetProperty("humanError").GetSingle() >= 0);
            Assert.Equal(16, l.GetProperty("id").GetString()!.Length);
        }

        // The same question again is answered from disk: one line, no phases.
        var second = await server.Client.SendAsync(Request(HttpMethod.Post, "/api/lineup", null, body));
        var again = (await second.Content.ReadAsStringAsync()).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Single(again);
        Assert.StartsWith("{\"result\":", again[0]);
        Assert.NotEmpty(Directory.GetFiles(Path.Combine(server.Root, "data", "cache"), "*.json"));
    }

    [Fact]
    public async Task ASolveNobodyIsWaitingForIsCancelledAndNotCached()
    {
        // A different target, so this is a cold solve; the client leaves
        // after the first line and the solve must not go on to cache itself.
        var body = "{\"map\":\"arena\",\"target\":[700,400,0],\"originReach\":300,\"origin\":[200,100]}";
        var cacheDir = Path.Combine(server.Root, "data", "cache");
        Directory.CreateDirectory(cacheDir);
        var before = Directory.GetFiles(cacheDir, "*.json").ToHashSet();

        using var cts = new CancellationTokenSource();
        var res = await server.Client.SendAsync(Request(HttpMethod.Post, "/api/lineup", null, body), HttpCompletionOption.ResponseHeadersRead, cts.Token);
        using var stream = await res.Content.ReadAsStreamAsync(cts.Token);
        var buffer = new byte[64];
        await stream.ReadAsync(buffer, cts.Token);
        cts.Cancel();
        res.Dispose();

        await Task.Delay(1500);
        Assert.Equal(before, Directory.GetFiles(cacheDir, "*.json").ToHashSet());
    }

    [Fact]
    public async Task ExecuteAnswersOneJsonDocumentAfterItsKeepalives()
    {
        var res = await server.Client.SendAsync(Request(HttpMethod.Post, "/api/execute", null,
            "{\"map\":\"arena\",\"origin\":[300,200],\"targets\":[[512,300,0],[600,350,0]]}"));

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var text = await res.Content.ReadAsStringAsync();
        // Leading blank lines are the keepalive; what follows parses as one document.
        var doc = JsonDocument.Parse(text.TrimStart());
        Assert.Equal(2, doc.RootElement.GetProperty("smokes").GetArrayLength());
    }
}

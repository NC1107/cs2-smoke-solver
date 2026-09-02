using Microsoft.AspNetCore.Http;
using SmokeSolver.Cli;
using static SmokeSolver.Cli.ServeCommand;

namespace SmokeSolver.Sim.Tests;

/// <summary>
/// Which client a rate-limit bucket belongs to.
/// </summary>
// This is security-critical and got it wrong once. The first version keyed on
// X-Forwarded-For's first entry, which Cloudflare and traefik APPEND to rather
// than replace - so a client could send its own X-Forwarded-For, land at the
// front of the chain, and mint a fresh bucket per request. Measured against
// production: after the real bucket was exhausted to 429, two forged values
// both went straight through.
public class ClientKeyTests
{
    static HttpContext Request(params (string Header, string Value)[] headers)
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = System.Net.IPAddress.Parse("10.0.0.7");
        foreach (var (header, value) in headers)
        {
            context.Request.Headers[header] = value;
        }
        return context;
    }

    [Fact]
    public void AClientSuppliedForwardedForCannotChooseItsOwnBucket()
    {
        var forged = ClientKey(Request(("X-Forwarded-For", "203.0.113.77")));
        var alsoForged = ClientKey(Request(("X-Forwarded-For", "198.51.100.9")));

        Assert.Equal(forged, alsoForged);
        Assert.DoesNotContain("203.0.113.77", forged);
        Assert.DoesNotContain("198.51.100.9", alsoForged);
    }

    [Fact]
    public void CloudflareConnectingIpIdentifiesTheClient()
    {
        Assert.Equal("203.0.113.5", ClientKey(Request(("CF-Connecting-IP", "203.0.113.5"))));
        Assert.NotEqual(
            ClientKey(Request(("CF-Connecting-IP", "203.0.113.5"))),
            ClientKey(Request(("CF-Connecting-IP", "203.0.113.6"))));
    }

    [Fact]
    public void ForwardedForIsIgnoredEvenAlongsideCloudflaresHeader()
    {
        var key = ClientKey(Request(
            ("X-Forwarded-For", "203.0.113.77"),
            ("CF-Connecting-IP", "198.51.100.4")));

        Assert.Equal("198.51.100.4", key);
    }

    [Fact]
    public void WithNoProxyHeadersTheSocketAddressIsUsed() =>
        // Over-limits (everyone behind one proxy shares a bucket) rather than
        // under-limits, which is the right way round for a fallback.
        Assert.Equal("10.0.0.7", ClientKey(Request()));
}

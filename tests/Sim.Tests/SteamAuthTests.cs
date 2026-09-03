using SmokeSolver.Cli;

namespace SmokeSolver.Sim.Tests;

/// <summary>
/// Sign in with Steam: the two checks that, skipped, let anyone log in as
/// anyone, and the session cookie that follows.
/// </summary>
// There is a published fake Steam OpenID provider built specifically to forge
// logins against implementations that either skip the check_authentication
// round trip or accept a claimed identity that is not exactly Steam's URL
// shape. These tests are the guard against shipping either mistake.
public class SteamAuthTests
{
    const string SteamId = "76561198012345678";
    const string ReturnTo = "https://smoke.example/auth/steam/callback";

    static Dictionary<string, string> Callback(string claimedId = $"https://steamcommunity.com/openid/id/{SteamId}") => new()
    {
        ["openid.ns"] = "http://specs.openid.net/auth/2.0",
        ["openid.mode"] = "id_res",
        ["openid.op_endpoint"] = "https://steamcommunity.com/openid/login",
        ["openid.claimed_id"] = claimedId,
        ["openid.identity"] = claimedId,
        ["openid.return_to"] = ReturnTo,
        ["openid.response_nonce"] = "2026-09-03T10:00:00Zabc",
        ["openid.assoc_handle"] = "1234567890",
        ["openid.signed"] = "signed,op_endpoint,claimed_id,identity,return_to,response_nonce,assoc_handle",
        ["openid.sig"] = "dGVzdA==",
    };

    static Task<string> SteamSays(string reply) => Task.FromResult(reply);

    [Fact]
    public async Task ACallbackSteamVouchesForSignsIn()
    {
        var id = await SteamAuth.VerifyCallbackAsync(Callback(), ReturnTo, _ => SteamSays("ns:http://specs.openid.net/auth/2.0\nis_valid:true\n"));

        Assert.Equal(SteamId, id);
    }

    [Fact]
    public async Task ACallbackSteamRejectsIsRefusedEvenThoughItLooksPerfect()
    {
        // The redirect carries no secret the browser cannot replay. Only
        // Steam's answer to the round trip means anything.
        var id = await SteamAuth.VerifyCallbackAsync(Callback(), ReturnTo, _ => SteamSays("ns:http://specs.openid.net/auth/2.0\nis_valid:false\n"));

        Assert.Null(id);
    }

    [Fact]
    public async Task TheRoundTripSendsSteamsOwnParametersWithOnlyTheModeChanged()
    {
        // The signature covers exactly what Steam sent; adding, dropping or
        // altering a parameter fails it. So the verify request must be that
        // set, with mode flipped to check_authentication and nothing else.
        IReadOnlyDictionary<string, string>? sent = null;
        await SteamAuth.VerifyCallbackAsync(Callback(), ReturnTo, form => { sent = form; return SteamSays("is_valid:true"); });

        Assert.NotNull(sent);
        Assert.Equal("check_authentication", sent!["openid.mode"]);
        Assert.Equal("dGVzdA==", sent["openid.sig"]);
        Assert.Equal(Callback().Count, sent.Count);
    }

    [Theory]
    [InlineData("https://steamcommunity.com/openid/id/7656119801234567")]   // 16 digits
    [InlineData("https://steamcommunity.com/openid/id/765611980123456789")] // 18 digits
    [InlineData("http://steamcommunity.com/openid/id/76561198012345678")]   // not https
    [InlineData("https://steamcommunity.com.evil.example/openid/id/76561198012345678")]
    [InlineData("https://evil.example/?u=https://steamcommunity.com/openid/id/76561198012345678")]
    [InlineData("https://steamcommunity.com/openid/id/76561198012345678/../1")]
    [InlineData("STEAM_0:1:12345")]
    public async Task AClaimedIdentityThatIsNotExactlySteamsShapeIsRefused(string claimedId)
    {
        // Even with Steam saying yes: the identity is attacker-supplied text
        // until it matches the one URL shape Steam issues.
        var id = await SteamAuth.VerifyCallbackAsync(Callback(claimedId), ReturnTo, _ => SteamSays("is_valid:true"));

        Assert.Null(id);
    }

    [Fact]
    public async Task AnAssertionForSomeOtherSiteIsRefused()
    {
        var cb = Callback();
        cb["openid.return_to"] = "https://other.example/auth/steam/callback";

        var id = await SteamAuth.VerifyCallbackAsync(cb, ReturnTo, _ => SteamSays("is_valid:true"));

        Assert.Null(id);
    }

    // ---- session cookie ----

    static readonly byte[] Secret = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();
    static readonly DateTimeOffset Now = new(2026, 9, 3, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void AMintedSessionReadsBackTheSameAccount()
    {
        var cookie = SteamAuth.MintSession(Secret, SteamId, Now);

        Assert.Equal(SteamId, SteamAuth.ReadSession(Secret, cookie, Now.AddDays(1)));
    }

    [Fact]
    public void ATamperedSessionIsRefused()
    {
        var cookie = SteamAuth.MintSession(Secret, SteamId, Now);
        var other = cookie.Replace(SteamId, "76561198000000001");

        Assert.Null(SteamAuth.ReadSession(Secret, other, Now));
    }

    [Fact]
    public void ASessionSignedWithADifferentSecretIsRefused()
    {
        var cookie = SteamAuth.MintSession(Secret, SteamId, Now);
        var otherSecret = Secret.Select(b => (byte)(b ^ 0xff)).ToArray();

        Assert.Null(SteamAuth.ReadSession(otherSecret, cookie, Now));
    }

    [Fact]
    public void AnExpiredSessionIsRefused()
    {
        var cookie = SteamAuth.MintSession(Secret, SteamId, Now);

        Assert.Null(SteamAuth.ReadSession(Secret, cookie, Now.Add(SteamAuth.SessionLength).AddSeconds(1)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("garbage")]
    [InlineData("a.b")]
    [InlineData("a.b.c.d")]
    public void MalformedCookiesAreRefused(string? cookie)
    {
        Assert.Null(SteamAuth.ReadSession(Secret, cookie, Now));
    }

    [Fact]
    public void TheSecretIsCreatedOnceAndReusedAfter()
    {
        var root = Path.Combine(Path.GetTempPath(), "smokesolver-auth-" + Guid.NewGuid().ToString("N"));
        try
        {
            var first = SteamAuth.LoadOrCreateSecret(root);
            var second = SteamAuth.LoadOrCreateSecret(root);

            Assert.Equal(32, first.Length);
            Assert.Equal(first, second);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }
}

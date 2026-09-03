using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace SmokeSolver.Cli;

/// <summary>
/// Sign in with Steam, by hand: OpenID 2.0 redirect, the verification round
/// trip, and an HMAC-signed session cookie.
/// </summary>
// Every CS2 player already has the one account that fits, and Steam's OpenID
// needs no client secret and no app registration - which is also why it is
// hand-rolled rather than pulled in: the maintained package wants the whole
// ASP.NET authentication pipeline plus a data-protection key ring that must be
// persisted or every restart logs everyone out. This is ~150 lines that fit
// the codebase, isolated behind one verification function so that if Valve
// ever replaces the protocol only this file changes.
//
// Two checks are not optional, and there is a published tool that forges a
// login against any implementation that skips either. The callback's
// parameters must be POSTed back to Steam with openid.mode=check_authentication
// and the reply must say is_valid:true - the redirect carries no secret the
// browser cannot replay. And the claimed identity must match the exact Steam
// URL shape, because anything else is attacker-supplied text.
public static partial class SteamAuth
{
    const string SteamOpenId = "https://steamcommunity.com/openid/login";
    const string OpenIdNs = "http://specs.openid.net/auth/2.0";
    const string IdentifierSelect = "http://specs.openid.net/auth/2.0/identifier_select";

    public const string CookieName = "smoke.session";
    public static readonly TimeSpan SessionLength = TimeSpan.FromDays(30);

    [GeneratedRegex(@"^https://steamcommunity\.com/openid/id/(\d{17})$")]
    private static partial Regex ClaimedIdPattern();

    /// <summary>Where to send the browser to sign in.</summary>
    public static string LoginUrl(string publicOrigin)
    {
        var q = new Dictionary<string, string>
        {
            ["openid.ns"] = OpenIdNs,
            ["openid.mode"] = "checkid_setup",
            ["openid.identity"] = IdentifierSelect,
            ["openid.claimed_id"] = IdentifierSelect,
            ["openid.return_to"] = publicOrigin + "/auth/steam/callback",
            ["openid.realm"] = publicOrigin,
        };
        return SteamOpenId + "?" + string.Join("&", q.Select(kv => $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));
    }

    /// <summary>
    /// The 17-digit SteamID64 from a callback's claimed identity, or null if
    /// the value is not exactly Steam's URL shape.
    /// </summary>
    public static string? SteamIdFromClaimedId(string? claimedId) =>
        claimedId is not null && ClaimedIdPattern().Match(claimedId) is { Success: true } m ? m.Groups[1].Value : null;

    /// <summary>
    /// Verifies a callback with Steam and returns the SteamID64, or null when
    /// Steam does not vouch for it. The POST goes through <paramref name="post"/>
    /// so a test can stand in for Steam.
    /// </summary>
    public static async Task<string?> VerifyCallbackAsync(
        IReadOnlyDictionary<string, string> query,
        string expectedReturnTo,
        Func<IReadOnlyDictionary<string, string>, Task<string>> post)
    {
        if (!query.TryGetValue("openid.mode", out var mode) || mode != "id_res")
        {
            return null;
        }
        // The return_to Steam signed must be ours, or a valid assertion for
        // some other site could be replayed here.
        if (!query.TryGetValue("openid.return_to", out var returnTo) ||
            !returnTo.StartsWith(expectedReturnTo, StringComparison.Ordinal))
        {
            return null;
        }
        var steamId = SteamIdFromClaimedId(query.GetValueOrDefault("openid.claimed_id"));
        if (steamId is null)
        {
            return null;
        }
        // Exactly the parameters Steam sent, with only the mode changed: the
        // signature covers them, so anything added or dropped fails.
        var verify = query
            .Where(kv => kv.Key.StartsWith("openid.", StringComparison.Ordinal))
            .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
        verify["openid.mode"] = "check_authentication";
        var reply = await post(verify);
        var valid = reply.Split('\n')
            .Select(l => l.Trim())
            .Any(l => l.Equals("is_valid:true", StringComparison.Ordinal));
        return valid ? steamId : null;
    }

    /// <summary>The real POST to Steam.</summary>
    public static async Task<string> PostToSteamAsync(HttpClient http, IReadOnlyDictionary<string, string> form)
    {
        using var content = new FormUrlEncodedContent(form);
        using var response = await http.PostAsync(SteamOpenId, content);
        return await response.Content.ReadAsStringAsync();
    }

    // ---- session cookie: steamid.expiry.hmac ----

    public static string MintSession(byte[] secret, string steamId, DateTimeOffset now)
    {
        var expires = now.Add(SessionLength).ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        var payload = $"{steamId}.{expires}";
        return payload + "." + Sign(secret, payload);
    }

    /// <summary>The SteamID64 a session cookie vouches for, or null.</summary>
    public static string? ReadSession(byte[] secret, string? cookie, DateTimeOffset now)
    {
        if (string.IsNullOrEmpty(cookie))
        {
            return null;
        }
        var parts = cookie.Split('.');
        if (parts.Length != 3)
        {
            return null;
        }
        var payload = parts[0] + "." + parts[1];
        var expected = Sign(secret, payload);
        // Constant-time: a byte-by-byte compare leaks how much of a forged
        // signature was right.
        if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(expected), Encoding.ASCII.GetBytes(parts[2])))
        {
            return null;
        }
        if (!long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var expires) ||
            DateTimeOffset.FromUnixTimeSeconds(expires) <= now)
        {
            return null;
        }
        return parts[0].Length == 17 && parts[0].All(char.IsAsciiDigit) ? parts[0] : null;
    }

    static string Sign(byte[] secret, string payload) =>
        Convert.ToHexString(HMACSHA256.HashData(secret, Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();

    /// <summary>
    /// The signing secret, created on first run and kept beside the data so it
    /// survives restarts and redeploys. Baked into the image it would rotate
    /// on every deploy and log everyone out.
    /// </summary>
    public static byte[] LoadOrCreateSecret(string root)
    {
        var path = Path.Combine(root, "data", "session.secret");
        if (File.Exists(path))
        {
            var existing = Convert.FromHexString(File.ReadAllText(path).Trim());
            if (existing.Length >= 32)
            {
                return existing;
            }
        }
        var secret = RandomNumberGenerator.GetBytes(32);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        File.WriteAllText(temp, Convert.ToHexString(secret));
        // Owner-only. Anyone who can read this file can mint a session for any
        // account, and the data directory is shared with backups and the
        // occasional shell on the host.
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(temp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        File.Move(temp, path, overwrite: true);
        return secret;
    }
}

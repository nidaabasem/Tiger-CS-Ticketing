extern alias TigerCsWeb;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using TigerCsWeb::TigerCS.Web.Services.Embedding;

namespace TigerCS.Tests.Web;

/// <summary>
/// Genesys Cloud embedding: framing is allowed only from exact, approved
/// https origins; with nothing configured the site stays unframeable; and the
/// session cookies are switched to what a cross-site frame requires.
/// </summary>
public sealed class EmbeddingPolicyTests
{
    private const string Genesys = "https://apps.mypurecloud.de";

    [Fact]
    public void NothingConfigured_DeniesAllFraming_AndKeepsStrictCookies()
    {
        var policy = EmbeddingPolicy.Create(null);

        Assert.False(policy.IsEmbeddingEnabled);
        Assert.Equal("'none'", policy.FrameAncestors);
        Assert.Equal("DENY", policy.XFrameOptions);
        Assert.Equal(SameSiteMode.Strict, policy.CookieSameSite);
        Assert.Equal(CookieSecurePolicy.SameAsRequest, policy.CookieSecurePolicy);
    }

    [Fact]
    public void GenesysOrigin_IsTheOnlyAncestor_AndOmitsXFrameOptions()
    {
        var policy = EmbeddingPolicy.Create([Genesys]);

        Assert.True(policy.IsEmbeddingEnabled);
        Assert.Equal(Genesys, policy.FrameAncestors);
        Assert.DoesNotContain("*", policy.FrameAncestors);
        Assert.DoesNotContain("'self'", policy.FrameAncestors);
        Assert.Null(policy.XFrameOptions);
    }

    [Fact]
    public void Embedding_UsesSameSiteNone_WithAlwaysSecureCookies()
    {
        var policy = EmbeddingPolicy.Create([Genesys]);

        Assert.Equal(SameSiteMode.None, policy.CookieSameSite);
        Assert.Equal(CookieSecurePolicy.Always, policy.CookieSecurePolicy);
    }

    [Fact]
    public void Origins_AreNormalised_Deduplicated_AndBlanksIgnored()
    {
        var policy = EmbeddingPolicy.Create(["HTTPS://Apps.MyPureCloud.de/", Genesys, "  ", ""]);

        Assert.Equal([Genesys], policy.AllowedOrigins);
    }

    [Theory]
    [InlineData("*")]
    [InlineData("https://*.mypurecloud.de")]
    [InlineData("http://apps.mypurecloud.de")]
    [InlineData("apps.mypurecloud.de")]
    [InlineData("https://apps.mypurecloud.de/path")]
    [InlineData("https://apps.mypurecloud.de?x=1")]
    [InlineData("https://user:pw@apps.mypurecloud.de")]
    [InlineData("https://apps.mypurecloud.de https://evil.example")]
    [InlineData("https://apps.mypurecloud.de; script-src *")]
    [InlineData("'self'")]
    [InlineData("data:")]
    public void UnsafeOrMalformedOrigins_AreRejectedAtStartup(string origin)
    {
        Assert.Throws<InvalidOperationException>(() => EmbeddingPolicy.Create([origin]));
    }

    [Fact]
    public void SessionCookies_GetPartitioned_OthersAreUntouched()
    {
        var headers = new[]
        {
            "TigerCS.Web.Auth=abc; path=/; secure; samesite=none; httponly",
            "TigerCS.Web.Antiforgery=xyz; path=/; secure; samesite=none; httponly",
            "tigercs.customers.context=1; path=/; samesite=strict",
        };

        var result = EmbeddingPolicy.MarkSessionCookiesPartitioned(headers);

        Assert.EndsWith("; partitioned", result[0]);
        Assert.EndsWith("; partitioned", result[1]);
        Assert.Equal(headers[2], result[2]);
    }

    [Fact]
    public void SessionCookies_AlreadyPartitioned_AreNotDuplicated()
    {
        var header = "TigerCS.Web.Auth=abc; path=/; secure; samesite=none; partitioned";

        Assert.Equal(header, EmbeddingPolicy.MarkSessionCookiesPartitioned(header).ToString());
    }

    [Fact]
    public void Shipped_AppSettings_AllowOnlyTheApprovedGenesysOrigin([System.Runtime.CompilerServices.CallerFilePath] string testFilePath = "")
    {
        var path = Path.Combine(Path.GetDirectoryName(testFilePath)!, "..", "..", "TigerCS.Web", "appsettings.json");
        var json = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
        var origins = json.RootElement.GetProperty("Embedding").GetProperty("AllowedFrameAncestors")
            .EnumerateArray().Select(e => e.GetString()).ToArray();

        Assert.Equal([Genesys], origins);
    }

    // ---- the real Web host: what the browser actually receives ----

    private static async Task<HttpResponseMessage> GetHealth(string? origin)
    {
        await using var factory = new WebApplicationFactory<TigerCsWeb::Program>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Development");
            // An empty array in config would not clear the shipped value, so name the single entry (or a blank one) explicitly.
            b.UseSetting("Embedding:AllowedFrameAncestors:0", origin ?? "");
        });
        return await factory.CreateClient().GetAsync("/health");
    }

    [Fact]
    public async Task Host_WithGenesysOrigin_SendsScopedFrameAncestors_AndNoXFrameOptions()
    {
        var response = await GetHealth(Genesys);

        var csp = Assert.Single(response.Headers.GetValues("Content-Security-Policy"));
        Assert.Contains($"frame-ancestors {Genesys}", csp);
        Assert.DoesNotContain("frame-ancestors *", csp);
        Assert.Contains("script-src 'self'", csp);
        Assert.DoesNotContain("unsafe-inline", csp);
        Assert.False(response.Headers.Contains("X-Frame-Options"));
    }

    [Fact]
    public async Task Host_WithNoOrigin_StillDeniesFraming()
    {
        var response = await GetHealth(null);

        var csp = Assert.Single(response.Headers.GetValues("Content-Security-Policy"));
        Assert.Contains("frame-ancestors 'none'", csp);
        Assert.Equal("DENY", Assert.Single(response.Headers.GetValues("X-Frame-Options")));
    }
}

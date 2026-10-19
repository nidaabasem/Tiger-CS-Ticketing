using System.Net;
using Microsoft.Extensions.Options;
using TigerCS.Application.Modules.CrmDocuments.Abstractions;
using TigerCS.Integrations.Modules.CrmIntegration;
using TigerCS.Tests.CustomerVerification.Fakes;
using TigerCS.Tests.Notifications.Fakes;

namespace TigerCS.Tests.CrmDocuments;

/// <summary>
/// When CRM's <c>fileUrl</c> is refused (<c>CRM_INVALID_RESPONSE</c>), the log must say which rule failed and where the reference
/// pointed — without the query string, user info or the CRM secret. The refusal itself (no fetch, no secret) is covered by
/// <see cref="CrmDocumentHttpGatewayTests"/>.
/// </summary>
public class CrmDocumentReferenceDiagnosticsTests
{
    private const string BaseUrl = "https://crm.example.test:8014/";
    private const string Secret = "test-only-secret";

    private static (CrmDocumentHttpGateway Gateway, CapturingLogger<CrmDocumentHttpGateway> Log, StubHttpMessageHandler Handler) Build(
        Func<HttpRequestMessage, HttpResponseMessage>? respond = null)
    {
        var handler = new StubHttpMessageHandler((r, _) => Task.FromResult(respond?.Invoke(r) ?? new HttpResponseMessage(HttpStatusCode.OK)));
        var log = new CapturingLogger<CrmDocumentHttpGateway>();
        var gateway = new CrmDocumentHttpGateway(
            new HttpClient(handler) { BaseAddress = new Uri(BaseUrl) },
            Options.Create(new CrmGatewayOptions { BaseUrl = BaseUrl, SecretKey = Secret }), log);
        return (gateway, log, handler);
    }

    private static Task Download(CrmDocumentHttpGateway g, string reference) =>
        g.DownloadAsync(new CrmDocumentRecord("5001", "Contract", reference));

    [Theory]
    [InlineData("https://other.example.test/Uploads/a.pdf", "HostNotOnCrmOriginOrAllowList")]
    [InlineData("http://crm.example.test:8014/Uploads/a.pdf", "SchemeDiffersFromCrmOrigin")]
    [InlineData("https://crm.example.test/Uploads/a.pdf", "PortDiffersFromCrmOrigin")]
    [InlineData("http://10.0.0.5/Uploads/a.pdf", "HostNotOnCrmOrigin_NotHttps")]
    [InlineData(@"C:\inetpub\Uploads\a.pdf", "NotAnHttpUrl")]
    public async Task ARefusedReference_LogsWhichRuleFailed_AndFetchesNothing(string reference, string reason)
    {
        var (gateway, log, handler) = Build();

        var ex = await Assert.ThrowsAsync<CrmDocumentSourceException>(() => Download(gateway, reference));

        Assert.Equal(CrmDocumentSourceFailure.ReferenceRejected, ex.Failure);
        Assert.Equal(0, handler.CallCount);
        var line = Assert.Single(log.Messages, m => m.Contains("refused"));
        Assert.Contains(reason, line);
        Assert.Contains("crm.example.test:8014", line);   // the configured origin it was compared with
        Assert.DoesNotContain(Secret, line);
    }

    [Fact]
    public async Task TheLoggedReference_NeverIncludesQueryFragmentOrUserInfo()
    {
        var (gateway, log, _) = Build();

        await Assert.ThrowsAsync<CrmDocumentSourceException>(
            () => Download(gateway, "https://user:pw@other.example.test/Uploads/a.pdf?sig=SECRETSIG&token=abc#frag"));

        var all = string.Join('\n', log.Messages);
        Assert.Contains("other.example.test", all);
        Assert.Contains("/Uploads/a.pdf", all);
        foreach (var leaked in new[] { "SECRETSIG", "token=abc", "frag", "user:pw", "pw@" })
        {
            Assert.DoesNotContain(leaked, all);
        }
    }

    [Fact]
    public async Task ANonUrlReference_IsLoggedWithoutItsQuery()
    {
        var (gateway, log, _) = Build();

        await Assert.ThrowsAsync<CrmDocumentSourceException>(() => Download(gateway, @"D:\files\a.pdf?key=SECRETKEY"));

        var all = string.Join('\n', log.Messages);
        Assert.Contains(@"D:\files\a.pdf", all);
        Assert.DoesNotContain("SECRETKEY", all);
    }

    [Fact]
    public async Task ARedirectOnTheDownload_LogsWhereItPointed_WithoutItsQuery_AndIsStillNotFollowed()
    {
        var (gateway, log, handler) = Build(_ =>
        {
            var r = new HttpResponseMessage(HttpStatusCode.Found);
            r.Headers.Location = new Uri("https://crm.example.test:8014/Account/Login?ReturnUrl=%2FUploads%2Fa.pdf&token=SECRETTOKEN");
            return r;
        });

        var ex = await Assert.ThrowsAsync<CrmDocumentSourceException>(() => Download(gateway, "/Uploads/a.pdf"));

        Assert.Equal(CrmDocumentSourceFailure.ReferenceRejected, ex.Failure);
        Assert.Equal(1, handler.CallCount);                // not followed
        var all = string.Join('\n', log.Messages);
        Assert.Contains("redirected (302)", all);
        Assert.Contains("/Account/Login", all);
        Assert.DoesNotContain("SECRETTOKEN", all);
        Assert.DoesNotContain("ReturnUrl", all);
    }
}

using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TigerCS.Application.Modules.CrmDocuments;
using TigerCS.Application.Modules.CrmDocuments.Abstractions;
using TigerCS.Domain.Modules.CustomerVerification;

namespace TigerCS.Integrations.Modules.CrmIntegration;

/// <summary>
/// Tiger CRM's customer-document API, over the existing <c>Crm</c>
/// configuration and the same server-side authentication convention as
/// <see cref="CrmBuyerHttpGateway"/>: <c>X-SECRET-KEY</c> from
/// <c>Crm:SecretKey</c>, never from a caller.
///
/// <para>
/// <c>POST {Crm:BaseUrl}/TicketingSystem/GetCustomerDocuments</c> with
/// <c>{ "CustomerID", "LeadID", "DocumentType" }</c>
/// (<see cref="CrmDocumentTypeMapping"/>). Every status CRM can answer is
/// handled explicitly: 200 (parsed and <b>checked to be for the customer and
/// lead asked about</b>), 404 (nothing on record — an empty list), 400
/// (<see cref="CrmDocumentSourceFailure.RequestRejected"/>), 401
/// (<see cref="CrmDocumentSourceFailure.AuthenticationFailed"/>), 403
/// (<see cref="CrmDocumentSourceFailure.AccessDenied"/>), and 500/503/other
/// 5xx, timeouts and connection failures
/// (<see cref="CrmDocumentSourceFailure.Unavailable"/>).
/// </para>
///
/// <para>
/// <b>Attachment documents (ReservationForm, TigerContract, RegistrationReceipt) are never fetched from their <c>fileUrl</c></b> —
/// those objects are private storage. They are downloaded through CRM's authenticated
/// <c>POST {Crm:BaseUrl}/TicketingSystem/GetCustomerDocumentFile</c> with
/// <c>{ "CustomerID", "LeadID", "DocumentType", "AttachmentId" }</c>: CRM re-checks that the attachment belongs to that customer,
/// lead and type, then streams the bytes from its private store. TigerCS only ever asks for an <c>attachmentId</c> that
/// <c>GetCustomerDocuments</c> just listed for the verified customer and lead, and checks the answer is a complete PDF
/// (<c>%PDF-</c> header, <c>%%EOF</c> trailer, size cap, and CRM's <c>X-Content-SHA256</c> when sent). No AWS URL or credential
/// is ever seen by TigerCS.
/// </para>
///
/// <para>
/// <b>Only the unit layout still uses <c>fileUrl</c></b> (it is the unit's <c>unitplan</c> field, not an attachment). It is
/// a storage reference, not a public URL, and is
/// fetched only by this server, with the CRM credential, and only when it
/// resolves to the configured CRM origin (a relative or <c>~/</c> path is
/// resolved against <c>Crm:BaseUrl</c>) or to a host listed in
/// <see cref="CrmGatewayOptions.DocumentFileHosts"/>: the secret is never sent
/// to a host the configuration did not name. Redirects are not followed (a
/// redirect could carry the credential elsewhere), an HTML body is rejected
/// (a login/error page served with 200), and the body is read under a size cap.
/// The URL is never logged, returned or stored.
/// </para>
/// </summary>
public sealed class CrmDocumentHttpGateway(
    HttpClient httpClient, IOptions<CrmGatewayOptions> options, ILogger<CrmDocumentHttpGateway> logger)
    : ICrmDocumentGateway
{
    private const string SecretHeaderName = "X-SECRET-KEY";
    private const string ListPath = "TicketingSystem/GetCustomerDocuments";

    private sealed record ListRequest(
        [property: JsonPropertyName("CustomerID")] int CustomerId,
        [property: JsonPropertyName("LeadID")] int LeadId,
        [property: JsonPropertyName("DocumentType")] string DocumentType);

    public async Task<CrmDocumentListing> ListAsync(
        CrmDocumentType type, int customerId, int leadId, CancellationToken cancellationToken = default)
    {
        var secretKey = RequireSecretKey();

        using var request = new HttpRequestMessage(HttpMethod.Post, ListPath)
        {
            Content = JsonContent.Create(new ListRequest(customerId, leadId, CrmDocumentTypeMapping.ToCrmName(type)))
        };
        request.Headers.TryAddWithoutValidation(SecretHeaderName, secretKey);

        using var response = await SendAsync(request, "GetCustomerDocuments", cancellationToken);

        switch (response.StatusCode)
        {
            case HttpStatusCode.OK:
                return await ParseListingAsync(response, type, customerId, leadId, cancellationToken);

            case HttpStatusCode.NotFound:
                // Unknown customer/lead, or nothing of this type on record: a normal "no documents".
                return new CrmDocumentListing(customerId, leadId, false, []);

            default:
                throw FailureFor(response.StatusCode, "GetCustomerDocuments");
        }
    }

    private const string FilePath = "TicketingSystem/GetCustomerDocumentFile";

    private sealed record FileRequest(
        [property: JsonPropertyName("CustomerID")] int CustomerId,
        [property: JsonPropertyName("LeadID")] int LeadId,
        [property: JsonPropertyName("DocumentType")] string DocumentType,
        [property: JsonPropertyName("AttachmentId")] object AttachmentId);

    public async Task<CrmDocumentContent?> DownloadAsync(
        CrmDocumentType type, int customerId, int leadId, CrmDocumentRecord record, CancellationToken cancellationToken = default)
    {
        var secretKey = RequireSecretKey();

        if (type == CrmDocumentType.UnitLayout)
        {
            return await DownloadByFileUrlAsync(record, secretKey, cancellationToken);
        }

        // The id is what GetCustomerDocuments listed. CRM's attachmentId is numeric; anything else could not be one of its own.
        object attachmentId = long.TryParse(record.RecordId, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var numeric)
            ? numeric
            : throw Invalid("The attachment id is not a CRM attachment id.");

        using var request = new HttpRequestMessage(HttpMethod.Post, FilePath)
        {
            Content = JsonContent.Create(new FileRequest(customerId, leadId, CrmDocumentTypeMapping.ToCrmName(type), attachmentId))
        };
        request.Headers.TryAddWithoutValidation(SecretHeaderName, secretKey);

        using var response = await SendAsync(request, "GetCustomerDocumentFile", cancellationToken, HttpCompletionOption.ResponseHeadersRead);

        // CRM answers 404 for "no such attachment" AND for "not this customer's / lead's / type": the two are indistinguishable by design.
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        if (response.StatusCode != HttpStatusCode.OK)
        {
            throw FailureFor(response.StatusCode, "GetCustomerDocumentFile");
        }

        var content = await ReadFileAsync(response, record, requirePdf: true, request.RequestUri!.AbsolutePath, cancellationToken);
        VerifyChecksum(response, content.Bytes, record);
        return content;
    }

    /// <summary>The unit layout only: its <c>unitplan</c> reference, fetched from the CRM origin (or an allow-listed host) under the rules in the class remarks.</summary>
    private async Task<CrmDocumentContent?> DownloadByFileUrlAsync(CrmDocumentRecord record, string secretKey, CancellationToken cancellationToken)
    {
        var (target, isCrmOrigin) = ResolveFileReference(record.FileReference);

        using var request = new HttpRequestMessage(HttpMethod.Get, target);
        if (isCrmOrigin)
        {
            request.Headers.TryAddWithoutValidation(SecretHeaderName, secretKey);
        }

        using var response = await SendAsync(request, "document download", cancellationToken, HttpCompletionOption.ResponseHeadersRead);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        if ((int)response.StatusCode is >= 300 and < 400)
        {
            // Often a login redirect: the file route is not accepting X-SECRET-KEY. Where it points is useful; its query is not logged.
            logger.LogWarning(
                "CRM document download for record {RecordId} was redirected ({Status}) to {Location}; redirects are never followed with the CRM credential.",
                record.RecordId, (int)response.StatusCode, RedactLocation(response.Headers.Location));
        }

        if (response.StatusCode != HttpStatusCode.OK)
        {
            throw FailureFor(response.StatusCode, "document download");
        }

        return await ReadFileAsync(response, record, requirePdf: false, target.AbsolutePath, cancellationToken);
    }

    /// <summary>
    /// Reads the body under the size cap and establishes what it is from its bytes. An HTML body is a login/error page, not a
    /// document; an attachment document must be a complete PDF.
    /// </summary>
    private async Task<CrmDocumentContent> ReadFileAsync(
        HttpResponseMessage response, CrmDocumentRecord record, bool requirePdf, string pathHint, CancellationToken cancellationToken)
    {
        var declared = response.Content.Headers.ContentType?.MediaType;
        if (string.Equals(declared, "text/html", StringComparison.OrdinalIgnoreCase))
        {
            // A login or error page served with 200 is not a document.
            logger.LogWarning("CRM document download for record {RecordId} returned HTML instead of a file.", record.RecordId);
            throw new CrmDocumentSourceException(CrmDocumentSourceFailure.InvalidResponse, "CRM returned an HTML page instead of a document.");
        }

        var bytes = await ReadBoundedAsync(response, options.Value.MaxDocumentBytes, cancellationToken);

        // The true type comes from the file itself (signature), corroborated by the declared type and the file-name
        // metadata CRM gave us (Content-Disposition, storage path, attachment name). Never assumed to be a PDF.
        var disposition = response.Content.Headers.ContentDisposition;
        var detected = CrmDocumentFileType.Resolve(
            bytes, declared, disposition?.FileNameStar ?? disposition?.FileName, pathHint, record.Label);
        if (detected is null)
        {
            logger.LogWarning(
                "CRM document for record {RecordId} is not a recognised document type (declared {DeclaredType}); it was not sent.",
                record.RecordId, declared ?? "none");
            throw new CrmDocumentSourceException(
                CrmDocumentSourceFailure.InvalidResponse, "The file's type could not be established as an allowed document type.");
        }

        if (requirePdf && !CrmDocumentFileType.IsCompletePdf(bytes, detected))
        {
            logger.LogWarning(
                "CRM document for record {RecordId} is not a complete PDF (detected {MediaType}, {Length} bytes); it was not sent.",
                record.RecordId, detected.MediaType, bytes.Length);
            throw new CrmDocumentSourceException(CrmDocumentSourceFailure.InvalidResponse, "CRM returned a file that is not a complete PDF.");
        }

        return new CrmDocumentContent(record.RecordId, bytes, detected.MediaType, CrmDocumentFileType.FileName(record.Label, detected));
    }

    /// <summary>When CRM sends <c>X-Content-SHA256</c> (hex), the bytes received must hash to it — a truncated or altered transfer is not sent.</summary>
    private void VerifyChecksum(HttpResponseMessage response, byte[] bytes, CrmDocumentRecord record)
    {
        if (!response.Headers.TryGetValues("X-Content-SHA256", out var values))
        {
            return;
        }

        var expected = values.FirstOrDefault()?.Trim();
        var actual = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes));
        if (!string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase))
        {
            logger.LogWarning("CRM document for record {RecordId} does not match the checksum CRM sent; it was not sent.", record.RecordId);
            throw new CrmDocumentSourceException(CrmDocumentSourceFailure.InvalidResponse, "The document does not match the checksum CRM sent.");
        }
    }

    // ---- listing ----

    private async Task<CrmDocumentListing> ParseListingAsync(
        HttpResponseMessage response, CrmDocumentType type, int customerId, int leadId, CancellationToken cancellationToken)
    {
        JsonDocument document;
        try
        {
            document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException)
        {
            logger.LogWarning(ex, "CRM GetCustomerDocuments returned a malformed body.");
            throw new CrmDocumentSourceException(CrmDocumentSourceFailure.InvalidResponse, "CRM returned a malformed response body.", ex);
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw Invalid("The response is not a JSON object.");
            }

            // Tolerate the legacy {success:false} envelope other CRM ticketing endpoints use.
            if (TryGet(root, "success", out var success) && success.ValueKind == JsonValueKind.False)
            {
                throw Invalid("CRM reported success=false.");
            }

            // The answer must be for the customer and lead asked about — the ownership guarantee at the wire.
            if (!TryGetInt(root, "customerId", out var echoedCustomer) || echoedCustomer != customerId
                || !TryGetInt(root, "leadId", out var echoedLead) || echoedLead != leadId)
            {
                throw Invalid("The response does not name the customer and lead that were requested.");
            }

            var selectionRequired = TryGet(root, "selectionRequired", out var sel) && sel.ValueKind == JsonValueKind.True;

            var records = new List<CrmDocumentRecord>();
            if (TryGet(root, "attachments", out var attachments) && attachments.ValueKind == JsonValueKind.Array)
            {
                var layoutIndex = 0;
                foreach (var item in attachments.EnumerateArray())
                {
                    var fileUrl = GetString(item, "fileUrl");
                    if (type == CrmDocumentType.UnitLayout && string.IsNullOrWhiteSpace(fileUrl))
                    {
                        logger.LogWarning("CRM returned an attachment without a fileUrl for a {DocumentType}; it was skipped.", type);
                        continue;
                    }

                    var name = GetString(item, "name");
                    string recordId;
                    if (type == CrmDocumentType.UnitLayout)
                    {
                        // The unit's unitplan has no attachmentId.
                        recordId = layoutIndex++ == 0
                            ? CrmDocumentTypeMapping.LayoutRecordId(leadId)
                            : $"{CrmDocumentTypeMapping.LayoutRecordId(leadId)}-{layoutIndex}";
                    }
                    else
                    {
                        recordId = GetString(item, "attachmentId")
                            ?? throw Invalid("An attachment has no attachmentId.");
                    }

                    records.Add(new CrmDocumentRecord(recordId.Trim(), string.IsNullOrWhiteSpace(name) ? DefaultLabel(type) : name.Trim(), fileUrl?.Trim() ?? string.Empty));
                }
            }

            return new CrmDocumentListing(customerId, leadId, selectionRequired, records);
        }
    }

    // ---- file reference ----

    /// <summary>The URI a stored <c>fileUrl</c> may be fetched from with the CRM credential, or a <see cref="CrmDocumentSourceFailure.ReferenceRejected"/>.</summary>
    internal (Uri Target, bool IsCrmOrigin) ResolveFileReference(string fileReference)
    {
        var baseAddress = httpClient.BaseAddress
            ?? throw new CrmDocumentSourceException(CrmDocumentSourceFailure.Unavailable, "Crm:BaseUrl is not configured.");

        var reference = fileReference.Trim();
        if (reference.StartsWith("~/", StringComparison.Ordinal))
        {
            reference = reference[2..]; // legacy ASP.NET virtual path: relative to the CRM application root
        }

        Uri target;
        if (Uri.TryCreate(reference, UriKind.Absolute, out var absolute) && (absolute.Scheme == Uri.UriSchemeHttp || absolute.Scheme == Uri.UriSchemeHttps))
        {
            target = absolute;
        }
        else if (!reference.Contains("://", StringComparison.Ordinal) && Uri.TryCreate(baseAddress, reference, out var relative))
        {
            target = relative;
        }
        else
        {
            throw Rejected(reference, "NotAnHttpUrl", baseAddress);
        }

        if (target.Scheme != Uri.UriSchemeHttp && target.Scheme != Uri.UriSchemeHttps)
        {
            // e.g. a Windows physical path ("C:\\…") that parses as a URI with a one-letter scheme.
            throw Rejected(reference, "NotAnHttpUrl", baseAddress);
        }

        var sameOrigin = string.Equals(target.Host, baseAddress.Host, StringComparison.OrdinalIgnoreCase)
            && target.Scheme == baseAddress.Scheme && target.Port == baseAddress.Port;
        var allowedHost = target.Scheme == Uri.UriSchemeHttps
            && options.Value.DocumentFileHosts.Any(h => string.Equals(h?.Trim(), target.Host, StringComparison.OrdinalIgnoreCase));

        if (!sameOrigin && !allowedHost)
        {
            throw Rejected(reference, RejectionReason(target, baseAddress), baseAddress);
        }

        // The CRM secret goes to the CRM origin and nowhere else. An allow-listed storage host is fetched
        // WITHOUT it: that host is not CRM, and the secret is not ours to hand it.
        return (target, sameOrigin);

        CrmDocumentSourceException Rejected(string rawReference, string reason, Uri crmOrigin)
        {
            // Which rule failed and where the reference points — never the query string, fragment or user info, which can carry tokens.
            logger.LogWarning(
                "CRM document reference refused ({Reason}): {Reference}. Configured CRM origin is {Origin}; nothing was fetched and the CRM secret was not sent.",
                reason, Redact(rawReference), $"{crmOrigin.Scheme}://{crmOrigin.Host}:{crmOrigin.Port}");
            return new CrmDocumentSourceException(CrmDocumentSourceFailure.ReferenceRejected, "The document reference is not on the configured CRM origin.");
        }
    }

    private static string RejectionReason(Uri target, Uri crmOrigin)
    {
        if (target.Scheme == Uri.UriSchemeHttps && !string.Equals(target.Host, crmOrigin.Host, StringComparison.OrdinalIgnoreCase))
        {
            return "HostNotOnCrmOriginOrAllowList";
        }

        if (!string.Equals(target.Host, crmOrigin.Host, StringComparison.OrdinalIgnoreCase))
        {
            return "HostNotOnCrmOrigin_NotHttps";
        }

        return target.Scheme != crmOrigin.Scheme ? "SchemeDiffersFromCrmOrigin" : "PortDiffersFromCrmOrigin";
    }

    /// <summary>
    /// A reference safe to log: scheme, host, port and path of a URL (user info, query and fragment dropped), or the first
    /// characters of a non-URL value (a physical path, say) with any query dropped.
    /// </summary>
    internal static string Redact(string reference)
    {
        const int Max = 200;
        if (Uri.TryCreate(reference, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
        {
            var text = $"{uri.Scheme}://{uri.Host}:{uri.Port}{uri.AbsolutePath}";
            return text.Length > Max ? text[..Max] + "…" : text;
        }

        var cut = reference.IndexOfAny(['?', '#']);
        var bare = cut >= 0 ? reference[..cut] : reference;
        return bare.Length > Max ? bare[..Max] + "…" : bare;
    }

    private static string? RedactLocation(Uri? location) => location is null ? null : Redact(location.IsAbsoluteUri ? location.ToString() : location.OriginalString);

    // ---- plumbing ----

    private string RequireSecretKey()
    {
        var secretKey = options.Value.SecretKey;
        if (string.IsNullOrWhiteSpace(secretKey))
        {
            logger.LogError("Crm:SecretKey is not configured — cannot call CRM for customer documents. See docs/DEV-SETUP.md §3a.");
            throw new CrmDocumentSourceException(CrmDocumentSourceFailure.AuthenticationFailed, "Crm:SecretKey is not configured.");
        }

        return secretKey;
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, string operation, CancellationToken cancellationToken,
        HttpCompletionOption completion = HttpCompletionOption.ResponseContentRead)
    {
        try
        {
            return await httpClient.SendAsync(request, completion, cancellationToken);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(ex, "CRM {Operation} timed out.", operation);
            throw new CrmDocumentSourceException(CrmDocumentSourceFailure.Unavailable, "CRM request timed out.", ex);
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException)
        {
            // InvalidOperationException: unconfigured Crm:BaseUrl (relative URI, no base address).
            logger.LogWarning(ex, "CRM {Operation} could not be reached.", operation);
            throw new CrmDocumentSourceException(CrmDocumentSourceFailure.Unavailable, "CRM could not be reached.", ex);
        }
    }

    private CrmDocumentSourceException FailureFor(HttpStatusCode status, string operation)
    {
        switch (status)
        {
            case HttpStatusCode.BadRequest:
                logger.LogError("CRM {Operation} returned 400 Bad Request — the request does not match CRM's contract.", operation);
                return new CrmDocumentSourceException(CrmDocumentSourceFailure.RequestRejected, "CRM rejected the request (400).");
            case HttpStatusCode.Unauthorized:
                logger.LogError("CRM {Operation} returned 401 Unauthorized — check Crm:SecretKey.", operation);
                return new CrmDocumentSourceException(CrmDocumentSourceFailure.AuthenticationFailed, "CRM rejected the configured secret key (401).");
            case HttpStatusCode.Forbidden:
                logger.LogError("CRM {Operation} returned 403 Forbidden.", operation);
                return new CrmDocumentSourceException(CrmDocumentSourceFailure.AccessDenied, "CRM denied access (403).");
            case HttpStatusCode.Moved or HttpStatusCode.Redirect or HttpStatusCode.RedirectMethod or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect:
                logger.LogWarning("CRM {Operation} answered with a redirect; redirects are not followed with the CRM credential.", operation);
                return new CrmDocumentSourceException(CrmDocumentSourceFailure.ReferenceRejected, "CRM redirected the request; redirects are not followed.");
            default:
                // 500, 502, 503, 504 and anything unexpected: not an answer.
                logger.LogWarning("CRM {Operation} returned unexpected status {StatusCode}.", operation, (int)status);
                return new CrmDocumentSourceException(CrmDocumentSourceFailure.Unavailable, $"CRM returned status {(int)status}.");
        }
    }

    private static CrmDocumentSourceException Invalid(string message) => new(CrmDocumentSourceFailure.InvalidResponse, message);

    private static async Task<byte[]> ReadBoundedAsync(HttpResponseMessage response, int maxBytes, CancellationToken cancellationToken)
    {
        // Reads at most maxBytes + 1: enough for the caller's size check to see "too large" without buffering an unbounded body.
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        var remaining = (long)maxBytes + 1;
        while (remaining > 0)
        {
            var read = await stream.ReadAsync(chunk.AsMemory(0, (int)Math.Min(chunk.Length, remaining)), cancellationToken);
            if (read == 0)
            {
                break;
            }

            buffer.Write(chunk, 0, read);
            remaining -= read;
        }

        return buffer.ToArray();
    }

    private static bool TryGet(JsonElement obj, string name, out JsonElement value)
    {
        if (obj.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in obj.EnumerateObject())
            {
                if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }
        }

        value = default;
        return false;
    }

    private static bool TryGetInt(JsonElement obj, string name, out int value)
    {
        value = 0;
        if (!TryGet(obj, name, out var element))
        {
            return false;
        }

        return element.ValueKind switch
        {
            JsonValueKind.Number => element.TryGetInt32(out value),
            JsonValueKind.String => int.TryParse(element.GetString(), out value),
            _ => false
        };
    }

    /// <summary>A string property, accepting a JSON number too (attachmentId may be either).</summary>
    private static string? GetString(JsonElement obj, string name) =>
        TryGet(obj, name, out var element)
            ? element.ValueKind switch
            {
                JsonValueKind.String => element.GetString(),
                JsonValueKind.Number => element.GetRawText(),
                _ => null
            }
            : null;

    private static string DefaultLabel(CrmDocumentType type) => type switch
    {
        CrmDocumentType.ReservationForm => "Reservation Form",
        CrmDocumentType.UnitLayout => "Unit Layout",
        CrmDocumentType.RegistrationReceipt => "Registration Receipt",
        _ => "Contract"
    };
}

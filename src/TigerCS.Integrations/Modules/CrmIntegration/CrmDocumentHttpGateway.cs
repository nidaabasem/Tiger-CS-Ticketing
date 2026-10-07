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
/// <b><c>fileUrl</c> is a storage reference, not a public URL.</b> It is
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

    public async Task<CrmDocumentContent?> DownloadAsync(CrmDocumentRecord record, CancellationToken cancellationToken = default)
    {
        var secretKey = RequireSecretKey();
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

        if (response.StatusCode != HttpStatusCode.OK)
        {
            throw FailureFor(response.StatusCode, "document download");
        }

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
            bytes, declared, disposition?.FileNameStar ?? disposition?.FileName, target.AbsolutePath, record.Label);
        if (detected is null)
        {
            logger.LogWarning(
                "CRM document for record {RecordId} is not a recognised document type (declared {DeclaredType}); it was not sent.",
                record.RecordId, declared ?? "none");
            throw new CrmDocumentSourceException(
                CrmDocumentSourceFailure.InvalidResponse, "The file's type could not be established as an allowed document type.");
        }

        return new CrmDocumentContent(record.RecordId, bytes, detected.MediaType, CrmDocumentFileType.FileName(record.Label, detected));
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
                    if (string.IsNullOrWhiteSpace(fileUrl))
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

                    records.Add(new CrmDocumentRecord(recordId.Trim(), string.IsNullOrWhiteSpace(name) ? DefaultLabel(type) : name.Trim(), fileUrl.Trim()));
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
            throw Rejected();
        }

        var sameOrigin = string.Equals(target.Host, baseAddress.Host, StringComparison.OrdinalIgnoreCase)
            && target.Scheme == baseAddress.Scheme && target.Port == baseAddress.Port;
        var allowedHost = target.Scheme == Uri.UriSchemeHttps
            && options.Value.DocumentFileHosts.Any(h => string.Equals(h?.Trim(), target.Host, StringComparison.OrdinalIgnoreCase));

        if (!sameOrigin && !allowedHost)
        {
            logger.LogWarning("CRM document reference points at host {Host}, which is not the configured CRM origin or an allowed file host; nothing was fetched.", target.Host);
            throw Rejected();
        }

        // The CRM secret goes to the CRM origin and nowhere else. An allow-listed storage host is fetched
        // WITHOUT it: that host is not CRM, and the secret is not ours to hand it.
        return (target, sameOrigin);

        static CrmDocumentSourceException Rejected() =>
            new(CrmDocumentSourceFailure.ReferenceRejected, "The document reference is not on the configured CRM origin.");
    }

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

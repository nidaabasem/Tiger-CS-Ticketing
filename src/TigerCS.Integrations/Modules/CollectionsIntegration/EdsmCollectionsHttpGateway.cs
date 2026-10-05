using System.Globalization;
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TigerCS.Application.Modules.Collections;
using TigerCS.Application.Modules.Collections.Abstractions;
using TigerCS.Integrations.Modules.PactIntegration;

namespace TigerCS.Integrations.Modules.CollectionsIntegration;

/// <summary>
/// The real <see cref="IEdsmCollectionsGateway"/>, on the <c>PactApi</c> base
/// URL and <c>X-API-KEY</c> (the same settings as <see cref="PactCustomerHttpGateway"/>).
///
/// <para>
/// <b>Transport (contract §1, VERIFIED in EDSM source):</b>
/// <list type="bullet">
/// <item>The success body is <c>{"title":"","status":200,"data":…}</c> (camelCase), and
/// the HTTP status equals the envelope's <c>status</c>.</item>
/// <item>A business-rule 400 (for example "Company not supported") is the same envelope
/// with <c>data: null</c>.</item>
/// <item>A validation 400 is ASP.NET ProblemDetails, not the envelope.</item>
/// <item>A missing key gives 401 and a wrong key gives 403, each with a plain-text body.</item>
/// <item>A 500 has no envelope.</item>
/// </list>
/// Each case is told apart and mapped to its own <see cref="EdsmOutcome"/>.
/// The API key is never logged.
/// </para>
/// </summary>
public sealed class EdsmCollectionsHttpGateway(
    HttpClient httpClient,
    IOptions<PactApiOptions> pactOptions,
    IOptions<CollectionsEdsmOptions> edsmOptions,
    ILogger<EdsmCollectionsHttpGateway> logger) : IEdsmCollectionsGateway
{
    private const string ApiKeyHeaderName = "X-API-KEY";

    public string SourceName => "Pact";

    private CultureInfo? Culture => EdsmAmountParser.ResolveCulture(edsmOptions.Value.EdsmNumberCulture);

    public Task<EdsmResult<EdsmPaymentSummary>> GetPaymentSummaryAsync(int companyId, string tenantId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        if (EdsmCompanies.Find(companyId) is null)
        {
            return Task.FromResult(EdsmResult<EdsmPaymentSummary>.Fail(EdsmOutcome.NotSupported, $"EDSM does not support company {companyId}."));
        }

        var culture = Culture;
        return SendAsync(Query("v1/reports/payment-summary", ("TenantId", tenantId), ("CompanyId", Invariant(companyId))),
            data => ReadSummary(data, culture), cancellationToken);
    }

    public Task<EdsmResult<EdsmPaymentTransactions>> GetPaymentTransactionsAsync(
        int companyId, string tenantId, string mobile, EdsmTransactionType type, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(mobile);
        if (type is not (EdsmTransactionType.Paid or EdsmTransactionType.Due or EdsmTransactionType.Outstanding))
        {
            // TransactionTypeId 4 is never sent: for rented companies it writes to EDSM's databases.
            throw new ArgumentOutOfRangeException(nameof(type), type, "Only read-only transaction types 1-3 are supported.");
        }

        if (EdsmCompanies.Find(companyId) is not { } company)
        {
            return Task.FromResult(EdsmResult<EdsmPaymentTransactions>.Fail(EdsmOutcome.NotSupported, $"EDSM does not support company {companyId}."));
        }

        var culture = Culture;
        var aedSuffix = company.Model == EdsmBusinessModel.Rented && type == EdsmTransactionType.Due;
        return SendAsync(
            Query("v1/reports/payment-transactions", ("Mobile", mobile), ("TenantId", tenantId), ("CompanyId", Invariant(companyId)),
                ("TransactionTypeId", Invariant((int)type))),
            data => ReadTransactions(data, culture, aedSuffix), cancellationToken);
    }

    public Task<EdsmResult<IReadOnlyList<EdsmDueInstallment>>> GetDueInstallmentsAsync(
        int companyId, DateOnly fromDate, DateOnly toDate, CancellationToken cancellationToken = default)
    {
        if (EdsmCompanies.Find(companyId) is not { SupportsDueInstallments: true })
        {
            return Task.FromResult(EdsmResult<IReadOnlyList<EdsmDueInstallment>>.Fail(EdsmOutcome.NotSupported,
                $"EDSM due-installments does not support company {companyId}."));
        }

        return SendAsync(
            Query("v1/due-installments", ("CompanyId", Invariant(companyId)),
                ("FromDate", fromDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
                ("ToDate", toDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))),
            ReadDueInstallments, cancellationToken);
    }

    private async Task<EdsmResult<T>> SendAsync<T>(string path, Func<JsonElement, EdsmResult<T>> readData, CancellationToken cancellationToken)
        where T : class
    {
        var apiKey = pactOptions.Value.ApiKey;
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            logger.LogError("PactApi:ApiKey is not configured — cannot call EDSM.");
            return EdsmResult<T>.Fail(EdsmOutcome.Unavailable, "PactApi:ApiKey is not configured.");
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.TryAddWithoutValidation(ApiKeyHeaderName, apiKey);

        HttpResponseMessage response;
        try
        {
            response = await httpClient.SendAsync(request, cancellationToken);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(ex, "EDSM request timed out.");
            return EdsmResult<T>.Fail(EdsmOutcome.Unavailable, "EDSM timed out.");
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException)
        {
            logger.LogWarning(ex, "EDSM could not be reached.");
            return EdsmResult<T>.Fail(EdsmOutcome.Unavailable, "EDSM could not be reached.");
        }

        using (response)
        {
            logger.LogDebug("EDSM: GET {AbsoluteRequestUri} -> HTTP {StatusCode}.", response.RequestMessage?.RequestUri, (int)response.StatusCode);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            var result = Interpret((int)response.StatusCode, body, readData);
            if (result.Outcome is EdsmOutcome.Unauthorized or EdsmOutcome.ValidationRejected or EdsmOutcome.Unavailable)
            {
                logger.LogWarning("EDSM answered {StatusCode}: {Outcome}.", (int)response.StatusCode, result.Outcome);
            }

            return result;
        }
    }

    /// <summary>Status + body → result (contract §1.2). Public so every documented shape is tested without HTTP.</summary>
    public static EdsmResult<T> Interpret<T>(int status, string body, Func<JsonElement, EdsmResult<T>> readData)
        where T : class
    {
        switch (status)
        {
            case 401:
                return EdsmResult<T>.Fail(EdsmOutcome.Unauthorized, "EDSM: API key was not provided (check PactApi:ApiKey).", PlainText(body));
            case 403:
                return EdsmResult<T>.Fail(EdsmOutcome.Unauthorized, "EDSM rejected the configured API key.", PlainText(body));
            case 404:
                return EdsmResult<T>.Fail(EdsmOutcome.Unavailable, "EDSM route not found — check PactApi:BaseUrl.");
            case 200 or 400:
                break;
            default:
                return EdsmResult<T>.Fail(EdsmOutcome.Unavailable, $"EDSM returned HTTP {status}{(status >= 500 ? " (server error, no envelope)" : "")}.");
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(body);
        }
        catch (JsonException)
        {
            return EdsmResult<T>.Fail(EdsmOutcome.InvalidResponse, $"EDSM returned HTTP {status} with a body that is not JSON.");
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return EdsmResult<T>.Fail(EdsmOutcome.InvalidResponse, $"EDSM returned a JSON {root.ValueKind}, not the response envelope.");
            }

            var title = TryGet(root, "title", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;

            // ProblemDetails (framework validation): has "errors" and no "data".
            if (status == 400 && TryGet(root, "errors", out var errors) && !TryGet(root, "data", out _))
            {
                var fields = errors.ValueKind == JsonValueKind.Object ? string.Join(", ", errors.EnumerateObject().Select(p => p.Name)) : "";
                return EdsmResult<T>.Fail(EdsmOutcome.ValidationRejected,
                    $"EDSM rejected the request parameters{(fields.Length > 0 ? $" ({fields})" : "")}.", title);
            }

            if (!TryGet(root, "status", out var envelopeStatus) || !envelopeStatus.TryGetInt32(out var code) || !TryGet(root, "data", out var data))
            {
                return EdsmResult<T>.Fail(EdsmOutcome.InvalidResponse, "EDSM's response is not the {title, status, data} envelope.", title);
            }

            if (code != 200 || status != 200)
            {
                return EdsmResult<T>.Fail(EdsmOutcome.BusinessRuleRejected,
                    $"EDSM refused the request: {(string.IsNullOrWhiteSpace(title) ? $"status {code}" : title)}.", title);
            }

            return data.ValueKind == JsonValueKind.Null
                ? EdsmResult<T>.Fail(EdsmOutcome.InvalidResponse, "EDSM returned status 200 with no data.", title)
                : readData(data);
        }
    }

    public static EdsmResult<EdsmPaymentSummary> ReadSummary(JsonElement data, CultureInfo? culture)
    {
        if (data.ValueKind != JsonValueKind.Object)
        {
            return EdsmResult<EdsmPaymentSummary>.Fail(EdsmOutcome.InvalidResponse, "EDSM's payment-summary data is not an object.");
        }

        EdsmAmount Field(string name) =>
            !TryGet(data, name, out var v) || v.ValueKind == JsonValueKind.Null ? EdsmAmount.Missing
            : v.ValueKind == JsonValueKind.String ? EdsmAmountParser.Parse(v.GetString(), culture)
            : new EdsmAmount(EdsmAmountStatus.Unreadable, null, v.GetRawText());

        return EdsmResult<EdsmPaymentSummary>.Ok(new EdsmPaymentSummary(
            Field("totalAmount"), Field("paidAmount"), Field("dueAmount"), Field("outstandingAmount"), Field("lateFines")));
    }

    public static EdsmResult<EdsmPaymentTransactions> ReadTransactions(JsonElement data, CultureInfo? culture, bool allowAedSuffix)
    {
        if (data.ValueKind != JsonValueKind.Object || !TryGet(data, "transactions", out var rows) || rows.ValueKind != JsonValueKind.Array)
        {
            return EdsmResult<EdsmPaymentTransactions>.Fail(EdsmOutcome.InvalidResponse, "EDSM's payment-transactions data has no transactions array.");
        }

        var items = new List<EdsmTransaction>();
        foreach (var row in rows.EnumerateArray())
        {
            var formatted = TryGet(row, "formattedAmount", out var f) && f.ValueKind == JsonValueKind.String
                ? EdsmAmountParser.Parse(f.GetString(), culture, allowAedSuffix)
                : EdsmAmount.Missing;
            var dateRaw = String(row, "date");
            DateOnly? date = culture is not null && !string.IsNullOrWhiteSpace(dateRaw)
                && DateOnly.TryParseExact(dateRaw, "dd-MMM-yyyy", culture, DateTimeStyles.None, out var d) ? d : null;
            items.Add(new EdsmTransaction(
                Number(row, "amount"), formatted, dateRaw, date, String(row, "chequeNumber"), Int(row, "transactionTypeId"), Int(row, "paymentTypeId")));
        }

        return EdsmResult<EdsmPaymentTransactions>.Ok(new EdsmPaymentTransactions(items));
    }

    public static EdsmResult<IReadOnlyList<EdsmDueInstallment>> ReadDueInstallments(JsonElement data)
    {
        if (data.ValueKind != JsonValueKind.Array)
        {
            return EdsmResult<IReadOnlyList<EdsmDueInstallment>>.Fail(EdsmOutcome.InvalidResponse, "EDSM's due-installments data is not an array.");
        }

        var items = new List<EdsmDueInstallment>();
        foreach (var row in data.EnumerateArray())
        {
            if (Long(row, "companyID") is not { } company || company is < int.MinValue or > int.MaxValue || Long(row, "tenantID") is not { } tenant)
            {
                continue; // a row that cannot be attributed to a company and tenant is never shown
            }

            // chequeDueDate is ISO 8601 without offset (Kind Unspecified): a UAE calendar date.
            var dueRaw = String(row, "chequeDueDate");
            DateOnly? due = dueRaw is { Length: >= 10 } && DateOnly.TryParseExact(dueRaw[..10], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var dd)
                ? dd : null;
            items.Add(new EdsmDueInstallment((int)company, tenant, (int?)Long(row, "unitID"), String(row, "voucherNumber"), String(row, "chequeNumber"),
                due, Number(row, "amount"), String(row, "status")));
        }

        return EdsmResult<IReadOnlyList<EdsmDueInstallment>>.Ok(items);
    }

    private static string Query(string path, params (string Name, string Value)[] parameters) =>
        path + "?" + string.Join("&", parameters.Select(p => $"{p.Name}={Uri.EscapeDataString(p.Value)}"));

    private static string Invariant(int value) => value.ToString(CultureInfo.InvariantCulture);

    private static string? PlainText(string body) => string.IsNullOrWhiteSpace(body) ? null : body.Trim()[..Math.Min(body.Trim().Length, 200)];

    private static string? String(JsonElement row, string name) =>
        TryGet(row, name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static decimal? Number(JsonElement row, string name) =>
        TryGet(row, name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d) && double.IsFinite(d)
            ? EdsmAmountParser.RoundRaw(d) : null;

    private static int? Int(JsonElement row, string name) =>
        TryGet(row, name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : null;

    private static long? Long(JsonElement row, string name) =>
        TryGet(row, name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var l) ? l : null;

    private static bool TryGet(JsonElement element, string name, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
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
}

/// <summary>Every real environment until <c>CollectionsSource:EdsmProvider</c> is "Pact".</summary>
public sealed class UnavailableEdsmCollectionsGateway : IEdsmCollectionsGateway
{
    private const string Message = "EDSM is not enabled in this environment (CollectionsSource:EdsmProvider).";

    public string SourceName => "Unavailable";

    public Task<EdsmResult<EdsmPaymentSummary>> GetPaymentSummaryAsync(int companyId, string tenantId, CancellationToken cancellationToken = default) =>
        Task.FromResult(EdsmResult<EdsmPaymentSummary>.Fail(EdsmOutcome.Unavailable, Message));

    public Task<EdsmResult<EdsmPaymentTransactions>> GetPaymentTransactionsAsync(
        int companyId, string tenantId, string mobile, EdsmTransactionType type, CancellationToken cancellationToken = default) =>
        Task.FromResult(EdsmResult<EdsmPaymentTransactions>.Fail(EdsmOutcome.Unavailable, Message));

    public Task<EdsmResult<IReadOnlyList<EdsmDueInstallment>>> GetDueInstallmentsAsync(
        int companyId, DateOnly fromDate, DateOnly toDate, CancellationToken cancellationToken = default) =>
        Task.FromResult(EdsmResult<IReadOnlyList<EdsmDueInstallment>>.Fail(EdsmOutcome.Unavailable, Message));
}

/// <summary>
/// Development/Testing only (refused elsewhere by <see cref="CollectionsSourceSafety"/>).
/// Bodies are in EDSM's exact wire shape and go through the same
/// <see cref="EdsmCollectionsHttpGateway.Interpret{T}"/> path as real ones.
/// Tenant 3001 is <c>MockPactGateway</c>'s customer: company 4 (owned) and 25 (rented). Not EDSM data.
/// </summary>
public sealed class FixtureEdsmCollectionsGateway(IOptions<CollectionsEdsmOptions> edsmOptions) : IEdsmCollectionsGateway
{
    private static readonly IReadOnlyDictionary<(int, string), string> Summaries = new Dictionary<(int, string), string>
    {
        [(4, "3001")] = """{"title":"","status":200,"data":{"totalAmount":"1,250,000.00","paidAmount":"812,500.00","dueAmount":"62,500.00","outstandingAmount":"375,000.00","lateFines":"1,500.00"}}""",
        [(25, "3001")] = """{"title":"","status":200,"data":{"totalAmount":"84,500.00","paidAmount":"60,000.00","dueAmount":"-500.00","outstandingAmount":"25,000.00","lateFines":""}}"""
    };

    private static readonly IReadOnlyDictionary<(int, string, int), string> Transactions = new Dictionary<(int, string, int), string>
    {
        [(4, "3001", 1)] = """{"title":"","status":200,"data":{"totalAmount":812500.0,"formattedTotalAmount":"812,500.00","transactions":[{"amount":500000.0,"formattedAmount":"500,000.00","date":"15-Jan-2026","chequeNumber":null,"transactionTypeId":1,"paymentTypeId":null},{"amount":312500.0,"formattedAmount":"312,500.00","date":"15-Jun-2026","chequeNumber":null,"transactionTypeId":1,"paymentTypeId":null}]}}""",
        [(4, "3001", 2)] = """{"title":"","status":200,"data":{"totalAmount":62500.0,"formattedTotalAmount":"62,500.00","transactions":[{"amount":62500.0,"formattedAmount":"62,500.00","date":"15-Sep-2026","chequeNumber":"000412","transactionTypeId":2,"paymentTypeId":null}]}}""",
        [(4, "3001", 3)] = """{"title":"","status":200,"data":{"totalAmount":375000.0,"formattedTotalAmount":"375,000.00","transactions":[{"amount":187500.0,"formattedAmount":"187,500.00","date":"15-Dec-2026","chequeNumber":null,"transactionTypeId":3,"paymentTypeId":null},{"amount":187500.0,"formattedAmount":"187,500.00","date":"15-Mar-2027","chequeNumber":null,"transactionTypeId":3,"paymentTypeId":null}]}}""",
        [(25, "3001", 1)] = """{"title":"","status":200,"data":{"totalAmount":60000.0,"formattedTotalAmount":"60,000.00","transactions":[{"amount":60000.0,"formattedAmount":"60,000.00","date":"01-Feb-2026","chequeNumber":"100201","transactionTypeId":1,"paymentTypeId":2}]}}""",
        [(25, "3001", 2)] = """{"title":"","status":200,"data":{"totalAmount":-500.0,"formattedTotalAmount":"-500.00","transactions":[{"amount":1000.0,"formattedAmount":"1,000.00 AED","date":"01-Mar-2026","chequeNumber":null,"transactionTypeId":2,"paymentTypeId":3}]}}""",
        [(25, "3001", 3)] = """{"title":"","status":200,"data":{"totalAmount":25000.0,"formattedTotalAmount":"25,000.00","transactions":[{"amount":25000.0,"formattedAmount":"25,000.00","date":"01-Dec-2026","chequeNumber":"100205","transactionTypeId":3,"paymentTypeId":2}]}}""",
    };

    private static readonly IReadOnlyDictionary<int, string> DueInstallments = new Dictionary<int, string>
    {
        [4] = """{"title":"","status":200,"data":[{"companyID":4,"tenantID":3001,"unitID":41230,"voucherNumber":"PDC-0412 ","chequeNumber":"000412","chequeDueDate":"2026-09-15T00:00:00","amount":62500.0,"status":"Due "},{"companyID":4,"tenantID":3001,"unitID":41230,"voucherNumber":"PDC-0413","chequeNumber":null,"chequeDueDate":"2026-12-15T00:00:00","amount":187500.00000000003,"status":"PDC"},{"companyID":4,"tenantID":9999,"unitID":50000,"voucherNumber":"PDC-9999","chequeNumber":"9","chequeDueDate":"2026-10-01T00:00:00","amount":1.0,"status":"Due"}]}""",
        [25] = """{"title":"","status":200,"data":[{"companyID":25,"tenantID":3001,"unitID":51200,"voucherNumber":"RV-100205","chequeNumber":"100205","chequeDueDate":"2026-12-01T00:00:00","amount":25000.0,"status":"PDC"}]}"""
    };

    public string SourceName => "Fixture";

    private CultureInfo? Culture => EdsmAmountParser.ResolveCulture(edsmOptions.Value.EdsmNumberCulture);

    public Task<EdsmResult<EdsmPaymentSummary>> GetPaymentSummaryAsync(int companyId, string tenantId, CancellationToken cancellationToken = default)
    {
        // EDSM answers an unknown tenant with 200 and zeros — reproduced here.
        var body = Summaries.TryGetValue((companyId, tenantId), out var known) ? known
            : """{"title":"","status":200,"data":{"totalAmount":"0.00","paidAmount":"0.00","dueAmount":"0.00","outstandingAmount":"0.00","lateFines":""}}""";
        var culture = Culture;
        return Task.FromResult(EdsmCompanies.Find(companyId) is null
            ? EdsmResult<EdsmPaymentSummary>.Fail(EdsmOutcome.NotSupported, $"EDSM does not support company {companyId}.")
            : EdsmCollectionsHttpGateway.Interpret(200, body, d => EdsmCollectionsHttpGateway.ReadSummary(d, culture)));
    }

    public Task<EdsmResult<EdsmPaymentTransactions>> GetPaymentTransactionsAsync(
        int companyId, string tenantId, string mobile, EdsmTransactionType type, CancellationToken cancellationToken = default)
    {
        var body = Transactions.TryGetValue((companyId, tenantId, (int)type), out var known) ? known
            : """{"title":"","status":200,"data":{"totalAmount":0.0,"formattedTotalAmount":"","transactions":[]}}""";
        var culture = Culture;
        var aed = EdsmCompanies.Find(companyId)?.Model == EdsmBusinessModel.Rented && type == EdsmTransactionType.Due;
        return Task.FromResult(EdsmCollectionsHttpGateway.Interpret(200, body, d => EdsmCollectionsHttpGateway.ReadTransactions(d, culture, aed)));
    }

    public Task<EdsmResult<IReadOnlyList<EdsmDueInstallment>>> GetDueInstallmentsAsync(
        int companyId, DateOnly fromDate, DateOnly toDate, CancellationToken cancellationToken = default)
    {
        if (EdsmCompanies.Find(companyId) is not { SupportsDueInstallments: true })
        {
            return Task.FromResult(EdsmResult<IReadOnlyList<EdsmDueInstallment>>.Fail(EdsmOutcome.NotSupported,
                $"EDSM due-installments does not support company {companyId}."));
        }

        var body = DueInstallments.TryGetValue(companyId, out var known) ? known : """{"title":"","status":200,"data":[]}""";
        return Task.FromResult(EdsmCollectionsHttpGateway.Interpret(200, body, EdsmCollectionsHttpGateway.ReadDueInstallments));
    }
}

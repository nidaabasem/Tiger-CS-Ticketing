using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using System.Linq;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Tiger.TigerGroupWeb.Genesys;
using Tiger.TigerGroupWeb.Genesys.Auth;

namespace Tiger.TigerGroupWeb.Controllers
{
    // =========================================================
    // Every endpoint requires a Bearer access token issued by
    // POST /api/genesys/oauth/token with scope ticketing.genesys.
    // =========================================================
    [ApiController]
    [Route("api/genesys")]
    [Authorize(Policy = GenesysAuthDefaults.PolicyName)]
    public class GenesysController : ControllerBase
    {
        private readonly TicketingGenesysService _ticketingService;
        private readonly TicketingOptions _ticketingOptions;
        private readonly ILogger<GenesysController> _logger;

        public GenesysController(
            TicketingGenesysService ticketingService,
            IOptions<TicketingOptions> ticketingOptions,
            ILogger<GenesysController> logger)
        {
            _ticketingService = ticketingService;
            _ticketingOptions = ticketingOptions.Value;
            _logger = logger;
        }

        // =========================================================
        // 1. CUSTOMER LOOKUP
        //
        // GET:
        // /api/genesys/customers/lookup?phoneNumber=+971...
        // =========================================================
        [HttpGet("customers/lookup")]
        public async Task<IActionResult> CustomerLookup(
            [FromQuery] string phoneNumber)
        {
            if (string.IsNullOrWhiteSpace(phoneNumber))
            {
                return BadRequest(new
                {
                    message = "phoneNumber is required."
                });
            }

            try
            {
                using var response =
                    await _ticketingService.CustomerLookupAsync(
                        phoneNumber);

                var body =
                    await response.Content.ReadAsStringAsync();

                return new ContentResult
                {
                    StatusCode = (int)response.StatusCode,
                    Content = body,
                    ContentType =
                        response.Content.Headers.ContentType?.ToString()
                        ?? "application/json"
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Genesys customer lookup failed while communicating with TigerCS Ticketing.");

                return StatusCode(
                    StatusCodes.Status502BadGateway,
                    new
                    {
                        message =
                            "Unable to communicate with TigerCS Ticketing."
                    });
            }
        }

        // =========================================================
        // 2. CREATE / REUSE TICKET
        //
        // POST:
        // /api/genesys/tickets
        // =========================================================
        [HttpPost("tickets")]
        public Task<IActionResult> CreateTicket(
            [FromBody] JsonElement body,
            CancellationToken cancellationToken)
        {
            // GetRawText() is the request body exactly as received, so
            // requestType, transcript and every other field reach TigerCS
            // untouched; the answer (status, bytes, content type,
            // ProblemDetails, Retry-After) comes back as TigerCS gave it.
            var requestJson = body.GetRawText();

            return ForwardTicketJson(
                "ticket creation",
                ticketId: null,
                token => _ticketingService.CreateTicketAsync(requestJson, token),
                cancellationToken);
        }

        // =========================================================
        // 3. UPDATE TICKET / CONVERSATION
        //
        // PATCH:
        // /api/genesys/tickets/{ticketId}
        // =========================================================
        [HttpPatch("tickets/{ticketId:long}")]
        public Task<IActionResult> UpdateTicket(
            long ticketId,
            [FromBody] JsonElement body,
            CancellationToken cancellationToken)
        {
            var requestJson = body.GetRawText();

            return ForwardTicketJson(
                "ticket update",
                ticketId,
                token => _ticketingService.UpdateTicketAsync(ticketId, requestJson, token),
                cancellationToken);
        }

        // Shared by ticket creation and update. The caller's disconnect and
        // the forwarding budget both cancel the call; a timeout or network
        // failure is NEVER retried here (the outcome is unknown - TigerCS
        // is idempotent per conversationId, so the CALLER may resend the
        // identical request). Nothing from the request or the response body
        // is logged: only the operation, the status and the elapsed time.
        private async Task<IActionResult> ForwardTicketJson(
            string operation,
            long? ticketId,
            Func<CancellationToken, Task<HttpResponseMessage>> forward,
            CancellationToken cancellationToken)
        {
            Response.Headers["Cache-Control"] = "no-store";

            var clock = Stopwatch.StartNew();

            try
            {
                using var response = await forward(cancellationToken);

                _logger.LogInformation(
                    "Genesys {Operation} (TicketId {TicketId}) answered by TigerCS Ticketing: {StatusCode} in {ElapsedMs} ms.",
                    operation, ticketId, (int)response.StatusCode, clock.ElapsedMilliseconds);

                return await RelayAsync(response, cancellationToken);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning(
                    "Genesys {Operation} (TicketId {TicketId}) timed out waiting for TigerCS Ticketing after {ElapsedMs} ms.",
                    operation, ticketId, clock.ElapsedMilliseconds);

                return StatusCode(
                    StatusCodes.Status504GatewayTimeout,
                    new
                    {
                        message =
                            "TigerCS Ticketing did not respond in time. " +
                            "The result is unknown; resend the identical request - it is idempotent."
                    });
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                // Exception type only: messages can carry URLs or credentials.
                _logger.LogError(
                    "Genesys {Operation} (TicketId {TicketId}) failed while communicating with TigerCS Ticketing ({ErrorType}).",
                    operation, ticketId, ex.GetType().Name);

                return StatusCode(
                    StatusCodes.Status502BadGateway,
                    new
                    {
                        message =
                            "Unable to communicate with TigerCS Ticketing."
                    });
            }
        }

        // =========================================================
        // 4. SECURE SCREEN POP
        //
        // POST:
        // /api/genesys/screen-pop
        //
        // Returns TigerCS's launchUrl unchanged; Genesys opens it.
        // Works before a ticket exists (customerPhone only).
        // =========================================================
        [HttpPost("screen-pop")]
        public async Task<IActionResult> ScreenPop(
            [FromBody] JsonElement body)
        {
            if (body.ValueKind != JsonValueKind.Object ||
                !body.TryGetProperty("genesysUserId", out var genesysUserId) ||
                genesysUserId.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(genesysUserId.GetString()))
            {
                return BadRequest(new
                {
                    message = "genesysUserId is required."
                });
            }

            try
            {
                var requestJson =
                    body.GetRawText();

                using var response =
                    await _ticketingService.ScreenPopAsync(
                        requestJson);

                var responseBody =
                    await response.Content.ReadAsStringAsync();

                return new ContentResult
                {
                    StatusCode = (int)response.StatusCode,
                    Content = responseBody,
                    ContentType =
                        response.Content.Headers.ContentType?.ToString()
                        ?? "application/json"
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Genesys screen pop failed while communicating with TigerCS Ticketing.");

                return StatusCode(
                    StatusCodes.Status502BadGateway,
                    new
                    {
                        message =
                            "Unable to communicate with TigerCS Ticketing."
                    });
            }
        }

        // Public Genesys route -> TigerCS /api/collections/units/payment-summary.
        // Bearer authorization is inherited from this controller.
        [HttpGet("collections/units/payment-summary")]
        public async Task<IActionResult> UnitPaymentSummary(
            [FromQuery] string? unitCode,
            [FromQuery] int? companyId,
            CancellationToken cancellationToken)
        {
            Response.Headers["Cache-Control"] = "no-store";

            if (string.IsNullOrWhiteSpace(unitCode) ||
                Request.Query["unitCode"].Count != 1)
            {
                return BadRequest(new
                {
                    message = "Exactly one non-empty unitCode is required."
                });
            }

            if (Request.Query["companyId"].Count > 1 ||
                (Request.Query.ContainsKey("companyId") && !companyId.HasValue) ||
                (companyId.HasValue && companyId.Value != 4 && companyId.Value != 32))
            {
                return BadRequest(new
                {
                    message = "companyId must be a single value: 4 or 32, or omitted."
                });
            }

            try
            {
                using var response = await _ticketingService.CollectionsUnitPaymentSummaryAsync(
                    unitCode.Trim(), companyId, CollectionsBudget(), cancellationToken);
                return await RelayAsync(response, cancellationToken);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning("Genesys unit payment summary timed out waiting for TigerCS Ticketing.");
                return StatusCode(StatusCodes.Status504GatewayTimeout, new
                {
                    message = "TigerCS Ticketing did not respond in time."
                });
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                _logger.LogError(
                    "Genesys unit payment summary failed while communicating with TigerCS Ticketing ({ErrorType}).",
                    ex.GetType().Name);
                return StatusCode(StatusCodes.Status502BadGateway, new
                {
                    message = "Unable to communicate with TigerCS Ticketing."
                });
            }
        }

        // Simplified PACT payment routes: Genesys supplies only tenantId.
        [HttpGet("collections/payment-summary")]
        public Task<IActionResult> PaymentSummaryByTenant(
            [FromQuery] string tenantId,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(tenantId) ||
                Request.Query["tenantId"].Count != 1)
            {
                return Task.FromResult<IActionResult>(BadRequest(new
                {
                    message = "Exactly one non-empty tenantId is required."
                }));
            }

            return ForwardCollectionsRead(
                $"ext:Pact:{tenantId.Trim()}",
                "payment summary",
                (key, query, budget) => _ticketingService.CollectionsPaymentSummaryAsync(
                    key, query, budget, cancellationToken),
                cancellationToken,
                GetPaymentQueryString());
        }

        [HttpGet("collections/payment-transactions")]
        public Task<IActionResult> PaymentTransactionsByTenant(
            [FromQuery] string tenantId,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(tenantId) ||
                Request.Query["tenantId"].Count != 1)
            {
                return Task.FromResult<IActionResult>(BadRequest(new
                {
                    message = "Exactly one non-empty tenantId is required."
                }));
            }

            return ForwardCollectionsRead(
                $"ext:Pact:{tenantId.Trim()}",
                "payment transactions",
                (key, query, budget) => _ticketingService.CollectionsPaymentTransactionsAsync(
                    key, query, budget, cancellationToken),
                cancellationToken,
                GetPaymentQueryString());
        }

        private string GetPaymentQueryString()
        {
            // tenantId is already part of the internally constructed customer key.
            return QueryString.Create(Request.Query.Where(parameter =>
                !string.Equals(parameter.Key, "tenantId", StringComparison.OrdinalIgnoreCase)))
                .Value ?? string.Empty;
        }

        // =========================================================
        // 5. COLLECTIONS: PAYMENT SUMMARY
        //
        // GET:
        // /api/genesys/collections/customers/by-key/{customerKey}/payment-summary
        //
        // customerKey (e.g. ext%3APact%3A3001), the query string,
        // status code and body pass through unchanged. Amounts are
        // TigerCS's/EDSM's; nothing is calculated here.
        // =========================================================
        [HttpGet("collections/customers/by-key/{customerKey}/payment-summary")]
        public Task<IActionResult> CollectionsPaymentSummary(
            string customerKey,
            CancellationToken cancellationToken)
        {
            return ForwardCollectionsRead(
                customerKey,
                "payment summary",
                (key, query, budget) => _ticketingService.CollectionsPaymentSummaryAsync(
                    key, query, budget, cancellationToken),
                cancellationToken);
        }

        // =========================================================
        // 6. COLLECTIONS: PAYMENT TRANSACTIONS
        //
        // GET:
        // /api/genesys/collections/customers/by-key/{customerKey}/payment-transactions?companyId=..&type=..
        //
        // Query validation (companyId, type, Type All refused) is
        // TigerCS's; its 400 is passed through.
        // =========================================================
        [HttpGet("collections/customers/by-key/{customerKey}/payment-transactions")]
        public Task<IActionResult> CollectionsPaymentTransactions(
            string customerKey,
            CancellationToken cancellationToken)
        {
            return ForwardCollectionsRead(
                customerKey,
                "payment transactions",
                (key, query, budget) => _ticketingService.CollectionsPaymentTransactionsAsync(
                    key, query, budget, cancellationToken),
                cancellationToken);
        }

        // =========================================================
        // 5b. COLLECTIONS: OTHER READS (GET)
        //
        // /api/genesys/collections/customers/{crmCustomerId}/outstanding
        // /api/genesys/collections/customers/{crmCustomerId}/payments
        // /api/genesys/collections/customers/{crmCustomerId}/reminders
        // /api/genesys/collections/reminders/candidates
        //
        // Same budget, query and pass-through as the reads above.
        // =========================================================
        [HttpGet("collections/customers/{crmCustomerId}/outstanding")]
        public Task<IActionResult> CollectionsOutstanding(
            string crmCustomerId,
            CancellationToken cancellationToken)
        {
            return ForwardCollectionsCustomerRead(
                crmCustomerId, "outstanding", cancellationToken);
        }

        [HttpGet("collections/customers/{crmCustomerId}/payments")]
        public Task<IActionResult> CollectionsPayments(
            string crmCustomerId,
            CancellationToken cancellationToken)
        {
            return ForwardCollectionsCustomerRead(
                crmCustomerId, "payments", cancellationToken);
        }

        [HttpGet("collections/customers/{crmCustomerId}/reminders")]
        public Task<IActionResult> CollectionsCustomerReminders(
            string crmCustomerId,
            CancellationToken cancellationToken)
        {
            return ForwardCollectionsCustomerRead(
                crmCustomerId, "reminders", cancellationToken);
        }

        [HttpGet("collections/reminders/candidates")]
        public Task<IActionResult> CollectionsReminderCandidates(
            CancellationToken cancellationToken)
        {
            return ForwardCollectionsRead(
                "reminders/candidates",
                "reminder candidates",
                cancellationToken);
        }

        // =========================================================
        // 5c. COLLECTIONS: REMINDERS (POST)
        //
        // /api/genesys/collections/reminders
        // /api/genesys/collections/reminders/{reminderId}/outcomes
        //
        // The Idempotency-Key is forwarded untouched. TigerCS decides
        // whether Collections is enabled and what the account may do.
        // =========================================================
        [HttpPost("collections/reminders")]
        public Task<IActionResult> QueueCollectionsReminder(
            [FromBody] JsonElement body,
            CancellationToken cancellationToken)
        {
            return ForwardPost(
                body, "collections reminder",
                _ticketingService.QueueCollectionsReminderAsync,
                cancellationToken);
        }

        [HttpPost("collections/reminders/{reminderId}/outcomes")]
        public Task<IActionResult> RecordCollectionsReminderOutcome(
            string reminderId,
            [FromBody] JsonElement body,
            CancellationToken cancellationToken)
        {
            var encodedReminderId = EncodePathSegment(reminderId);

            if (encodedReminderId is null)
            {
                return Task.FromResult<IActionResult>(BadRequest(new
                {
                    message = "reminderId is invalid."
                }));
            }

            return ForwardPost(
                body, "collections reminder outcome",
                (json, key, token) => _ticketingService.RecordCollectionsReminderOutcomeAsync(
                    encodedReminderId, json, key, token),
                cancellationToken);
        }

        private Task<IActionResult> ForwardCollectionsCustomerRead(
            string crmCustomerId,
            string leaf,
            CancellationToken cancellationToken)
        {
            // CRM customer ids are positive integers; anything else could
            // only change the downstream path.
            if (crmCustomerId.Length == 0 ||
                !crmCustomerId.All(char.IsAsciiDigit))
            {
                return Task.FromResult<IActionResult>(BadRequest(new
                {
                    message = "crmCustomerId must be a positive CRM customer id."
                }));
            }

            return ForwardCollectionsRead(
                $"customers/{crmCustomerId}/{leaf}",
                $"customer {leaf}",
                cancellationToken);
        }

        private async Task<IActionResult> ForwardCollectionsRead(
            string pathAfterCollections,
            string operation,
            CancellationToken cancellationToken)
        {
            try
            {
                using var response =
                    await _ticketingService.CollectionsReadAsync(
                        pathAfterCollections,
                        Request.QueryString.Value ?? string.Empty,
                        CollectionsBudget(),
                        cancellationToken);

                return await RelayAsync(response, cancellationToken);
            }
            catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
            {
                _logger.LogError(
                    ex,
                    "Genesys collections {Operation} timed out waiting for TigerCS Ticketing.",
                    operation);

                return StatusCode(
                    StatusCodes.Status504GatewayTimeout,
                    new
                    {
                        message =
                            "TigerCS Ticketing did not respond in time."
                    });
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                _logger.LogError(
                    ex,
                    "Genesys collections {Operation} failed while communicating with TigerCS Ticketing.",
                    operation);

                return StatusCode(
                    StatusCodes.Status502BadGateway,
                    new
                    {
                        message =
                            "Unable to communicate with TigerCS Ticketing."
                    });
            }
        }

        // Status, body bytes, Content-Type and Retry-After are TigerCS's.
        private async Task<IActionResult> RelayAsync(
            HttpResponseMessage response,
            CancellationToken cancellationToken)
        {
            var responseBody =
                await response.Content.ReadAsByteArrayAsync(cancellationToken);

            // Rate-limit answers must retain their wait time.
            if (response.Headers.TryGetValues("Retry-After", out var retryAfter))
            {
                Response.Headers["Retry-After"] = string.Join(", ", retryAfter);
            }

            Response.StatusCode = (int)response.StatusCode;

            // Preserve TigerCS's bytes and media type, including
            // ProblemDetails and selection/session fields.
            return File(
                responseBody,
                response.Content.Headers.ContentType?.ToString()
                    ?? "application/json");
        }

        // A value that could change the downstream path is refused.
        private static string? EncodePathSegment(string value)
        {
            var decoded = Uri.UnescapeDataString(value);

            if (decoded.Length == 0 ||
                decoded == "." ||
                decoded == ".." ||
                decoded.IndexOfAny(new[] { '/', '\\', '?', '#' }) >= 0)
            {
                return null;
            }

            return Uri.EscapeDataString(decoded);
        }

        private async Task<IActionResult> ForwardCollectionsRead(
            string customerKey,
            string operation,
            Func<string, string, TimeSpan, Task<HttpResponseMessage>> forward,
            CancellationToken cancellationToken,
            string? queryOverride = null)
        {
            var encodedCustomerKey =
                GetEncodedCustomerKey(customerKey);

            if (encodedCustomerKey is null)
            {
                return BadRequest(new
                {
                    message = "customerKey is invalid."
                });
            }

            try
            {
                using var response =
                    await forward(
                        encodedCustomerKey,
                        queryOverride ?? Request.QueryString.Value ?? string.Empty,
                        CollectionsBudget());

                return await RelayAsync(response, cancellationToken);
            }
            catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
            {
                _logger.LogError(
                    ex,
                    "Genesys collections {Operation} timed out waiting for TigerCS Ticketing.",
                    operation);

                return StatusCode(
                    StatusCodes.Status504GatewayTimeout,
                    new
                    {
                        message =
                            "TigerCS Ticketing did not respond in time."
                    });
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                _logger.LogError(
                    ex,
                    "Genesys collections {Operation} failed while communicating with TigerCS Ticketing.",
                    operation);

                return StatusCode(
                    StatusCodes.Status502BadGateway,
                    new
                    {
                        message =
                            "Unable to communicate with TigerCS Ticketing."
                    });
            }
        }

        // Shorter when the Genesys data action sends its flow's
        // timeout, so TigerGroupWeb answers before Genesys gives up.
        private TimeSpan CollectionsBudget()
        {
            int? flowTimeoutSeconds =
                Request.Headers.TryGetValue(TicketingOptions.FlowTimeoutHeader, out var values) &&
                int.TryParse(values.ToString(), NumberStyles.None, CultureInfo.InvariantCulture, out var seconds)
                    ? seconds
                    : null;

            return _ticketingOptions.CollectionsBudget(flowTimeoutSeconds);
        }

        // The server percent-decodes route values (ext%3APact%3A3001
        // becomes ext:Pact:3001), so the segment is taken from the raw
        // request target to forward it byte-for-byte. Returns null for a
        // key that could change the downstream path.
        private string? GetEncodedCustomerKey(string customerKey)
        {
            var decodedKey =
                Uri.UnescapeDataString(customerKey);

            if (decodedKey.Length == 0 ||
                decodedKey == "." ||
                decodedKey == ".." ||
                decodedKey.IndexOfAny(new[] { '/', '\\', '?', '#' }) >= 0)
            {
                return null;
            }

            var rawTarget =
                HttpContext.Features.Get<IHttpRequestFeature>()?.RawTarget;

            const string marker = "/by-key/";

            if (!string.IsNullOrEmpty(rawTarget))
            {
                var path = rawTarget.Split('?', 2)[0];
                var start = path.IndexOf(marker, StringComparison.OrdinalIgnoreCase);

                if (start >= 0)
                {
                    start += marker.Length;
                    var end = path.IndexOf('/', start);
                    var rawSegment = end < 0 ? path[start..] : path[start..end];

                    if (Uri.UnescapeDataString(rawSegment) == decodedKey)
                    {
                        return rawSegment;
                    }
                }
            }

            return Uri.EscapeDataString(decodedKey);
        }

        // =========================================================
        // 7. AGENT CONTEXT AND UNIT DETAILS
        //
        // POST:
        // /api/genesys/agent-context
        // /api/genesys/customers/unit-details
        //
        // TigerCS owns the contract (field names, the agent mapping,
        // customer verification and the unit-selection modes); the
        // body and the answer pass through unchanged.
        // =========================================================
        [HttpPost("agent-context")]
        public Task<IActionResult> AgentContext(
            [FromBody] JsonElement body,
            CancellationToken cancellationToken)
        {
            return ForwardPost(
                body, "agent context", _ticketingService.AgentContextAsync,
                cancellationToken);
        }

        [HttpPost("customers/unit-details")]
        public Task<IActionResult> UnitDetails(
            [FromBody] JsonElement body,
            CancellationToken cancellationToken)
        {
            return ForwardPost(
                body, "unit details", _ticketingService.UnitDetailsAsync,
                cancellationToken);
        }

        // =========================================================
        // 8. CRM BUYER VERIFICATION AND DOCUMENT COPIES
        //
        // Public routes stay on TigerWebsite. TigerCS performs all
        // CRM lookups, OTP checks, ownership checks and email delivery.
        // =========================================================
        [HttpPost("verification/buyer-lookup")]
        public Task<IActionResult> BuyerLookup(
            [FromBody] JsonElement body,
            CancellationToken cancellationToken)
        {
            return ForwardPost(
                body, "buyer lookup", _ticketingService.BuyerLookupAsync,
                cancellationToken);
        }

        [HttpPost("verification/otp/send")]
        public Task<IActionResult> SendOtp(
            [FromBody] JsonElement body,
            CancellationToken cancellationToken)
        {
            return ForwardPost(
                body, "OTP send", _ticketingService.SendOtpAsync,
                cancellationToken);
        }

        [HttpPost("verification/otp/resend")]
        public Task<IActionResult> ResendOtp(
            [FromBody] JsonElement body,
            CancellationToken cancellationToken)
        {
            return ForwardPost(
                body, "OTP resend", _ticketingService.ResendOtpAsync,
                cancellationToken);
        }

        [HttpPost("verification/otp/verify")]
        public Task<IActionResult> VerifyOtp(
            [FromBody] JsonElement body,
            CancellationToken cancellationToken)
        {
            return ForwardPost(
                body, "OTP verify", _ticketingService.VerifyOtpAsync,
                cancellationToken);
        }

        [HttpPost("documents/send-copy")]
        public Task<IActionResult> SendDocumentCopy(
            [FromBody] JsonElement body,
            CancellationToken cancellationToken)
        {
            return ForwardPost(
                body, "document copy", _ticketingService.SendDocumentCopyAsync,
                cancellationToken);
        }

        // Shared by the POST routes above that must not be replayed
        // blindly: no automatic retry after a timeout or network failure
        // (the outcome is unknown), the caller's Idempotency-Key is passed
        // through untouched, and TigerCS's status, bytes, Content-Type and
        // Retry-After come back as they are.
        private async Task<IActionResult> ForwardPost(
            JsonElement body,
            string operation,
            Func<string, string?, CancellationToken, Task<HttpResponseMessage>> forward,
            CancellationToken cancellationToken)
        {
            Response.Headers["Cache-Control"] = "no-store";
            Response.Headers["Pragma"] = "no-cache";

            if (body.ValueKind != JsonValueKind.Object)
            {
                return BadRequest(new { message = "A JSON object is required." });
            }

            string? idempotencyKey = null;
            if (Request.Headers.TryGetValue("Idempotency-Key", out var keys))
            {
                if (keys.Count != 1)
                {
                    return BadRequest(new
                    {
                        message = "Send exactly one Idempotency-Key header."
                    });
                }

                // Never generate, trim or replace the caller's key.
                // TigerCS validates it (and requires it for send-copy).
                idempotencyKey = keys[0];
            }

            try
            {
                using var response = await forward(
                    body.GetRawText(), idempotencyKey, cancellationToken);

                return await RelayAsync(response, cancellationToken);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning(
                    "Genesys {Operation} timed out waiting for TigerCS Ticketing.",
                    operation);

                return StatusCode(StatusCodes.Status504GatewayTimeout, new
                {
                    message = "TigerCS Ticketing did not respond in time. " +
                        "The result is unknown; retry with the same Idempotency-Key."
                });
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                // Do not log bodies, OTPs, destinations or tokens.
                _logger.LogError(
                    "Genesys {Operation} failed while communicating with TigerCS Ticketing ({ErrorType}).",
                    operation, ex.GetType().Name);

                return StatusCode(StatusCodes.Status502BadGateway, new
                {
                    message = "Unable to communicate with TigerCS Ticketing. " +
                        "Retry with the same Idempotency-Key."
                });
            }
        }
    }
}
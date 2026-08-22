using System.Net;
using TigerCS.Application.Modules.CustomerVerification.Dto;
using TigerCS.Domain.Modules.IdentityAndAccess;

namespace TigerCS.Tests.Web;

/// <summary>
/// The new-ticket wizard, end to end against a scripted API: the verified
/// happy path, ISSUE-006's two CRM-outage outcomes, and duplicate-submission
/// protection.
/// </summary>
public sealed class NewTicketWizardTests
{
    private static TigerCsWebFactory SignedIn()
    {
        var factory = new TigerCsWebFactory();
        factory.Api
            .On(HttpMethod.Post, "/api/auth/login", HttpStatusCode.OK, TestData.Login(Roles.CsAgent))
            .On(HttpMethod.Get, "/api/users/me", HttpStatusCode.OK, TestData.Profile(Roles.CsAgent));

        return factory;
    }

    private static async Task<HttpClient> SignInAsync(TigerCsWebFactory factory)
    {
        var client = factory.CreateWebClient();
        await WebTestHelpers.SignInAsync(client, "agent", "Test-Password-1!");
        return client;
    }

    /// <summary>Runs steps 1-3 and leaves the wizard on the review step.</summary>
    private static async Task<HttpClient> WalkToReviewAsync(TigerCsWebFactory factory)
    {
        var client = await SignInAsync(factory);

        var intake = await WebTestHelpers.PostFormAsync(
            client, "/Tickets/New/Intake", "/Tickets/New/Intake",
            new KeyValuePair<string, string>("RawUnitNumberEntered", "A-1402"),
            new KeyValuePair<string, string>("PriorityHint", "3"));
        WebTestHelpers.AssertRedirectTo(intake, "/Tickets/New/Unit");

        var selectUnit = await WebTestHelpers.PostFormAsync(
            client, "/Tickets/New/Unit", "/Tickets/New/Unit?handler=Select",
            new KeyValuePair<string, string>("unitReferenceId", "42"),
            new KeyValuePair<string, string>("crmUnitId", "CRM-UNIT-42"),
            new KeyValuePair<string, string>("unitLabel", "A-1402 · Tiger Tower"));
        WebTestHelpers.AssertRedirectTo(selectUnit, "/Tickets/New/Contact");

        var confirm = await WebTestHelpers.PostFormAsync(
            client, "/Tickets/New/Contact", "/Tickets/New/Contact?handler=Confirm",
            new KeyValuePair<string, string>("contactReferenceId", "84"),
            new KeyValuePair<string, string>("confirmed", "true"),
            new KeyValuePair<string, string>("contactLabel", "Mona Haddad (Owner)"));
        WebTestHelpers.AssertRedirectTo(confirm, "/Tickets/New/Review");

        return client;
    }

    private static void ScriptVerifiedPath(TigerCsWebFactory factory)
    {
        factory.Api
            .On(HttpMethod.Post, "/api/intake-records", HttpStatusCode.Created, TestData.Intake())
            .On(HttpMethod.Get, "/api/crm/units/search", HttpStatusCode.OK, new[] { TestData.Unit() })
            .On(HttpMethod.Get, "/api/crm/units/CRM-UNIT-42/contacts", HttpStatusCode.OK,
                new[] { TestData.Contact() })
            .On(HttpMethod.Post, "/api/verification-sessions", HttpStatusCode.Created, TestData.Session());
    }

    // ------------------------------------------------------------ happy path

    [Fact]
    public async Task Wizard_HappyPath_CreatesATicketAndShowsItsRealNumber()
    {
        using var factory = SignedIn();
        ScriptVerifiedPath(factory);
        factory.Api.On(HttpMethod.Post, "/api/tickets", HttpStatusCode.Created, TestData.Created());

        var client = await WalkToReviewAsync(factory);

        var response = await WebTestHelpers.PostFormAsync(
            client, "/Tickets/New/Review", "/Tickets/New/Review",
            new KeyValuePair<string, string>("CategoryId", "7"),
            new KeyValuePair<string, string>("PriorityId", "3"),
            new KeyValuePair<string, string>("RequestSummary", "Air conditioning in the main hall is not cooling."));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await WebTestHelpers.AssertContainsAsync(response, "Ticket created");

        // The number is the API's, not one this tier invented or predicted.
        await WebTestHelpers.AssertContainsAsync(response, "CS-2026-0777");
    }

    [Fact]
    public async Task Wizard_FollowsTheApiWorkflowInOrder()
    {
        using var factory = SignedIn();
        ScriptVerifiedPath(factory);
        factory.Api.On(HttpMethod.Post, "/api/tickets", HttpStatusCode.Created, TestData.Created());

        var client = await WalkToReviewAsync(factory);
        await WebTestHelpers.PostFormAsync(
            client, "/Tickets/New/Review", "/Tickets/New/Review",
            new KeyValuePair<string, string>("CategoryId", "7"),
            new KeyValuePair<string, string>("PriorityId", "3"),
            new KeyValuePair<string, string>("RequestSummary", "Not cooling."));

        var order = factory.Api.Requests
            .Where(r => r.Method == HttpMethod.Post
                        && (r.Path.StartsWith("/api/intake-records", StringComparison.Ordinal)
                            || r.Path.StartsWith("/api/verification-sessions", StringComparison.Ordinal)
                            || r.Path == "/api/tickets"))
            .Select(r => r.Path)
            .ToArray();

        // Intake first (nothing is lost if verification never completes), then
        // the verification session, then the ticket.
        Assert.Equal(["/api/intake-records", "/api/verification-sessions", "/api/tickets"], order);
    }

    [Fact]
    public async Task Wizard_SendsTheVerificationSessionIdRatherThanUnitAndContactDirectly()
    {
        using var factory = SignedIn();
        ScriptVerifiedPath(factory);
        factory.Api.On(HttpMethod.Post, "/api/tickets", HttpStatusCode.Created, TestData.Created());

        var client = await WalkToReviewAsync(factory);
        await WebTestHelpers.PostFormAsync(
            client, "/Tickets/New/Review", "/Tickets/New/Review",
            new KeyValuePair<string, string>("CategoryId", "7"),
            new KeyValuePair<string, string>("PriorityId", "3"),
            new KeyValuePair<string, string>("RequestSummary", "Not cooling."));

        var create = factory.Api.Requests.Last(r => r.Path == "/api/tickets" && r.Method == HttpMethod.Post);

        Assert.Contains("verificationSessionId", create.Body!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("intakeRecordId", create.Body!, StringComparison.OrdinalIgnoreCase);

        // The unit and contact reach the ticket through the session, not as
        // fields the browser could have altered after confirming them.
        Assert.DoesNotContain("unitReferenceId", create.Body!, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("contactReferenceId", create.Body!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Wizard_AlwaysConfirmsVerificationRatherThanSendingConfirmedFalse()
    {
        using var factory = SignedIn();
        ScriptVerifiedPath(factory);

        await WalkToReviewAsync(factory);

        var session = factory.Api.Requests.Last(r => r.Path == "/api/verification-sessions");
        Assert.Contains("\"confirmed\":true", session.Body!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ManualAgentConfirmation", session.Body!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Wizard_WithoutTheConfirmationCheckbox_DoesNotCreateASession()
    {
        using var factory = SignedIn();
        ScriptVerifiedPath(factory);

        var client = await SignInAsync(factory);
        await WebTestHelpers.PostFormAsync(
            client, "/Tickets/New/Intake", "/Tickets/New/Intake",
            new KeyValuePair<string, string>("RawUnitNumberEntered", "A-1402"));
        await WebTestHelpers.PostFormAsync(
            client, "/Tickets/New/Unit", "/Tickets/New/Unit?handler=Select",
            new KeyValuePair<string, string>("unitReferenceId", "42"),
            new KeyValuePair<string, string>("crmUnitId", "CRM-UNIT-42"),
            new KeyValuePair<string, string>("unitLabel", "A-1402"));

        var response = await WebTestHelpers.PostFormAsync(
            client, "/Tickets/New/Contact", "/Tickets/New/Contact?handler=Confirm",
            new KeyValuePair<string, string>("contactReferenceId", "84"),
            new KeyValuePair<string, string>("confirmed", "false"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await WebTestHelpers.AssertContainsAsync(response, "Confirm that you have verified");
        Assert.Equal(0, factory.Api.CountFor(HttpMethod.Post, "/api/verification-sessions"));
    }

    [Fact]
    public async Task Wizard_ShowsTheAuthorisationChainForARepresentativeContact()
    {
        // ISSUE-007: the agent must be able to see the chain before disclosing
        // anything to someone acting on another contact's behalf.
        using var factory = SignedIn();
        factory.Api
            .On(HttpMethod.Post, "/api/intake-records", HttpStatusCode.Created, TestData.Intake())
            .On(HttpMethod.Get, "/api/crm/units/search", HttpStatusCode.OK, new[] { TestData.Unit() })
            .On(HttpMethod.Get, "/api/crm/units/CRM-UNIT-42/contacts", HttpStatusCode.OK, new[]
            {
                TestData.Contact(contactReferenceId: 85, displayName: "Sami Aziz",
                    contactType: "Representative", representativeOf: 84)
            });

        var client = await SignInAsync(factory);
        await WebTestHelpers.PostFormAsync(
            client, "/Tickets/New/Intake", "/Tickets/New/Intake",
            new KeyValuePair<string, string>("RawUnitNumberEntered", "A-1402"));
        await WebTestHelpers.PostFormAsync(
            client, "/Tickets/New/Unit", "/Tickets/New/Unit?handler=Select",
            new KeyValuePair<string, string>("unitReferenceId", "42"),
            new KeyValuePair<string, string>("crmUnitId", "CRM-UNIT-42"),
            new KeyValuePair<string, string>("unitLabel", "A-1402"));

        var response = await client.GetAsync("/Tickets/New/Contact");

        await WebTestHelpers.AssertContainsAsync(response, "Authorised representative of contact #84");
    }

    // ----------------------------------------------------- CRM outage paths

    [Fact]
    public async Task CrmOutage_OffersTheFallbackAsAFirstClassPathNotAnError()
    {
        using var factory = SignedIn();
        factory.Api
            .On(HttpMethod.Post, "/api/intake-records", HttpStatusCode.Created, TestData.Intake())
            .OnProblem(HttpMethod.Get, "/api/crm/units/search", HttpStatusCode.BadGateway,
                "crm-unavailable", "CRM is currently unavailable");

        var client = await SignInAsync(factory);
        var response = await WebTestHelpers.PostFormAsync(
            client, "/Tickets/New/Intake", "/Tickets/New/Intake",
            new KeyValuePair<string, string>("RawUnitNumberEntered", "A-1402"));

        WebTestHelpers.AssertRedirectTo(response, "/Tickets/New/Unit");

        var unitPage = await client.GetAsync("/Tickets/New/Unit");
        await WebTestHelpers.AssertContainsAsync(unitPage, "Tiger CRM is unavailable");
        await WebTestHelpers.AssertContainsAsync(unitPage, "Continue without verification");

        // role="status", not role="alert" - an expected, handled condition.
        await WebTestHelpers.AssertContainsAsync(unitPage, "role=\"status\"");
    }

    [Theory]
    [InlineData("1", "Critical")]
    [InlineData("2", "High")]
    public async Task CrmOutage_CriticalAndHigh_CreateAProvisionalTicket(string priorityId, string priorityName)
    {
        using var factory = SignedIn();
        factory.Api
            .On(HttpMethod.Post, "/api/intake-records", HttpStatusCode.Created, TestData.Intake())
            .On(HttpMethod.Get, "/api/crm/units/search", HttpStatusCode.BadGateway)
            .On(HttpMethod.Post, "/api/tickets/provisional", HttpStatusCode.Created,
                TestData.Created(
                    ticketNumber: "CS-2026-0999",
                    priorityId: byte.Parse(priorityId),
                    verification: "PendingCrmVerification"));

        var client = await SignInAsync(factory);
        await WebTestHelpers.PostFormAsync(
            client, "/Tickets/New/Intake", "/Tickets/New/Intake",
            new KeyValuePair<string, string>("RawUnitNumberEntered", "A-1402"));
        await WebTestHelpers.PostFormAsync(
            client, "/Tickets/New/Unit", "/Tickets/New/Unit?handler=Fallback");

        var response = await WebTestHelpers.PostFormAsync(
            client, "/Tickets/New/Review", "/Tickets/New/Review",
            new KeyValuePair<string, string>("CategoryId", "7"),
            new KeyValuePair<string, string>("PriorityId", priorityId),
            new KeyValuePair<string, string>("RequestSummary", $"{priorityName} fault, CRM down."));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await WebTestHelpers.AssertContainsAsync(response, "Ticket created");
        await WebTestHelpers.AssertContainsAsync(response, "CS-2026-0999");
        await WebTestHelpers.AssertContainsAsync(response, "Provisional");

        // The provisional endpoint, not the verified one.
        Assert.Equal(1, factory.Api.CountFor(HttpMethod.Post, "/api/tickets/provisional"));
    }

    [Theory]
    [InlineData("3")]
    [InlineData("4")]
    public async Task CrmOutage_MediumAndLow_RemainIntakeOnlyAndAreNotShownAsATicket(string priorityId)
    {
        // The API answers 200 with the updated intake record and creates no
        // ticket. Presenting that as a created ticket would hand the caller a
        // reference number that does not exist.
        using var factory = SignedIn();
        factory.Api
            .On(HttpMethod.Post, "/api/intake-records", HttpStatusCode.Created, TestData.Intake())
            .On(HttpMethod.Get, "/api/crm/units/search", HttpStatusCode.BadGateway)
            .On(HttpMethod.Post, "/api/tickets/provisional", HttpStatusCode.OK,
                TestData.Intake(verificationStatus: "PendingCrmVerification"));

        var client = await SignInAsync(factory);
        await WebTestHelpers.PostFormAsync(
            client, "/Tickets/New/Intake", "/Tickets/New/Intake",
            new KeyValuePair<string, string>("RawUnitNumberEntered", "A-1402"));
        await WebTestHelpers.PostFormAsync(
            client, "/Tickets/New/Unit", "/Tickets/New/Unit?handler=Fallback");

        var response = await WebTestHelpers.PostFormAsync(
            client, "/Tickets/New/Review", "/Tickets/New/Review",
            new KeyValuePair<string, string>("CategoryId", "7"),
            new KeyValuePair<string, string>("PriorityId", priorityId),
            new KeyValuePair<string, string>("RequestSummary", "Routine request, CRM down."));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await WebTestHelpers.AssertContainsAsync(response, "Interaction recorded");
        await WebTestHelpers.AssertContainsAsync(response, "No ticket was created");
        await WebTestHelpers.AssertContainsAsync(response, "Do not give the caller a ticket number");

        await WebTestHelpers.AssertDoesNotContainAsync(response, "Ticket created");
        await WebTestHelpers.AssertDoesNotContainAsync(response, "Open ticket");
    }

    // ------------------------------------------------------ guards and state

    [Fact]
    public async Task Wizard_ResubmittingTheSameFormDoesNotCreateASecondTicket()
    {
        using var factory = SignedIn();
        ScriptVerifiedPath(factory);
        factory.Api.On(HttpMethod.Post, "/api/tickets", HttpStatusCode.Created, TestData.Created());

        var client = await WalkToReviewAsync(factory);

        var reviewPage = await client.GetAsync("/Tickets/New/Review");
        var html = await reviewPage.Content.ReadAsStringAsync();
        var token = WebTestHelpers.ExtractAntiforgeryToken(html);
        var submitToken = System.Text.RegularExpressions.Regex
            .Match(html, "name=\"SubmitToken\" value=\"([^\"]+)\"").Groups[1].Value;

        Assert.NotEmpty(submitToken);

        KeyValuePair<string, string>[] Body() =>
        [
            new("CategoryId", "7"),
            new("PriorityId", "3"),
            new("RequestSummary", "Not cooling."),
            new("SubmitToken", submitToken),
            new("__RequestVerificationToken", token)
        ];

        var first = await client.PostAsync("/Tickets/New/Review", new FormUrlEncodedContent(Body()));
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        await WebTestHelpers.AssertContainsAsync(first, "CS-2026-0777");

        // The browser replaying the exact same post - a refresh, a Back-and-
        // resubmit, or an impatient second click that beat the JS guard.
        var second = await client.PostAsync("/Tickets/New/Review", new FormUrlEncodedContent(Body()));

        WebTestHelpers.AssertRedirectTo(second, "/Tickets");
        Assert.Equal(1, factory.Api.CountFor(HttpMethod.Post, "/api/tickets"));
    }

    [Fact]
    public async Task Wizard_ValidatesRequiredFieldsWithoutCallingTheApi()
    {
        using var factory = SignedIn();
        ScriptVerifiedPath(factory);

        var client = await WalkToReviewAsync(factory);

        var response = await WebTestHelpers.PostFormAsync(
            client, "/Tickets/New/Review", "/Tickets/New/Review",
            new KeyValuePair<string, string>("CategoryId", "0"),
            new KeyValuePair<string, string>("PriorityId", "3"),
            new KeyValuePair<string, string>("RequestSummary", ""));

        await WebTestHelpers.AssertContainsAsync(response, "Enter the category id");
        await WebTestHelpers.AssertContainsAsync(response, "Summarise the caller");
        Assert.Equal(0, factory.Api.CountFor(HttpMethod.Post, "/api/tickets"));
    }

    [Fact]
    public async Task Wizard_SurfacesAnUnknownCategoryOnTheFieldThatCausedIt()
    {
        using var factory = SignedIn();
        ScriptVerifiedPath(factory);
        factory.Api.OnProblem(
            HttpMethod.Post, "/api/tickets", HttpStatusCode.NotFound,
            "category-not-found", "Category not found");

        var client = await WalkToReviewAsync(factory);

        var response = await WebTestHelpers.PostFormAsync(
            client, "/Tickets/New/Review", "/Tickets/New/Review",
            new KeyValuePair<string, string>("CategoryId", "99999"),
            new KeyValuePair<string, string>("PriorityId", "3"),
            new KeyValuePair<string, string>("RequestSummary", "Not cooling."));

        await WebTestHelpers.AssertContainsAsync(response, "No category with that id");
    }

    [Fact]
    public async Task Wizard_WhenTheSessionExpired_SendsTheAgentBackToVerifyAgain()
    {
        using var factory = SignedIn();
        ScriptVerifiedPath(factory);
        factory.Api.OnProblem(
            HttpMethod.Post, "/api/tickets", HttpStatusCode.Gone,
            "verification-session-expired", "Verification session expired");

        var client = await WalkToReviewAsync(factory);

        var response = await WebTestHelpers.PostFormAsync(
            client, "/Tickets/New/Review", "/Tickets/New/Review",
            new KeyValuePair<string, string>("CategoryId", "7"),
            new KeyValuePair<string, string>("PriorityId", "3"),
            new KeyValuePair<string, string>("RequestSummary", "Not cooling."));

        await WebTestHelpers.AssertContainsAsync(response, "Verify the caller again");
    }

    [Fact]
    public async Task Wizard_StepsSkippedDirectly_RedirectBackToWhereTheyLeftOff()
    {
        using var factory = SignedIn();
        var client = await SignInAsync(factory);

        // No intake record yet.
        WebTestHelpers.AssertRedirectTo(await client.GetAsync("/Tickets/New/Unit"), "/Tickets/New/Intake");
        WebTestHelpers.AssertRedirectTo(await client.GetAsync("/Tickets/New/Contact"), "/Tickets/New/Intake");
        WebTestHelpers.AssertRedirectTo(await client.GetAsync("/Tickets/New/Review"), "/Tickets/New/Intake");
    }

    [Fact]
    public async Task Wizard_ReviewReachedWithoutVerification_ReturnsToTheUnitStep()
    {
        using var factory = SignedIn();
        factory.Api.On(HttpMethod.Post, "/api/intake-records", HttpStatusCode.Created, TestData.Intake());

        var client = await SignInAsync(factory);
        await WebTestHelpers.PostFormAsync(
            client, "/Tickets/New/Intake", "/Tickets/New/Intake",
            new KeyValuePair<string, string>("RawUnitNumberEntered", "A-1402"));

        var response = await client.GetAsync("/Tickets/New/Review");

        WebTestHelpers.AssertRedirectTo(response, "/Tickets/New/Unit");
    }

    [Fact]
    public async Task Wizard_StateCookieIsEncryptedHttpOnlyAndCarriesNoContactDetails()
    {
        using var factory = SignedIn();
        ScriptVerifiedPath(factory);

        var client = await SignInAsync(factory);
        var response = await WebTestHelpers.PostFormAsync(
            client, "/Tickets/New/Intake", "/Tickets/New/Intake",
            new KeyValuePair<string, string>("RawUnitNumberEntered", "A-1402"));

        var cookies = response.Headers.TryGetValues("Set-Cookie", out var values) ? values.ToArray() : [];
        var wizard = Assert.Single(cookies, c => c.StartsWith("TigerCS.NewTicket", StringComparison.Ordinal));

        Assert.Contains("httponly", wizard, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("secure", wizard, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", wizard, StringComparison.OrdinalIgnoreCase);

        // Encrypted, so even the unit number the agent typed is not readable.
        Assert.DoesNotContain("A-1402", wizard, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Wizard_IntakeIsRecordedBeforeAnyCrmLookupIsAttempted()
    {
        // MVP-ERD.md §2.9: the intake record is the unconditional first step so
        // no interaction is lost, including when the CRM is unreachable.
        using var factory = SignedIn();
        factory.Api
            .On(HttpMethod.Post, "/api/intake-records", HttpStatusCode.Created, TestData.Intake())
            .On(HttpMethod.Get, "/api/crm/units/search", HttpStatusCode.BadGateway);

        var client = await SignInAsync(factory);
        await WebTestHelpers.PostFormAsync(
            client, "/Tickets/New/Intake", "/Tickets/New/Intake",
            new KeyValuePair<string, string>("RawUnitNumberEntered", "A-1402"));
        await client.GetAsync("/Tickets/New/Unit");

        var intakeIndex = factory.Api.Requests.FindIndex(r => r.Path == "/api/intake-records");
        var crmIndex = factory.Api.Requests.FindIndex(r => r.Path.StartsWith("/api/crm", StringComparison.Ordinal));

        Assert.True(intakeIndex >= 0, "The intake record must be created.");
        Assert.True(crmIndex > intakeIndex, "The CRM lookup must come after the intake record.");
    }

    [Fact]
    public async Task Wizard_RecordsPhoneChannelAndTheRawUnitNumberAsTyped()
    {
        using var factory = SignedIn();
        factory.Api.On(HttpMethod.Post, "/api/intake-records", HttpStatusCode.Created, TestData.Intake());

        var client = await SignInAsync(factory);
        await WebTestHelpers.PostFormAsync(
            client, "/Tickets/New/Intake", "/Tickets/New/Intake",
            new KeyValuePair<string, string>("RawUnitNumberEntered", "  a-1402  "));

        var intake = factory.Api.Requests.Last(r => r.Path == "/api/intake-records");

        Assert.Contains("\"channelId\":\"Phone\"", intake.Body!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"isUnitRelated\":true", intake.Body!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("a-1402", intake.Body!, StringComparison.Ordinal);
    }
}

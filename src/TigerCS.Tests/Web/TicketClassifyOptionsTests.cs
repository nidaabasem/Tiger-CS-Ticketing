// TigerCS.Web is referenced under an alias — see TigerCS.Tests.csproj.
extern alias TigerCsWeb;

using TigerCS.Application.Modules.Administration.Dto;
using TigerCS.Application.Modules.ClassificationAndRouting.Dto;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCsWeb::TigerCS.Web.Models;
using TigerCsWeb::TigerCS.Web.Services.Auth;

namespace TigerCS.Tests.Web;

/// <summary>
/// What the Classify form offers, and when it says it cannot: only the ticket's own department's active categories and published
/// request types; a request type sits under the same-named category; a department with nothing configured is explained, with the
/// existing administrative path, and Classify is never recommended as something to do. Nothing is invented.
/// </summary>
public sealed class TicketClassifyOptionsTests
{
    private const int CustomerService = 2;
    private const int CallCenter = 4;

    private static CategoryDto Category(int id, string name, int department = CustomerService) => new(id, name, department, "Dept");
    private static RequestTypeOptionDto RequestType(int id, string name, int department = CustomerService, bool published = true) => new(id, name, department, 3, published);

    private static CurrentUser User(params string[] roles) => new(Guid.NewGuid(), "Test", roles, null);

    [Fact]
    public void ARequestType_IsOfferedOnlyUnderTheCategoryWithTheSameName()
    {
        var options = TicketClassifyOptions.Build(
            CustomerService,
            [Category(1, "General Inquiry"), Category(2, "Complaint Handling"), Category(3, "NOC for Resale")],
            [RequestType(10, "Complaint Handling"), RequestType(11, "NOC for Resale"), RequestType(12, "noc for resale ")]);

        Assert.Equal(ClassifyAvailability.Available, options.Availability);
        Assert.Equal(["Complaint Handling", "General Inquiry", "NOC for Resale"], options.Categories.Select(c => c.Name));
        Assert.Empty(options.Categories.Single(c => c.Name == "General Inquiry").RequestTypes);
        Assert.Equal([10], options.Categories.Single(c => c.Name == "Complaint Handling").RequestTypes.Select(r => r.RequestTypeId));
        Assert.Equal([11, 12], options.Categories.Single(c => c.Name == "NOC for Resale").RequestTypes.Select(r => r.RequestTypeId));
    }

    [Fact]
    public void TheDependency_IsEnforced_AndACategoryWithoutRequestTypesClassifiesOnItsOwn()
    {
        var options = TicketClassifyOptions.Build(
            CustomerService, [Category(1, "General Inquiry"), Category(2, "Complaint Handling")], [RequestType(10, "Complaint Handling")]);

        Assert.True(options.CategoryNeedsRequestType(2));
        Assert.True(options.Allows(2, 10));
        Assert.False(options.Allows(1, 10));          // a type under another category
        Assert.False(options.CategoryNeedsRequestType(1));
        Assert.False(options.Allows(99, 10));
    }

    [Fact]
    public void OnlyTheTicketsOwnDepartment_ActiveAndPublished_IsOffered()
    {
        var options = TicketClassifyOptions.Build(
            CustomerService,
            [Category(1, "Complaint Handling"), Category(2, "Complaint Handling", department: 99), Category(3, "Elsewhere", department: 99)],
            [RequestType(10, "Complaint Handling"), RequestType(11, "Complaint Handling", department: 99), RequestType(12, "Complaint Handling", published: false)]);

        Assert.Equal([1], options.Categories.Select(c => c.CategoryId));
        Assert.Equal([10], options.Categories[0].RequestTypes.Select(r => r.RequestTypeId));   // 12 has no published workflow, 11 is another department's
    }

    [Fact]
    public void ARequestTypeWithNoUniqueCategory_IsReportedNotAttachedToAnArbitraryOne()
    {
        var options = TicketClassifyOptions.Build(
            CustomerService,
            [Category(1, "General Inquiry"), Category(2, "Duplicate"), Category(3, "Duplicate")],
            [RequestType(10, "Coordinate Handover"), RequestType(11, "Duplicate")]);

        Assert.All(options.Categories, c => Assert.Empty(c.RequestTypes));
        Assert.Equal([10, 11], options.UnreachableRequestTypes.Select(r => r.RequestTypeId).Order());
    }

    [Fact]
    public void TheCategoryTheAiRequestTypeBelongsUnder_IsFoundByName()
    {
        var options = TicketClassifyOptions.Build(CustomerService, [Category(1, "General Inquiry"), Category(2, "Complaint Handling")], [RequestType(10, "Complaint Handling")]);

        Assert.Equal(2, options.CategoryForRequestType("Complaint Handling")?.CategoryId);
        Assert.Null(options.CategoryForRequestType("Something else"));
        Assert.Null(options.CategoryForRequestType(null));
    }

    [Fact]
    public void ADepartmentWithRequestTypesButNoCategories_HasNothingToClassifyUnder()
    {
        var options = TicketClassifyOptions.Build(CallCenter, [Category(1, "General Inquiry")], [RequestType(10, "Callback", CallCenter)]);

        Assert.Equal(ClassifyAvailability.NoCategories, options.Availability);
        Assert.False(options.IsAvailable);
    }

    // ---- the explanation ----

    [Fact]
    public void WhenNothingIsConfigured_TheNoticeNamesTheDepartment_AndPointsAnAdministratorAtRequestTypes()
    {
        var options = TicketClassifyOptions.Build(CallCenter, [], []);

        var notice = ClassifyNotice.For(options, "Call Center", User(Roles.SystemAdministrator), canTransfer: false);

        Assert.NotNull(notice);
        Assert.True(notice!.IsConfiguration);
        Assert.Contains("Call Center", notice.Headline);
        Assert.Equal("/Admin/RequestTypes", notice.AdminHref);
        Assert.Contains("Administration → Request Types", notice.Detail);
        Assert.DoesNotContain("#", notice.Detail);   // no technical ids
    }

    [Fact]
    public void ANonAdministrator_IsToldWhoCanFixIt_AndGetsNoAdminLink()
    {
        var options = TicketClassifyOptions.Build(CallCenter, [], []);

        var notice = ClassifyNotice.For(options, "Call Center", User(Roles.CsAgent), canTransfer: false)!;

        Assert.Null(notice.AdminHref);
        Assert.Contains("Ask a System Administrator", notice.Detail);
        Assert.DoesNotContain("Transfer", notice.Detail);
    }

    [Fact]
    public void ATransferringRole_IsAlsoToldTheTicketCanBeTransferred()
    {
        var notice = ClassifyNotice.For(TicketClassifyOptions.Build(CallCenter, [], []), "Call Center", User(Roles.CsManager), canTransfer: true)!;

        Assert.Contains("transfer the ticket", notice.Detail);
    }

    [Fact]
    public void AFailedRead_IsNotPresentedAsMissingConfiguration()
    {
        var notice = ClassifyNotice.For(TicketClassifyOptions.Failed, "Call Center", User(Roles.SystemAdministrator), canTransfer: false)!;

        Assert.False(notice.IsConfiguration);
        Assert.Null(notice.AdminHref);
        Assert.Contains("Reload", notice.Detail);
    }

    [Fact]
    public void WhenOptionsExist_ThereIsNoNotice() =>
        Assert.Null(ClassifyNotice.For(TicketClassifyOptions.Build(CustomerService, [Category(1, "General Inquiry")], []), "Customer Service", User(Roles.CsAgent), canTransfer: false));

    // ---- the guidance never recommends an action that cannot be completed ----

    [Fact]
    public void Guidance_DoesNotRecommendClassify_WhenThereIsNothingToClassifyWith()
    {
        var notice = ClassifyNotice.For(TicketClassifyOptions.Build(CallCenter, [], []), "Call Center", User(Roles.CsAgent), canTransfer: false);

        var guidance = TicketNextAction.Build(Facts(notice));

        Assert.NotEqual("classify", guidance.Primary?.Key);
        Assert.Null(guidance.Primary);                                  // an agent has no administrative path to offer
        Assert.Contains("no classification options", guidance.Headline);
        Assert.Contains(guidance.Unavailable, u => u.Label == "Classify");
    }

    [Fact]
    public void Guidance_OffersTheAdministrativePath_ToAnAdministrator()
    {
        var notice = ClassifyNotice.For(TicketClassifyOptions.Build(CallCenter, [], []), "Call Center", User(Roles.SystemAdministrator), canTransfer: false);

        var guidance = TicketNextAction.Build(Facts(notice));

        Assert.Equal("/Admin/RequestTypes", guidance.Primary!.Href);
        Assert.False(guidance.Primary.IsTab);
        Assert.NotEqual("classify", guidance.Primary.Key);
    }

    [Fact]
    public void Guidance_StillRecommendsClassify_WhenOptionsExist()
    {
        var guidance = TicketNextAction.Build(Facts(notice: null));

        Assert.Equal("classify", guidance.Primary!.Key);
    }

    private static TicketGuidanceFacts Facts(ClassifyNotice? notice) => new(
        TicketStatus: "InProgress", IsClassified: false, HasOwner: true, ViewerIsOwner: false, OwnerName: "Ahmed", DepartmentName: "Call Center",
        ReopenCount: 0, IsReopenEligible: false, PendingCrmVerification: false, HandoffState: "NotRequired", HandoffRequestedAtUtc: null,
        HandlingState: "Ai", SlaBreached: false, SlaBreachText: null, FirstResponsePending: false, ResolutionClockPaused: false,
        ViewerIsReadOnly: false, CanClassify: true, CanAssign: false, CanTransfer: false, CanChangeStatus: false, CanResolve: false,
        CanClose: false, CanEscalate: false, CanReopen: false, CanRequestDowngrade: false, CanRecordFirstResponse: false,
        HasPendingDowngrade: false, CanDecideDowngrade: false, PendingApprovals: [], RequiredApprovals: [], ReopenApproved: false,
        NowUtc: new DateTime(2026, 10, 23, 12, 0, 0, DateTimeKind.Utc), ClassifyNotice: notice);
}

using System.Runtime.CompilerServices;
using TigerCS.Application.Modules.Administration.Services;
using TigerCS.Domain.Modules.Ticketing;
using TigerCS.Domain.Modules.WorkflowConfiguration;

namespace TigerCS.Tests.Web;

/// <summary>
/// The approved lifecycle cleanup, across every surface that used to offer
/// <c>TicketStatus.PendingThirdParty</c> as something a person could pick:
/// the queue's status filter, the Workflow Designer's step catalogue, and the
/// Administration forms that carried its capability flag.
///
/// <para>
/// The rule these hold to is the same one throughout: <b>retired as a choice,
/// never as a value</b>. Nothing offers it, nothing stores a new one, and
/// every historical row that already has one keeps reading back exactly as it
/// was written — which is why the enum value, the step kind and both database
/// columns are all still here.
/// </para>
/// </summary>
public sealed class PendingThirdPartyRetirementTests
{
    private static string SourceFile(string relativeToSrc, [CallerFilePath] string testFilePath = "")
    {
        var srcDir = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(testFilePath)!, "..", ".."));
        return Path.Combine(srcDir, relativeToSrc);
    }

    private static string View(params string[] pathUnderPages) =>
        File.ReadAllText(SourceFile(Path.Combine(["TigerCS.Web", "Pages", .. pathUnderPages])));

    // ---- The ticket queue's status filter ----

    [Fact]
    public void QueueStatusFilter_IsBuiltFromTheDomainsActiveStatuses_NotALiteralList()
    {
        var queue = View("Shared", "_TicketListView.cshtml");

        Assert.Contains("TicketStatusTransitions.ActiveStatuses", queue, StringComparison.Ordinal);
        Assert.DoesNotContain("\"PendingThirdParty\"", queue, StringComparison.Ordinal);

        // The other five statuses are still filterable — this narrows one
        // option, it does not rebuild the filter.
        Assert.Equal(
            ["Open", "InProgress", "PendingCustomer", "Resolved", "Closed"],
            TicketStatusTransitions.ActiveStatuses.Select(s => s.ToString()).ToArray());
    }

    // ---- The Workflow Designer's step catalogue ----

    [Fact]
    public void WorkflowDesigner_NoLongerOffersThePendingInternalStep()
    {
        var offered = AdminWorkflowAppService.Catalog().StepKinds.Select(k => k.Kind).ToArray();

        Assert.DoesNotContain(WorkflowStepKind.PendingInternal, offered);
        Assert.Contains(WorkflowStepKind.PendingCustomer, offered);
        Assert.Equal(WorkflowStepKinds.Selectable.Count, offered.Length);
    }

    [Fact]
    public void ThePendingInternalStepKind_IsStillDescribable_SoExistingVersionsKeepValidating()
    {
        // A published version that already carries such a step must keep
        // validating and rendering its own step list; only what is offered for
        // a NEW step narrowed.
        Assert.True(WorkflowStepKinds.IsSupported(WorkflowStepKind.PendingInternal));
        Assert.Equal("Pending Internal / Third Party", WorkflowStepKinds.Describe(WorkflowStepKind.PendingInternal).Label);
        Assert.Contains(WorkflowStepKinds.All, i => i.Kind == WorkflowStepKind.PendingInternal);

        Assert.True(WorkflowStepKinds.IsLegacyOnly(WorkflowStepKind.PendingInternal));
        Assert.All(WorkflowStepKinds.Selectable, i => Assert.False(WorkflowStepKinds.IsLegacyOnly(i.Kind)));
    }

    // ---- The Administration forms ----

    [Fact]
    public void WorkflowVersionSettings_NoLongerOffersThePendingInternalCapability_ButPreservesItsStoredValue()
    {
        var page = View("Admin", "WorkflowVersion.cshtml");

        Assert.DoesNotContain(
            "<input type=\"checkbox\" asp-for=\"Settings.AllowsPendingInternal\" />", page, StringComparison.Ordinal);
        Assert.DoesNotContain("Pending Internal / Third Party available", page, StringComparison.Ordinal);

        // Round-tripped rather than dropped: saving the other settings must not
        // silently rewrite an existing version's column.
        Assert.Contains(
            "<input type=\"hidden\" asp-for=\"Settings.AllowsPendingInternal\" />", page, StringComparison.Ordinal);

        // The capability it sits beside is untouched.
        Assert.Contains(
            "<input type=\"checkbox\" asp-for=\"Settings.AllowsPendingCustomer\" />", page, StringComparison.Ordinal);
    }

    [Fact]
    public void RequestTypeEdit_NoLongerOffersThePendingInternalCapability_ButPreservesItsStoredValue()
    {
        var page = View("Admin", "RequestTypeEdit.cshtml");

        Assert.DoesNotContain(
            "<input type=\"checkbox\" asp-for=\"Details.AllowPendingInternal\" />", page, StringComparison.Ordinal);
        Assert.DoesNotContain("Pending Internal / Third Party allowed", page, StringComparison.Ordinal);

        // One hidden field, on the edit form only — a new request type simply
        // does not carry the flag.
        Assert.Equal(1, CountOccurrences(page, "<input type=\"hidden\" asp-for=\"Details.AllowPendingInternal\" />"));

        Assert.Equal(2, CountOccurrences(page, "<input type=\"checkbox\" asp-for=\"Details.AllowPendingCustomer\" />"));
    }

    // ---- The capability itself ----

    [Fact]
    public void CanGoPendingInternal_IsStillComputedFromItsStoredColumns_ThoughNothingConsultsItAnyMore()
    {
        // Deprecated, not deleted: both columns are still read and combined
        // the same way, so no migration and no data loss is involved. What
        // changed is that no transition consults the result — proved by
        // TicketWorkflowEnforcementTests.CanGoPendingInternal_NoLongerGatesAnything.
        var template = new WorkflowTemplate(
            workflowId: 1, versionNumber: 1, "LEGACY", "Legacy flow", description: null,
            allowsPendingCustomer: true, allowsPendingInternal: true, requiresApproval: false,
            new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), createdByEmployeeId: null);

        var requestType = new RequestType(
            departmentId: 2, "Legacy request", workflowId: 1, defaultPriorityId: 3,
            allowAgentPriorityChange: true, allowPendingCustomer: true, allowPendingInternal: true, allowReopen: true);

        Assert.True(WorkflowCapabilities.Resolve(template, requestType).CanGoPendingInternal);
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        for (var i = haystack.IndexOf(needle, StringComparison.Ordinal); i >= 0;
             i = haystack.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }
}

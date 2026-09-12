using TigerCS.Application.Modules.Ticketing.Dto;
using TigerCS.Domain.Modules.ClassificationAndRouting;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Domain.Modules.SlaAndEscalation;
using TigerCS.Domain.Modules.Ticketing;
using TigerCS.Tests.Ticketing.Dashboard;

namespace TigerCS.Tests.Ticketing.Integration;

/// <summary>
/// The ticket queue's free-text search, proven as SQL against the real
/// EF Core-mapped schema: besides the ticket number and summary it always
/// matched, it now finds a ticket by the customer name and unit number the
/// ticket snapshotted, and by the phone number of the intake it was
/// promoted from — the searchable data the unified Tickets workspace's
/// search box offers. Visibility scoping is untouched by the search.
/// </summary>
public sealed class TicketQueueSearchSqliteTests : IDisposable
{
    private static readonly string[] CrossDepartment = [Roles.CsSupervisor];
    private readonly DashboardSqliteFixture _db = new();

    public void Dispose() => _db.Dispose();

    private async Task<IReadOnlyList<string>> SearchAsync(string search, string[]? roles = null, Guid? caller = null)
    {
        using var context = _db.CreateContext();
        var result = await _db.CreateQueryService(context).GetQueueAsync(
            caller ?? Guid.NewGuid(), roles ?? CrossDepartment,
            new TicketListRequestDto(null, null, null, null, null, null, search, null, null, 1, 50));
        return result.Items.Select(i => i.TicketNumber).OrderBy(n => n).ToList();
    }

    [Fact]
    public async Task Search_MatchesTheTicketNumberAndSummary_AsBefore()
    {
        Ticket byNumber, bySummary;
        using (var context = _db.CreateContext())
        {
            byNumber = _db.AddTicket(context, _db.CustomerServiceId);
            bySummary = _db.AddTicket(context, _db.CustomerServiceId);
            _db.AddTicket(context, _db.CustomerServiceId);
        }

        Assert.Equal([byNumber.TicketNumber], await SearchAsync(byNumber.TicketNumber));
        Assert.Equal([bySummary.TicketNumber], await SearchAsync(bySummary.RequestSummary));
    }

    [Fact]
    public async Task Search_MatchesTheSnapshottedCustomerName()
    {
        Ticket match;
        using (var context = _db.CreateContext())
        {
            match = _db.AddTicket(context, _db.CustomerServiceId, customerName: "Mariam Al Falasi");
            _db.AddTicket(context, _db.CustomerServiceId, customerName: "Omar Haddad");
            _db.AddTicket(context, _db.CustomerServiceId);
        }

        Assert.Equal([match.TicketNumber], await SearchAsync("Falasi"));
    }

    [Fact]
    public async Task Search_MatchesTheCrmBuyerUnitNumber_AndTheManualUnitNumber()
    {
        Ticket crmUnit, manualUnit;
        using (var context = _db.CreateContext())
        {
            // The fixture's CRM Buyer path stamps unit "U-{sequence}".
            crmUnit = _db.AddTicket(context, _db.CustomerServiceId, customerName: "Any Buyer");
            manualUnit = Ticket.CreateUnverified(
                "TG-TST-20260910-90001", _db.CustomerServiceId, _db.CsCategoryId, (byte)PriorityLevel.Medium,
                "Manual entry ticket", DashboardSqliteFixture.Now.AddHours(-2),
                manualProjectName: "Tiger Tower", manualUnitNumber: "TT-1204");
            context.Tickets.Add(manualUnit);
            _db.AddTicket(context, _db.CustomerServiceId);
            context.SaveChanges();
        }

        Assert.Equal([crmUnit.TicketNumber], await SearchAsync(crmUnit.CrmBuyerUnitNumber!));
        Assert.Equal([manualUnit.TicketNumber], await SearchAsync("1204"));
    }

    [Fact]
    public async Task Search_MatchesThePhoneNumberOfTheIntakeTheTicketWasPromotedFrom()
    {
        Ticket match;
        using (var context = _db.CreateContext())
        {
            match = _db.AddTicket(context, _db.CustomerServiceId);
            var other = _db.AddTicket(context, _db.CustomerServiceId);

            var intake = new IntakeRecord(1, "+971501234567", _db.CustomerServiceId, false, null, null, _db.CsAgentId, DashboardSqliteFixture.Now.AddHours(-3));
            intake.LinkToTicket(match.TicketId, CrmVerificationStatus.Unverified, hasSelectedUnit: false);
            var otherIntake = new IntakeRecord(1, "+971559998877", _db.CustomerServiceId, false, null, null, _db.CsAgentId, DashboardSqliteFixture.Now.AddHours(-3));
            otherIntake.LinkToTicket(other.TicketId, CrmVerificationStatus.Unverified, hasSelectedUnit: false);
            // An unlinked intake with the searched phone must not surface anything.
            context.IntakeRecords.AddRange(intake, otherIntake,
                new IntakeRecord(1, "+971501234567", _db.CustomerServiceId, false, null, null, _db.CsAgentId, DashboardSqliteFixture.Now.AddHours(-3)));
            context.SaveChanges();
        }

        Assert.Equal([match.TicketNumber], await SearchAsync("50123"));
    }

    [Fact]
    public async Task Search_NeverWidensDepartmentVisibility()
    {
        using (var context = _db.CreateContext())
        {
            _db.AddTicket(context, _db.CollectionsId, customerName: "Mariam Al Falasi");
        }

        // A department-scoped Registration employee cannot see Collections'
        // ticket, searched by name or not.
        Assert.Empty(await SearchAsync("Falasi", roles: [Roles.DepartmentEmployee], caller: _db.RegistrationEmployeeId));
        Assert.Single(await SearchAsync("Falasi"));
    }
}

using TigerCS.Application.Modules.CustomerVerification.CrmIntegration;
using TigerCS.Application.Modules.GenesysIntegration.Dto;
using TigerCS.Application.Modules.Ticketing.Dto;
using TigerCS.Domain.Modules.Ticketing;
using TigerCS.Tests.GenesysIntegration.Fakes;

namespace TigerCS.Tests.GenesysIntegration.Services;

/// <summary>
/// "What is my latest ticket?" asked of the Genesys AI: the ticket the current
/// conversation created is excluded — and only that one — so the customer is told
/// about their most recent <i>previous</i> ticket, manual or Genesys-created.
/// </summary>
public class GenesysLatestPreviousTicketTests
{
    private const string Phone = "+971500000042";
    private static readonly Guid ServiceAccount = Guid.NewGuid();

    private static GenesysServiceFixture NewFixture()
    {
        var f = new GenesysServiceFixture();
        f.CrmBuyers.Returns(CrmBuyerLookupResult.NotFound());
        return f;
    }

    private static async Task<long> GenesysTicketAsync(GenesysServiceFixture f, string conversationId, int departmentId)
    {
        var r = await f.Ingestion.IngestAsync(
            ServiceAccount,
            new GenesysInquiryDto(conversationId, GenesysChannel.WhatsApp,
                CustomerPhone: Phone, CustomerName: "Ahmed Ali", DepartmentId: departmentId));
        Assert.Equal(GenesysIngestionOutcome.TicketCreated, r.Outcome);
        await Task.Delay(5);
        return r.Ticket!.TicketId;
    }

    private static async Task<long> ManualTicketAsync(GenesysServiceFixture f, int departmentId, int categoryId)
    {
        var intake = new IntakeRecord(
            WellKnownChannels.FaceToFaceKiosk, Phone, departmentId, false, null, null, ServiceAccount, DateTime.UtcNow);
        await f.IntakeRecords.AddAsync(intake);
        var r = await f.ManualTicketCreation.CreateAsync(
            ServiceAccount,
            new CreateTicketRequestDto(
                intake.IntakeRecordId, null, null, categoryId, (byte)PriorityLevel.High, "Manual walk-in ticket"));
        Assert.Equal(TicketCreationOutcome.Success, r.Outcome);
        await Task.Delay(5);
        return intake.LinkedTicketId!.Value;
    }

    [Fact]
    public async Task CurrentConversationTicket_IsExcluded_PreviousGenesysTicketIsReturned()
    {
        var f = NewFixture();
        var (dept, _) = f.SeedGenesysDepartment("Customer Service", "CS");
        var previous = await GenesysTicketAsync(f, "conv-100", dept.DepartmentId);
        var current = await GenesysTicketAsync(f, "conv-101", dept.DepartmentId);

        var r = (await f.CustomerLookup.LookUpAsync(Phone, "conv-101"))!;

        Assert.NotNull(r.LatestPreviousTicket);
        Assert.Equal(previous, r.LatestPreviousTicket!.TicketId);
        Assert.NotEqual(current, r.LatestPreviousTicket.TicketId);
        Assert.False(r.NoPreviousTicketFound);
        Assert.Equal(r.LatestPreviousTicket.TicketNumber, r.ScreenPop.LatestPreviousTicketNumber);
        Assert.Contains(r.LatestPreviousTicket.TicketNumber, r.ScreenPop.LatestPreviousTicketMessage);
    }

    [Fact]
    public async Task OnlyTheCurrentConversationIsExcluded_NotEveryGenesysTicket()
    {
        var f = NewFixture();
        var (dept, _) = f.SeedGenesysDepartment("Customer Service", "CS");
        await GenesysTicketAsync(f, "conv-a", dept.DepartmentId);
        var middle = await GenesysTicketAsync(f, "conv-b", dept.DepartmentId);
        await GenesysTicketAsync(f, "conv-c", dept.DepartmentId);

        // Asking from conv-c: conv-b (also Genesys-created) is the newest remaining ticket.
        var r = (await f.CustomerLookup.LookUpAsync(Phone, "conv-c"))!;

        Assert.Equal(middle, r.LatestPreviousTicket!.TicketId);
    }

    [Fact]
    public async Task ManuallyCreatedPreviousTicket_IsReturned_WhenItIsTheNewestBesideTheCurrentOne()
    {
        var f = NewFixture();
        var (dept, category) = f.SeedGenesysDepartment("Customer Service", "CS");
        await GenesysTicketAsync(f, "conv-old", dept.DepartmentId);
        var manual = await ManualTicketAsync(f, dept.DepartmentId, category.CategoryId);
        await GenesysTicketAsync(f, "conv-now", dept.DepartmentId);

        var r = (await f.CustomerLookup.LookUpAsync(Phone, "conv-now"))!;

        Assert.Equal(manual, r.LatestPreviousTicket!.TicketId);
    }

    [Fact]
    public async Task NoPreviousTicket_ReturnsAClearResponse()
    {
        var f = NewFixture();
        var (dept, _) = f.SeedGenesysDepartment("Customer Service", "CS");
        await GenesysTicketAsync(f, "conv-only", dept.DepartmentId);

        var r = (await f.CustomerLookup.LookUpAsync(Phone, "conv-only"))!;

        Assert.Null(r.LatestPreviousTicket);
        Assert.True(r.NoPreviousTicketFound);
        Assert.Equal(string.Empty, r.ScreenPop.LatestPreviousTicketNumber);
        Assert.Contains("No previous ticket", r.ScreenPop.LatestPreviousTicketMessage);
    }

    [Fact]
    public async Task CurrentTicket_IsNeverModified_AndTheTicketListIsUnchanged()
    {
        var f = NewFixture();
        var (dept, _) = f.SeedGenesysDepartment("Customer Service", "CS");
        await GenesysTicketAsync(f, "conv-1", dept.DepartmentId);
        var current = await GenesysTicketAsync(f, "conv-2", dept.DepartmentId);
        var before = f.Tickets.All.Single(t => t.TicketId == current).TicketStatus;

        var withConversation = (await f.CustomerLookup.LookUpAsync(Phone, "conv-2"))!;
        var without = (await f.CustomerLookup.LookUpAsync(Phone))!;

        Assert.Equal(2, f.Tickets.All.Count());
        Assert.Equal(before, f.Tickets.All.Single(t => t.TicketId == current).TicketStatus);
        // Call-pickup behaviour is untouched: the plain lookup still lists both tickets.
        Assert.Equal(2, without.Tickets.Count);
        Assert.Equal(2, withConversation.Tickets.Count);
        Assert.Null(without.LatestPreviousTicket);
        Assert.False(without.NoPreviousTicketFound);
        Assert.Equal(string.Empty, without.ScreenPop.LatestPreviousTicketMessage);
    }
}

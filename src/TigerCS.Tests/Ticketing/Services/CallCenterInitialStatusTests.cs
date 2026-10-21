using TigerCS.Application.Modules.GenesysIntegration.Dto;
using TigerCS.Application.Modules.Ticketing.Dto;
using TigerCS.Domain.Modules.SlaAndEscalation;
using TigerCS.Domain.Modules.Ticketing;
using TigerCS.Tests.GenesysIntegration.Fakes;

namespace TigerCS.Tests.Ticketing.Services;

/// <summary>A ticket that originates in the Call Center department starts InProgress; every other department keeps Open — on the manual and the Genesys creation paths alike.</summary>
public class CallCenterInitialStatusTests
{
    private static readonly Guid Agent = Guid.NewGuid();

    private static async Task<Ticket> CreateManualAsync(GenesysServiceFixture f, string code)
    {
        var (department, category) = f.SeedGenesysDepartment(code == "CC" ? "Call Center" : "Customer Service", code);
        var intake = new IntakeRecord(
            WellKnownChannels.FaceToFaceKiosk, "+971500000077", department.DepartmentId, false, null, null, Agent, DateTime.UtcNow);
        await f.IntakeRecords.AddAsync(intake);

        var result = await f.ManualTicketCreation.CreateAsync(
            Agent, new CreateTicketRequestDto(intake.IntakeRecordId, null, null, category.CategoryId, (byte)PriorityLevel.High, "Walk-in"));
        Assert.Equal(TicketCreationOutcome.Success, result.Outcome);
        return f.Tickets.All.Single();
    }

    [Fact]
    public async Task ManualCallCenterTicket_StartsInProgress_AndHistoryRecordsIt()
    {
        var f = new GenesysServiceFixture();

        var ticket = await CreateManualAsync(f, "CC");

        Assert.Equal(TicketStatus.InProgress, ticket.TicketStatus);
        Assert.Equal(ticket.CurrentDepartmentId, ticket.OriginatingDepartmentId);
        Assert.Contains(f.StatusHistory.Added, h =>
            h.TicketId == ticket.TicketId && h.Dimension == TicketStatusDimension.TicketStatus && h.OldValue is null
            && h.NewValue == (byte)TicketStatus.InProgress);
    }

    [Fact]
    public async Task ManualTicketOfAnotherDepartment_StillStartsOpen()
    {
        var f = new GenesysServiceFixture();

        var ticket = await CreateManualAsync(f, "CS");

        Assert.Equal(TicketStatus.Open, ticket.TicketStatus);
    }

    [Theory]
    [InlineData("CC", TicketStatus.InProgress)]
    [InlineData("cc", TicketStatus.InProgress)]
    [InlineData("CS", TicketStatus.Open)]
    public async Task GenesysTicket_StartsInProgressOnlyForTheCallCenter(string code, TicketStatus expected)
    {
        var f = new GenesysServiceFixture();
        var (department, _) = f.SeedGenesysDepartment("Dept", code);

        var result = await f.Ingestion.IngestAsync(
            Agent, new GenesysInquiryDto("conv-1", GenesysChannel.WebsiteChat, CustomerPhone: "+971500000001", DepartmentId: department.DepartmentId));

        Assert.Equal(GenesysIngestionOutcome.TicketCreated, result.Outcome);
        Assert.Equal(expected, f.Tickets.All.Single().TicketStatus);
    }

    [Fact]
    public async Task ATransferredCallCenterTicket_KeepsItsOriginatingDepartment()
    {
        var f = new GenesysServiceFixture();
        var ticket = await CreateManualAsync(f, "CC");
        var other = f.Departments.AddDepartment("Other", "OT");

        ticket.TransferToDepartment(other.DepartmentId);

        Assert.Equal(other.DepartmentId, ticket.CurrentDepartmentId);
        Assert.NotEqual(other.DepartmentId, ticket.OriginatingDepartmentId);
        Assert.Equal(TicketStatus.InProgress, ticket.TicketStatus);
    }

    [Fact]
    public void StartInProgressAtCreation_IsRefusedOnAPersistedOrNonOpenTicket()
    {
        var created = Ticket.CreateUnclassified("TG-CC-1", 1, "x", DateTime.UtcNow);
        created.StartInProgressAtCreation();
        Assert.Equal(TicketStatus.InProgress, created.TicketStatus);
        Assert.Throws<InvalidTicketStatusTransitionException>(() => created.StartInProgressAtCreation());

        var persisted = Ticket.CreateUnclassified("TG-CC-2", 1, "x", DateTime.UtcNow);
        typeof(Ticket).GetProperty(nameof(Ticket.TicketId))!.SetValue(persisted, 5L);
        Assert.Throws<InvalidTicketStatusTransitionException>(() => persisted.StartInProgressAtCreation());
    }
}

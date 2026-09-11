using TigerCS.Application.Modules.GenesysIntegration.Dto;
using TigerCS.Application.Modules.Ticketing.Dto;
using TigerCS.Domain.Modules.IdentityAndAccess;
using TigerCS.Domain.Modules.SlaAndEscalation;
using TigerCS.Domain.Modules.Ticketing;
using TigerCS.Tests.GenesysIntegration.Fakes;

namespace TigerCS.Tests.GenesysIntegration.Services;

/// <summary>
/// Genesys agent identity mapping and interaction ownership, against the
/// REAL ingestion, update and agent-context services (see
/// <see cref="GenesysServiceFixture"/>): a Genesys agent is resolved to a
/// Ticketing user by the immutable Genesys User ID and nothing else, an
/// unmapped agent is a controlled outcome rather than a provisioned user,
/// and the resolved user is recorded on the interaction — never on the
/// ticket's assignment.
/// </summary>
public class GenesysAgentMappingTests
{
    private static readonly Guid ServiceAccount = Guid.NewGuid();
    private const string GenesysUserId = "6f1d2c3b-0a9e-4b8d-9c7f-1e2d3c4b5a69";

    private static GenesysInquiryDto Inquiry(string conversationId, int departmentId, string? agentId = null, string? agentName = null) =>
        new(conversationId, GenesysChannel.Phone,
            CustomerPhone: "+971500000001", DepartmentId: departmentId, AgentId: agentId, AgentName: agentName);

    // ---- Mapping: Genesys User ID → Ticketing user ----

    [Fact]
    public async Task KnownGenesysUserId_ResolvesTheMappedTicketingUser_WithRolesAndDepartments()
    {
        var f = new GenesysServiceFixture();
        var (department, _) = f.SeedGenesysDepartment("Customer Service", "CS");
        var userId = f.MapAgent(GenesysUserId, "agent@tigerproperties.ae", roles: Roles.CsAgent);
        f.DepartmentAssignments.Assignments.Add(
            new UserDepartmentAssignment(userId, department.DepartmentId, isPrimary: true, DateTime.UtcNow, assignedByEmployeeId: null));

        var result = await f.AgentResolution.ResolveByGenesysUserIdAsync(GenesysUserId);

        Assert.Equal(GenesysAgentResolutionOutcome.Success, result.Outcome);
        Assert.True(result.IsResolved);
        Assert.Equal(userId, result.Agent!.UserId);
        Assert.Equal(GenesysUserId, result.Agent.GenesysUserId);
        Assert.Equal(Roles.CsAgent, Assert.Single(result.Roles!));
        Assert.Equal(department.DepartmentId, Assert.Single(result.DepartmentIds!));
    }

    [Fact]
    public async Task KnownGenesysUserId_IsMatchedAfterTrimming()
    {
        var f = new GenesysServiceFixture();
        var userId = f.MapAgent(GenesysUserId);

        var result = await f.AgentResolution.ResolveByGenesysUserIdAsync($"  {GenesysUserId}  ");

        Assert.Equal(GenesysAgentResolutionOutcome.Success, result.Outcome);
        Assert.Equal(userId, result.Agent!.UserId);
    }

    [Fact]
    public async Task UnknownGenesysUserId_IsNotMapped_AndProvisionsNothing()
    {
        var f = new GenesysServiceFixture();
        f.MapAgent(GenesysUserId);

        var result = await f.AgentResolution.ResolveByGenesysUserIdAsync("00000000-1111-2222-3333-444444444444");

        // A distinct, controlled outcome — not "user not found", and not a
        // silently created account.
        Assert.Equal(GenesysAgentResolutionOutcome.NotMapped, result.Outcome);
        Assert.Null(result.Agent);
        Assert.Single(f.AgentMappings.All);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task MissingGenesysUserId_IsInvalid_NotNotMapped(string? genesysUserId)
    {
        var f = new GenesysServiceFixture();

        var result = await f.AgentResolution.ResolveByGenesysUserIdAsync(genesysUserId);

        Assert.Equal(GenesysAgentResolutionOutcome.Invalid, result.Outcome);
        Assert.Null(result.Agent);
    }

    [Fact]
    public async Task OverlongGenesysUserId_IsInvalid()
    {
        var f = new GenesysServiceFixture();

        var result = await f.AgentResolution.ResolveByGenesysUserIdAsync(new string('a', 65));

        Assert.Equal(GenesysAgentResolutionOutcome.Invalid, result.Outcome);
    }

    [Fact]
    public async Task EmailMatchAlone_NeverResolvesAnAgent()
    {
        var f = new GenesysServiceFixture();
        f.MapAgent(GenesysUserId, "agent@tigerproperties.ae");

        // The email is right; the Genesys User ID is not. Email is
        // informational, never an identity key.
        var byEmailAsId = await f.AgentResolution.ResolveByGenesysUserIdAsync("agent@tigerproperties.ae");
        var wrongId = await f.AgentResolution.ResolveByGenesysUserIdAsync("11111111-2222-3333-4444-555555555555");

        Assert.Equal(GenesysAgentResolutionOutcome.NotMapped, byEmailAsId.Outcome);
        Assert.Equal(GenesysAgentResolutionOutcome.NotMapped, wrongId.Outcome);
    }

    [Fact]
    public async Task DeactivatedMappedUser_IsInactive_NotSuccess()
    {
        var f = new GenesysServiceFixture();
        f.MapAgent(GenesysUserId, isActive: false);

        var result = await f.AgentResolution.ResolveByGenesysUserIdAsync(GenesysUserId);

        Assert.Equal(GenesysAgentResolutionOutcome.Inactive, result.Outcome);
        Assert.Null(result.Agent);
    }

    [Fact]
    public void MappingTheSameGenesysUserIdTwice_IsRejected_AsTheUniqueIndexRejectsIt()
    {
        var f = new GenesysServiceFixture();
        f.MapAgent(GenesysUserId);

        // The fake mirrors UX_AspNetUsers_GenesysUserId; the real index is
        // proven in GenesysAgentMappingPersistenceTests.
        Assert.Throws<InvalidOperationException>(() => f.MapAgent(GenesysUserId));
        Assert.Single(f.AgentMappings.All);
    }

    // ---- Interaction ownership on the existing event contracts ----

    [Fact]
    public async Task Ingest_WithAMappedAgent_PersistsGenesysAgentUserId_HandledByUserId_AndConversationId()
    {
        var f = new GenesysServiceFixture();
        var (department, _) = f.SeedGenesysDepartment("Customer Service", "CS");
        var userId = f.MapAgent(GenesysUserId);

        var result = await f.Ingestion.IngestAsync(
            ServiceAccount, Inquiry("conv-owned", department.DepartmentId, agentId: GenesysUserId, agentName: "Layla"));

        Assert.Equal(GenesysIngestionOutcome.TicketCreated, result.Outcome);
        var interaction = Assert.Single(f.Interactions.All);
        Assert.Equal("conv-owned", interaction.GenesysConversationId);
        Assert.Equal(GenesysUserId, interaction.GenesysAgentUserId);
        Assert.Equal(userId, interaction.HandledByUserId);
        // The verbatim agent context is still what it always was.
        Assert.Equal(GenesysUserId, interaction.GenesysAgentId);
        Assert.Equal("Layla", interaction.GenesysAgentName);

        // Ownership is "who handled the interaction" — the ticket's own
        // assignment is untouched by it.
        var ticket = Assert.Single(f.Tickets.All);
        Assert.Null(ticket.CurrentOwnerEmployeeId);

        var audit = Assert.Single(f.Audit.Entries, a => a.Action == "GenesysInquiryIngested");
        Assert.Contains($"AgentMapping=Resolved(HandledByUserId={userId})", audit.AfterValue);
    }

    [Fact]
    public async Task Ingest_WithAnUnmappedAgent_StillCreatesTheTicket_WithNoOwner_AndSaysSoOnTheAudit()
    {
        var f = new GenesysServiceFixture();
        var (department, _) = f.SeedGenesysDepartment("Customer Service", "CS");

        var result = await f.Ingestion.IngestAsync(
            ServiceAccount, Inquiry("conv-unmapped", department.DepartmentId, agentId: "ga-unknown", agentName: "Somebody"));

        // The inquiry is never lost over a mapping gap — but the gap is not
        // hidden either: the verbatim agent id is kept, ownership is null,
        // and the audit entry names the NotMapped outcome.
        Assert.Equal(GenesysIngestionOutcome.TicketCreated, result.Outcome);
        var interaction = Assert.Single(f.Interactions.All);
        Assert.Equal("ga-unknown", interaction.GenesysAgentId);
        Assert.Null(interaction.GenesysAgentUserId);
        Assert.Null(interaction.HandledByUserId);
        Assert.Empty(f.AgentMappings.All);

        var audit = Assert.Single(f.Audit.Entries, a => a.Action == "GenesysInquiryIngested");
        Assert.Contains("AgentMapping=NotMapped(GenesysUserId=ga-unknown)", audit.AfterValue);
    }

    [Fact]
    public async Task Ingest_WithoutAnAgent_LeavesOwnershipNull()
    {
        var f = new GenesysServiceFixture();
        var (department, _) = f.SeedGenesysDepartment("Customer Service", "CS");

        var result = await f.Ingestion.IngestAsync(ServiceAccount, Inquiry("conv-noagent", department.DepartmentId));

        Assert.Equal(GenesysIngestionOutcome.TicketCreated, result.Outcome);
        var interaction = Assert.Single(f.Interactions.All);
        Assert.Null(interaction.GenesysAgentUserId);
        Assert.Null(interaction.HandledByUserId);
        var audit = Assert.Single(f.Audit.Entries, a => a.Action == "GenesysInquiryIngested");
        Assert.Contains("AgentMapping=(no agent)", audit.AfterValue);
    }

    [Fact]
    public async Task Update_NamingAMappedAgent_RecordsOwnership_ApplyIfAbsent()
    {
        var f = new GenesysServiceFixture();
        var (department, _) = f.SeedGenesysDepartment("Customer Service", "CS");
        var first = f.MapAgent(GenesysUserId);
        var second = f.MapAgent("second-agent-genesys-id");

        var created = await f.Ingestion.IngestAsync(ServiceAccount, Inquiry("conv-late", department.DepartmentId));
        var ticketId = created.Ticket!.TicketId;

        var applied = await f.TicketUpdate.UpdateAsync(
            ServiceAccount, ticketId, new GenesysTicketUpdateDto("conv-late", AgentId: GenesysUserId, AgentName: "Layla"));
        Assert.Equal(GenesysTicketUpdateOutcome.Applied, applied.Outcome);

        var interaction = f.InteractionFor("conv-late")!;
        Assert.Equal(first, interaction.HandledByUserId);
        Assert.Equal(GenesysUserId, interaction.GenesysAgentUserId);

        // A later, different agent never overwrites the recorded handler —
        // the same apply-if-absent rule the verbatim agent context follows.
        await f.TicketUpdate.UpdateAsync(
            ServiceAccount, ticketId, new GenesysTicketUpdateDto("conv-late", AgentId: "second-agent-genesys-id"));
        Assert.Equal(first, interaction.HandledByUserId);
        Assert.NotEqual(second, interaction.HandledByUserId);
    }

    [Fact]
    public async Task ManualTicketCreation_StillRecordsALocalInteraction_WithNoOwnership()
    {
        var f = new GenesysServiceFixture();
        var (department, category) = f.SeedGenesysDepartment("Facilities", "FM");
        var intake = new IntakeRecord(
            WellKnownChannels.FaceToFaceKiosk, "+971500000077", department.DepartmentId,
            false, null, null, ServiceAccount, DateTime.UtcNow);
        await f.IntakeRecords.AddAsync(intake);

        var creation = await f.ManualTicketCreation.CreateAsync(
            ServiceAccount,
            new CreateTicketRequestDto(
                intake.IntakeRecordId, null, null, category.CategoryId, (byte)PriorityLevel.High, "Walk-in: lift out of service"));

        Assert.Equal(TicketCreationOutcome.Success, creation.Outcome);
        var interaction = Assert.Single(f.Interactions.All);
        Assert.Equal(InteractionContextSource.Ticketing, interaction.Source);
        Assert.Null(interaction.GenesysConversationId);
        Assert.Null(interaction.GenesysAgentUserId);
        Assert.Null(interaction.HandledByUserId);
    }

    // ---- The agent action: agent-context ----

    [Fact]
    public async Task AgentContext_MappedAgentWithConversation_ResolvesTheUser_AndRecordsOwnership()
    {
        var f = new GenesysServiceFixture();
        var (department, _) = f.SeedGenesysDepartment("Customer Service", "CS");
        var userId = f.MapAgent(GenesysUserId, "agent@tigerproperties.ae", roles: Roles.CsAgent);
        var created = await f.Ingestion.IngestAsync(ServiceAccount, Inquiry("conv-ctx", department.DepartmentId));

        var result = await f.AgentContext.ResolveAsync(
            ServiceAccount, new GenesysAgentContextDto(GenesysUserId, "agent@tigerproperties.ae", "conv-ctx"));

        Assert.Equal(GenesysAgentContextOutcome.Resolved, result.Outcome);
        Assert.Equal(userId, result.Agent!.UserId);
        Assert.Equal(created.Ticket!.TicketId, result.TicketId);
        Assert.Equal(created.Ticket.TicketNumber, result.TicketNumber);
        Assert.Equal(userId, result.HandledByUserId);

        var interaction = f.InteractionFor("conv-ctx")!;
        Assert.Equal(userId, interaction.HandledByUserId);
        Assert.Equal(GenesysUserId, interaction.GenesysAgentUserId);
        Assert.Equal(interaction.TicketInteractionId, result.TicketInteractionId);

        // Recorded on the interaction, audited, and NOT on the ticket.
        var audit = Assert.Single(f.Audit.Entries, a => a.Action == "GenesysInteractionHandlerRecorded");
        Assert.Contains($"HandledByUserId={userId}", audit.AfterValue);
        Assert.Null(Assert.Single(f.Tickets.All).CurrentOwnerEmployeeId);
    }

    [Fact]
    public async Task AgentContext_IsIdempotent_ASecondCallRecordsNothingTwice()
    {
        var f = new GenesysServiceFixture();
        var (department, _) = f.SeedGenesysDepartment("Customer Service", "CS");
        var userId = f.MapAgent(GenesysUserId);
        await f.Ingestion.IngestAsync(ServiceAccount, Inquiry("conv-twice", department.DepartmentId));

        await f.AgentContext.ResolveAsync(ServiceAccount, new GenesysAgentContextDto(GenesysUserId, ConversationId: "conv-twice"));
        var again = await f.AgentContext.ResolveAsync(ServiceAccount, new GenesysAgentContextDto(GenesysUserId, ConversationId: "conv-twice"));

        Assert.Equal(GenesysAgentContextOutcome.Resolved, again.Outcome);
        Assert.Equal(userId, again.HandledByUserId);
        Assert.Single(f.Audit.Entries, a => a.Action == "GenesysInteractionHandlerRecorded");
    }

    [Fact]
    public async Task AgentContext_WithoutAConversation_ResolvesIdentityOnly()
    {
        var f = new GenesysServiceFixture();
        var userId = f.MapAgent(GenesysUserId, roles: Roles.CsSupervisor);

        var result = await f.AgentContext.ResolveAsync(ServiceAccount, new GenesysAgentContextDto(GenesysUserId));

        Assert.Equal(GenesysAgentContextOutcome.Resolved, result.Outcome);
        Assert.Equal(userId, result.Agent!.UserId);
        Assert.Equal(Roles.CsSupervisor, Assert.Single(result.Roles!));
        Assert.Null(result.TicketId);
        Assert.Null(result.TicketInteractionId);
        Assert.Empty(f.Audit.Entries);
    }

    [Fact]
    public async Task AgentContext_UnmappedAgent_IsRefused_AndNothingIsWritten()
    {
        var f = new GenesysServiceFixture();
        var (department, _) = f.SeedGenesysDepartment("Customer Service", "CS");
        await f.Ingestion.IngestAsync(ServiceAccount, Inquiry("conv-refused", department.DepartmentId));
        f.Audit.Entries.Clear();

        var result = await f.AgentContext.ResolveAsync(
            ServiceAccount, new GenesysAgentContextDto("not-a-mapped-agent", "someone@tigerproperties.ae", "conv-refused"));

        Assert.Equal(GenesysAgentContextOutcome.AgentNotMapped, result.Outcome);
        Assert.Null(f.InteractionFor("conv-refused")!.HandledByUserId);
        Assert.Empty(f.AgentMappings.All);
        Assert.Empty(f.Audit.Entries);
    }

    [Fact]
    public async Task AgentContext_DeactivatedAgent_IsRefusedAsInactive()
    {
        var f = new GenesysServiceFixture();
        f.MapAgent(GenesysUserId, isActive: false);

        var result = await f.AgentContext.ResolveAsync(ServiceAccount, new GenesysAgentContextDto(GenesysUserId));

        Assert.Equal(GenesysAgentContextOutcome.AgentInactive, result.Outcome);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public async Task AgentContext_MissingGenesysUserId_IsAValidationFailure(string? genesysUserId)
    {
        var f = new GenesysServiceFixture();

        var result = await f.AgentContext.ResolveAsync(ServiceAccount, new GenesysAgentContextDto(genesysUserId, ConversationId: "conv-x"));

        Assert.Equal(GenesysAgentContextOutcome.AgentIdRequired, result.Outcome);
    }

    [Fact]
    public async Task AgentContext_UnknownConversation_IsConversationNotFound()
    {
        var f = new GenesysServiceFixture();
        f.MapAgent(GenesysUserId);

        var result = await f.AgentContext.ResolveAsync(ServiceAccount, new GenesysAgentContextDto(GenesysUserId, ConversationId: "conv-never"));

        Assert.Equal(GenesysAgentContextOutcome.ConversationNotFound, result.Outcome);
    }

    [Fact]
    public async Task AgentContext_WithTheIntegrationOff_IsRefused()
    {
        var f = new GenesysServiceFixture(enabled: false);
        f.MapAgent(GenesysUserId);

        var result = await f.AgentContext.ResolveAsync(ServiceAccount, new GenesysAgentContextDto(GenesysUserId));

        Assert.Equal(GenesysAgentContextOutcome.IntegrationDisabled, result.Outcome);
    }

    // ---- Domain: the ownership record itself ----

    [Fact]
    public void RecordHandlingAgent_IsApplyIfAbsent()
    {
        var interaction = TicketInteraction.CreateFromGenesys(
            1, WellKnownChannels.Phone, "+971500000001", "conv-d", null, null, null, null, null, null, null, DateTime.UtcNow);
        var first = Guid.NewGuid();

        Assert.True(interaction.RecordHandlingAgentIfAbsent(GenesysUserId, first));
        Assert.False(interaction.RecordHandlingAgentIfAbsent("another-genesys-id", Guid.NewGuid()));

        Assert.Equal(first, interaction.HandledByUserId);
        Assert.Equal(GenesysUserId, interaction.GenesysAgentUserId);
    }

    [Fact]
    public void RecordHandlingAgent_RequiresBothHalvesOfThePair()
    {
        var interaction = TicketInteraction.CreateLocal(1, WellKnownChannels.FaceToFaceKiosk, null, DateTime.UtcNow);

        Assert.Throws<ArgumentException>(() => interaction.RecordHandlingAgentIfAbsent(" ", Guid.NewGuid()));
        Assert.Throws<ArgumentException>(() => interaction.RecordHandlingAgentIfAbsent(GenesysUserId, Guid.Empty));
        Assert.Null(interaction.HandledByUserId);
        Assert.Null(interaction.GenesysAgentUserId);
    }
}

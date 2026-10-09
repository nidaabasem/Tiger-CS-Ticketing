namespace TigerCS.Application.Modules.GenesysIntegration.Dto;

/// <summary>
/// The request type the bot (or the Architect flow) identified, as sent on
/// <c>POST /api/genesys/tickets</c> (initial ingestion) or in the
/// <c>requestType</c> part of <c>PATCH /api/genesys/tickets/{id}</c> (the bot
/// identifies it later). Either field; the id wins when both are sent.
/// </summary>
public sealed record GenesysRequestTypeDto(int? RequestTypeId = null, string? Name = null)
{
    public bool IsSupplied => RequestTypeId is not null || !string.IsNullOrWhiteSpace(Name);
}

public enum GenesysClassificationOutcome
{
    /// <summary>The request type was applied: department routing, automatic assignment and SLA policy followed.</summary>
    Classified,

    /// <summary>The ticket already carries exactly this request type — an idempotent repeat; nothing changed.</summary>
    AlreadyClassified,

    /// <summary>No request type supplied: the ticket stays awaiting classification, in the human follow-up queue.</summary>
    AwaitingClassification,

    /// <summary>The supplied request type does not exist, is inactive, is ambiguous, or cannot route; nothing was written.</summary>
    RequestTypeInvalid,

    /// <summary>The ticket already carries a different request type; a bot cannot silently reclassify it.</summary>
    RequestTypeConflict,

    TicketClosed,
    TicketNotFound,
    ConcurrencyConflict
}

public sealed record GenesysClassificationResult(
    GenesysClassificationOutcome Outcome,
    int? RequestTypeId = null,
    string? RequestTypeName = null,
    int? DepartmentId = null,
    bool Transferred = false,
    Guid? AssignedEmployeeId = null,
    string? AssignmentOutcome = null,
    string? SlaOutcome = null,
    string? HandoffStatus = null,
    string? Detail = null)
{
    public static GenesysClassificationResult Failure(GenesysClassificationOutcome outcome, string? detail = null) =>
        new(outcome, Detail: detail);

    /// <summary>The value reported on the wire: "Classified" or "AwaitingClassification".</summary>
    public string Status => Outcome switch
    {
        GenesysClassificationOutcome.Classified or GenesysClassificationOutcome.AlreadyClassified => "Classified",
        _ => "AwaitingClassification"
    };
}

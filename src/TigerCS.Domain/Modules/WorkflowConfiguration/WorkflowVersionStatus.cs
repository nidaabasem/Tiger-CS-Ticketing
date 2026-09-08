namespace TigerCS.Domain.Modules.WorkflowConfiguration;

/// <summary>
/// The lifecycle of one workflow version (<see cref="WorkflowTemplate"/>):
/// <c>Draft → Published → Historical</c>, strictly forward. Only a Draft is
/// editable; a Published version is read-only and is what new tickets pin
/// to; a Historical version is read-only and kept because existing tickets
/// still reference it. There is no "back to draft" — a change is always a
/// new version.
/// </summary>
public enum WorkflowVersionStatus : byte
{
    Draft = 1,
    Published = 2,
    Historical = 3
}

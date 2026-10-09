using TigerCS.Application.Modules.Ticketing.Abstractions;
using TigerCS.Domain.Modules.SlaAndEscalation;

namespace TigerCS.Application.Modules.GenesysIntegration.Services;

/// <summary>
/// Resolves the priority a new Genesys ticket starts with from configuration
/// (<see cref="GenesysOptions.DefaultTicketPriority"/>, "Normal" by default)
/// and the Priorities table — never from a hard-coded id. A priority row with
/// exactly the configured name wins; otherwise the documented business alias
/// (<see cref="PriorityAliases"/>: Normal is the Medium tier) is looked up by
/// <see cref="PriorityLevel"/>. Returns null when nothing resolves, which the
/// caller treats as "no default" (and audits) rather than losing the inquiry.
/// </summary>
public sealed class GenesysDefaultPriorityResolver(GenesysOptions options, IPriorityRepository priorityRepository)
{
    public async Task<Priority?> ResolveAsync(CancellationToken cancellationToken = default)
    {
        var name = options.DefaultTicketPriority;
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        if (await priorityRepository.GetByNameAsync(name, cancellationToken) is { } byName)
        {
            return byName;
        }

        return PriorityAliases.TryResolve(name, out var level)
            ? await priorityRepository.GetByIdAsync((byte)level, cancellationToken)
            : null;
    }
}

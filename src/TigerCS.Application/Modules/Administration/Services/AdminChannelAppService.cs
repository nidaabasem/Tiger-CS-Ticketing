using TigerCS.Application.Abstractions;
using TigerCS.Application.Modules.Administration.Dto;
using TigerCS.Application.Modules.Ticketing.Abstractions;
using TigerCS.Domain.Modules.Ticketing;

namespace TigerCS.Application.Modules.Administration.Services;

/// <summary>
/// Channel administration (Admin → Configuration → Channels). Channels are
/// never physically deleted — intake records and ticket interactions
/// reference them as the originating channel (both FKs Restrict) — so
/// deactivation is the only retirement path; an inactive channel stays
/// visible wherever history names it and simply disappears from the
/// Create Ticket channel list.
/// </summary>
public sealed class AdminChannelAppService(
    IChannelRepository channelRepository,
    ITicketingUnitOfWork unitOfWork,
    IAuditEntryWriter auditWriter)
{
    public async Task<IReadOnlyList<AdminChannelDto>> ListAsync(bool includeInactive, CancellationToken cancellationToken = default)
    {
        var channels = await channelRepository.ListAsync(activeOnly: !includeInactive, cancellationToken);
        var result = new List<AdminChannelDto>(channels.Count);
        foreach (var channel in channels)
        {
            result.Add(ToDto(channel, await channelRepository.CountReferencesAsync(channel.ChannelId, cancellationToken)));
        }

        return result;
    }

    public async Task<AdminChannelDto?> GetAsync(byte channelId, CancellationToken cancellationToken = default)
    {
        var channel = await channelRepository.GetByIdAsync(channelId, cancellationToken);
        return channel is null ? null : ToDto(channel, await channelRepository.CountReferencesAsync(channelId, cancellationToken));
    }

    public async Task<AdminResult<AdminChannelDto>> CreateAsync(
        Guid actorEmployeeId, SaveChannelRequestDto request, CancellationToken cancellationToken = default)
    {
        var errors = await ValidateAsync(request, excludeChannelId: null, cancellationToken);
        if (errors.Count > 0)
        {
            return AdminResult<AdminChannelDto>.Invalid(errors);
        }

        var channel = new Channel(
            request.Name, request.Code, request.RequiresPhone, request.IsGenesysEnabled, request.DisplayOrder, request.IsActive);
        await channelRepository.AddAsync(channel, cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        await auditWriter.WriteAsync(
            actorEmployeeId, "AdminCreateChannel", "Channel", channel.ChannelId.ToString(),
            beforeValue: null, afterValue: Describe(channel), Guid.NewGuid(), cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return AdminResult<AdminChannelDto>.Success((await GetAsync(channel.ChannelId, cancellationToken))!);
    }

    public async Task<AdminResult<AdminChannelDto>> UpdateAsync(
        Guid actorEmployeeId, byte channelId, SaveChannelRequestDto request, CancellationToken cancellationToken = default)
    {
        var channel = await channelRepository.GetByIdAsync(channelId, cancellationToken);
        if (channel is null)
        {
            return AdminResult<AdminChannelDto>.NotFound();
        }

        var errors = await ValidateAsync(request, channelId, cancellationToken);
        if (errors.Count > 0)
        {
            return AdminResult<AdminChannelDto>.Invalid(errors);
        }

        var before = Describe(channel);
        channel.Update(request.Name, request.Code, request.RequiresPhone, request.IsGenesysEnabled, request.DisplayOrder);
        if (request.IsActive)
        {
            channel.Activate();
        }
        else
        {
            channel.Deactivate();
        }

        await auditWriter.WriteAsync(
            actorEmployeeId, "AdminUpdateChannel", "Channel", channelId.ToString(),
            before, Describe(channel), Guid.NewGuid(), cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return AdminResult<AdminChannelDto>.Success((await GetAsync(channelId, cancellationToken))!);
    }

    public async Task<AdminResult<AdminChannelDto>> SetActivationAsync(
        Guid actorEmployeeId, byte channelId, SetActiveRequestDto request, CancellationToken cancellationToken = default)
    {
        var channel = await channelRepository.GetByIdAsync(channelId, cancellationToken);
        if (channel is null)
        {
            return AdminResult<AdminChannelDto>.NotFound();
        }

        var before = channel.IsActive;
        if (request.IsActive)
        {
            channel.Activate();
        }
        else
        {
            channel.Deactivate();
        }

        await auditWriter.WriteAsync(
            actorEmployeeId, request.IsActive ? "AdminActivateChannel" : "AdminDeactivateChannel", "Channel", channelId.ToString(),
            $"IsActive={before}", $"IsActive={channel.IsActive};Reason={request.Reason}", Guid.NewGuid(), cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return AdminResult<AdminChannelDto>.Success((await GetAsync(channelId, cancellationToken))!);
    }

    private async Task<List<string>> ValidateAsync(SaveChannelRequestDto request, byte? excludeChannelId, CancellationToken cancellationToken)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(request.Name))
        {
            errors.Add("Name is required.");
        }
        else if (request.Name.Trim().Length > Channel.NameMaxLength)
        {
            errors.Add($"Name must be at most {Channel.NameMaxLength} characters.");
        }

        if (string.IsNullOrWhiteSpace(request.Code))
        {
            errors.Add("Code is required.");
        }
        else if (request.Code.Trim().Length > Channel.CodeMaxLength)
        {
            errors.Add($"Code must be at most {Channel.CodeMaxLength} characters.");
        }
        else if (await channelRepository.CodeExistsAsync(request.Code.Trim(), excludeChannelId, cancellationToken))
        {
            errors.Add($"A channel with code '{request.Code.Trim()}' already exists.");
        }

        if (request.DisplayOrder < 0)
        {
            errors.Add("Display order must be zero or a positive whole number.");
        }

        return errors;
    }

    private static string Describe(Channel channel) =>
        $"Name={channel.Name};Code={channel.Code};RequiresPhone={channel.RequiresPhone};IsGenesysEnabled={channel.IsGenesysEnabled};IsActive={channel.IsActive};DisplayOrder={channel.DisplayOrder}";

    private static AdminChannelDto ToDto(Channel channel, int referenceCount) => new(
        channel.ChannelId, channel.Name, channel.Code, channel.RequiresPhone, channel.IsGenesysEnabled,
        channel.IsActive, channel.DisplayOrder, referenceCount);
}

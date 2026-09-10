using TigerCS.Application.Abstractions;
using TigerCS.Application.Modules.Administration.Dto;
using TigerCS.Application.Modules.GenesysIntegration.Abstractions;
using TigerCS.Application.Modules.IdentityAndAccess.Abstractions;
using TigerCS.Application.Modules.Ticketing.Abstractions;
using TigerCS.Domain.Modules.GenesysIntegration;

namespace TigerCS.Application.Modules.Administration.Services;

/// <summary>
/// Administration of the Genesys routing configuration: the Genesys Queue →
/// Department mapping, and each department's Genesys ticket category.
///
/// <para>
/// <b>This exists so nothing about routing is hard-coded.</b> Real Genesys
/// queue ids are not known yet, and none are seeded or invented — an
/// administrator enters them here once the Genesys team supplies them, and
/// the integration reads rows. A queue that has not been mapped resolves to
/// no department at all, which ingestion reports as a visible configuration
/// gap rather than papering over with a default.
/// </para>
///
/// <para>
/// Mappings are deactivated, never deleted, for the same reason every other
/// configuration table in this system is: an inquiry that arrived under a
/// mapping must stay explainable afterwards.
/// </para>
/// </summary>
public sealed class AdminGenesysRoutingAppService(
    IGenesysQueueMappingRepository queueMappingRepository,
    IGenesysDepartmentSettingsRepository departmentSettingsRepository,
    IDepartmentRepository departmentRepository,
    ICategoryRepository categoryRepository,
    ITicketingUnitOfWork unitOfWork,
    IAuditEntryWriter auditWriter,
    TimeProvider timeProvider)
{
    public async Task<IReadOnlyList<AdminGenesysQueueMappingDto>> ListQueueMappingsAsync(
        bool includeInactive, CancellationToken cancellationToken = default)
    {
        var mappings = await queueMappingRepository.ListAsync(includeInactive, cancellationToken);
        var departments = await departmentRepository.ListAsync(activeOnly: false, cancellationToken);
        var departmentNames = departments.ToDictionary(d => d.DepartmentId, d => d.Name);

        return mappings
            .Select(m => new AdminGenesysQueueMappingDto(
                m.GenesysQueueMappingId, m.QueueId, m.QueueName, m.DepartmentId,
                departmentNames.GetValueOrDefault(m.DepartmentId, $"#{m.DepartmentId}"), m.IsActive))
            .ToList();
    }

    public async Task<AdminResult<AdminGenesysQueueMappingDto>> CreateQueueMappingAsync(
        Guid actorEmployeeId, SaveGenesysQueueMappingRequestDto request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(request.QueueId))
        {
            errors.Add("Queue id is required.");
        }
        else if (request.QueueId.Trim().Length > GenesysQueueMapping.QueueIdMaxLength)
        {
            errors.Add($"Queue id must be at most {GenesysQueueMapping.QueueIdMaxLength} characters.");
        }
        else if (await queueMappingRepository.QueueIdExistsAsync(request.QueueId.Trim(), null, cancellationToken))
        {
            errors.Add($"Genesys queue '{request.QueueId.Trim()}' is already mapped. Edit the existing mapping instead.");
        }

        var department = await departmentRepository.GetByIdAsync(request.DepartmentId, cancellationToken);
        if (department is null)
        {
            errors.Add("Department not found.");
        }
        else if (!department.IsActive)
        {
            errors.Add($"Department '{department.Name}' is deactivated and cannot receive Genesys inquiries.");
        }

        if (errors.Count > 0)
        {
            return AdminResult<AdminGenesysQueueMappingDto>.Invalid(errors);
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var mapping = new GenesysQueueMapping(request.QueueId, request.QueueName, request.DepartmentId, now, request.IsActive);
        await queueMappingRepository.AddAsync(mapping, cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        await auditWriter.WriteAsync(
            actorEmployeeId, "AdminCreateGenesysQueueMapping", "GenesysQueueMapping", mapping.GenesysQueueMappingId.ToString(),
            beforeValue: null, afterValue: Describe(mapping), Guid.NewGuid(), cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return AdminResult<AdminGenesysQueueMappingDto>.Success(
            new AdminGenesysQueueMappingDto(
                mapping.GenesysQueueMappingId, mapping.QueueId, mapping.QueueName,
                mapping.DepartmentId, department!.Name, mapping.IsActive));
    }

    public async Task<AdminResult<AdminGenesysQueueMappingDto>> UpdateQueueMappingAsync(
        Guid actorEmployeeId, int genesysQueueMappingId, SaveGenesysQueueMappingRequestDto request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var mapping = await queueMappingRepository.GetByIdAsync(genesysQueueMappingId, cancellationToken);
        if (mapping is null)
        {
            return AdminResult<AdminGenesysQueueMappingDto>.NotFound();
        }

        var department = await departmentRepository.GetByIdAsync(request.DepartmentId, cancellationToken);
        if (department is null)
        {
            return AdminResult<AdminGenesysQueueMappingDto>.Invalid("Department not found.");
        }

        if (!department.IsActive)
        {
            return AdminResult<AdminGenesysQueueMappingDto>.Invalid(
                $"Department '{department.Name}' is deactivated and cannot receive Genesys inquiries.");
        }

        var before = Describe(mapping);
        // The queue id is the mapping's identity and is never edited — a
        // different queue is a different mapping, so history stays readable.
        mapping.Update(request.QueueName, request.DepartmentId, request.IsActive, timeProvider.GetUtcNow().UtcDateTime);

        await auditWriter.WriteAsync(
            actorEmployeeId, "AdminUpdateGenesysQueueMapping", "GenesysQueueMapping", mapping.GenesysQueueMappingId.ToString(),
            before, Describe(mapping), Guid.NewGuid(), cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return AdminResult<AdminGenesysQueueMappingDto>.Success(
            new AdminGenesysQueueMappingDto(
                mapping.GenesysQueueMappingId, mapping.QueueId, mapping.QueueName,
                mapping.DepartmentId, department.Name, mapping.IsActive));
    }

    public async Task<IReadOnlyList<AdminGenesysDepartmentSettingsDto>> ListDepartmentSettingsAsync(
        bool includeInactive, CancellationToken cancellationToken = default)
    {
        var settings = await departmentSettingsRepository.ListAsync(includeInactive, cancellationToken);
        var departments = await departmentRepository.ListAsync(activeOnly: false, cancellationToken);
        var departmentNames = departments.ToDictionary(d => d.DepartmentId, d => d.Name);

        var result = new List<AdminGenesysDepartmentSettingsDto>(settings.Count);
        foreach (var row in settings)
        {
            var category = await categoryRepository.GetByIdAsync(row.DefaultCategoryId, cancellationToken);
            result.Add(new AdminGenesysDepartmentSettingsDto(
                row.DepartmentId,
                departmentNames.GetValueOrDefault(row.DepartmentId, $"#{row.DepartmentId}"),
                row.DefaultCategoryId,
                category?.Name,
                row.IsActive));
        }

        return result;
    }

    /// <summary>
    /// Creates or replaces one department's Genesys settings (one row per
    /// department, so this is deliberately an upsert rather than separate
    /// create/edit endpoints). The category must be active AND belong to the
    /// same department — a Genesys ticket must never be filed under another
    /// department's category.
    /// </summary>
    public async Task<AdminResult<AdminGenesysDepartmentSettingsDto>> SaveDepartmentSettingsAsync(
        Guid actorEmployeeId, int departmentId, SaveGenesysDepartmentSettingsRequestDto request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var department = await departmentRepository.GetByIdAsync(departmentId, cancellationToken);
        if (department is null)
        {
            return AdminResult<AdminGenesysDepartmentSettingsDto>.NotFound();
        }

        var category = await categoryRepository.GetByIdAsync(request.DefaultCategoryId, cancellationToken);
        if (category is null || !category.IsActive)
        {
            return AdminResult<AdminGenesysDepartmentSettingsDto>.Invalid("The default category was not found, or is deactivated.");
        }

        if (category.DepartmentId != departmentId)
        {
            return AdminResult<AdminGenesysDepartmentSettingsDto>.Invalid(
                $"Category '{category.Name}' belongs to another department — a Genesys ticket is never filed under another department's category.");
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var existing = await departmentSettingsRepository.GetByDepartmentIdAsync(departmentId, cancellationToken);
        string? before = null;

        if (existing is null)
        {
            existing = new GenesysDepartmentSettings(departmentId, request.DefaultCategoryId, now, request.IsActive);
            await departmentSettingsRepository.AddAsync(existing, cancellationToken);
        }
        else
        {
            before = Describe(existing);
            existing.Update(request.DefaultCategoryId, request.IsActive, now);
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);

        await auditWriter.WriteAsync(
            actorEmployeeId, "AdminSaveGenesysDepartmentSettings", "GenesysDepartmentSettings", departmentId.ToString(),
            before, Describe(existing), Guid.NewGuid(), cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return AdminResult<AdminGenesysDepartmentSettingsDto>.Success(
            new AdminGenesysDepartmentSettingsDto(
                departmentId, department.Name, existing.DefaultCategoryId, category.Name, existing.IsActive));
    }

    private static string Describe(GenesysQueueMapping mapping) =>
        $"QueueId={mapping.QueueId};QueueName={mapping.QueueName ?? "(none)"};DepartmentId={mapping.DepartmentId};IsActive={mapping.IsActive}";

    private static string Describe(GenesysDepartmentSettings settings) =>
        $"DepartmentId={settings.DepartmentId};DefaultCategoryId={settings.DefaultCategoryId};IsActive={settings.IsActive}";
}

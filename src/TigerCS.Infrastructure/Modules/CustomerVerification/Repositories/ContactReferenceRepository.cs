using Microsoft.EntityFrameworkCore;
using TigerCS.Application.Modules.CustomerVerification.Abstractions;
using TigerCS.Domain.Modules.CustomerVerification;
using TigerCS.Infrastructure.Persistence;

namespace TigerCS.Infrastructure.Modules.CustomerVerification.Repositories;

public sealed class ContactReferenceRepository(TigerCsDbContext dbContext) : IContactReferenceRepository
{
    public async Task<IReadOnlyList<ContactReference>> GetByUnitReferenceIdAsync(
        int unitReferenceId, CancellationToken cancellationToken = default) =>
        await dbContext.ContactReferences
            .Where(c => c.UnitReferenceId == unitReferenceId)
            .ToListAsync(cancellationToken);

    public Task<ContactReference?> GetByCrmContactIdAsync(string crmContactId, CancellationToken cancellationToken = default) =>
        dbContext.ContactReferences.FirstOrDefaultAsync(c => c.CrmContactId == crmContactId, cancellationToken);

    public Task<ContactReference?> GetByIdAsync(int contactReferenceId, CancellationToken cancellationToken = default) =>
        dbContext.ContactReferences.FirstOrDefaultAsync(c => c.ContactReferenceId == contactReferenceId, cancellationToken);

    public async Task AddAsync(ContactReference contactReference, CancellationToken cancellationToken = default) =>
        await dbContext.ContactReferences.AddAsync(contactReference, cancellationToken);
}

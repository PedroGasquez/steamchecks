using Microsoft.EntityFrameworkCore;
using SteamTracker.Core.Abstractions;
using SteamTracker.Core.Entities;

namespace SteamTracker.Infrastructure.Persistence.Repositories;

public class TrackedItemRepository(SteamTrackerDbContext db) : ITrackedItemRepository
{
    public Task<TrackedItem?> GetByIdAsync(Guid id, CancellationToken ct = default)
        => db.TrackedItems.AsNoTracking().FirstOrDefaultAsync(t => t.Id == id, ct);

    public Task<bool> ExistsAsync(int appId, string marketHashName, CancellationToken ct = default)
        => db.TrackedItems.AsNoTracking()
            .AnyAsync(t => t.AppId == appId && t.MarketHashName == marketHashName, ct);

    public async Task<IReadOnlyList<TrackedItem>> GetActiveAsync(CancellationToken ct = default)
        => await db.TrackedItems.AsNoTracking().Where(t => t.IsActive).ToListAsync(ct);

    public async Task AddAsync(TrackedItem item, CancellationToken ct = default)
    {
        db.TrackedItems.Add(item);
        await db.SaveChangesAsync(ct);
    }
}

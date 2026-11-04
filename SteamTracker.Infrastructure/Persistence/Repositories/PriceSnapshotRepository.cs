using Microsoft.EntityFrameworkCore;
using SteamTracker.Core.Abstractions;
using SteamTracker.Core.Entities;

namespace SteamTracker.Infrastructure.Persistence.Repositories;

public class PriceSnapshotRepository(SteamTrackerDbContext db) : IPriceSnapshotRepository
{
    public async Task AddAsync(PriceSnapshot snapshot, CancellationToken ct = default)
    {
        db.PriceSnapshots.Add(snapshot);
        await db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<PriceSnapshot>> GetByItemAsync(
        Guid trackedItemId, int limit, CancellationToken ct = default)
        => await db.PriceSnapshots.AsNoTracking()
            .Where(s => s.TrackedItemId == trackedItemId)
            .OrderByDescending(s => s.CapturedAt)
            .Take(limit)
            .ToListAsync(ct);
}

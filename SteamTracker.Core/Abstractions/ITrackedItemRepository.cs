using SteamTracker.Core.Entities;

namespace SteamTracker.Core.Abstractions;

public interface ITrackedItemRepository
{
    Task<TrackedItem?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<bool> ExistsAsync(int appId, string marketHashName, CancellationToken ct = default);
    Task<IReadOnlyList<TrackedItem>> GetActiveAsync(CancellationToken ct = default);
    Task AddAsync(TrackedItem item, CancellationToken ct = default);
}

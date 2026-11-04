using SteamTracker.Core.Entities;

namespace SteamTracker.Core.Abstractions;

public interface IPriceSnapshotRepository
{
    Task AddAsync(PriceSnapshot snapshot, CancellationToken ct = default);

    /// <summary>Snapshots do item, do mais recente para o mais antigo.</summary>
    Task<IReadOnlyList<PriceSnapshot>> GetByItemAsync(Guid trackedItemId, int limit, CancellationToken ct = default);
}

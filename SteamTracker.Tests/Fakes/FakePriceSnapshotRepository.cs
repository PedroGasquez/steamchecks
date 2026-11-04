using SteamTracker.Core.Abstractions;
using SteamTracker.Core.Entities;

namespace SteamTracker.Tests.Fakes;

public sealed class FakePriceSnapshotRepository : IPriceSnapshotRepository
{
    public List<PriceSnapshot> Saved { get; } = [];

    public Task AddAsync(PriceSnapshot snapshot, CancellationToken ct = default)
    {
        Saved.Add(snapshot);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<PriceSnapshot>> GetByItemAsync(Guid trackedItemId, int limit, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<PriceSnapshot>>(
            Saved.Where(s => s.TrackedItemId == trackedItemId).Reverse().Take(limit).ToList());
}

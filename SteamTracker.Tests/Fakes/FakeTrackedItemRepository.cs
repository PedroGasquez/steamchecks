using SteamTracker.Core.Abstractions;
using SteamTracker.Core.Entities;

namespace SteamTracker.Tests.Fakes;

public sealed class FakeTrackedItemRepository : ITrackedItemRepository
{
    public List<TrackedItem> Items { get; } = [];

    /// <summary>Se não vazia, a próxima chamada de GetActiveAsync lança a próxima exceção.</summary>
    public Queue<Exception> GetActiveFailures { get; } = new();

    public int GetActiveCalls { get; private set; }

    public Task<TrackedItem?> GetByIdAsync(Guid id, CancellationToken ct = default) =>
        Task.FromResult(Items.FirstOrDefault(i => i.Id == id));

    public Task<bool> ExistsAsync(int appId, string marketHashName, CancellationToken ct = default) =>
        Task.FromResult(Items.Any(i => i.AppId == appId && i.MarketHashName == marketHashName));

    public Task<IReadOnlyList<TrackedItem>> GetActiveAsync(CancellationToken ct = default)
    {
        GetActiveCalls++;

        if (GetActiveFailures.Count > 0)
            throw GetActiveFailures.Dequeue();

        return Task.FromResult<IReadOnlyList<TrackedItem>>(Items.Where(i => i.IsActive).ToList());
    }

    public Task AddAsync(TrackedItem item, CancellationToken ct = default)
    {
        Items.Add(item);
        return Task.CompletedTask;
    }
}

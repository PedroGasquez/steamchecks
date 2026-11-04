using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Polly.CircuitBreaker;
using SteamTracker.Core.Abstractions;
using SteamTracker.Core.Entities;
using SteamTracker.Core.Enums;
using SteamTracker.Core.ValueObjects;
using SteamTracker.Infrastructure.Steam;
using SteamTracker.Tests.Fakes;
using SteamTracker.Worker;

namespace SteamTracker.Tests.Worker;

public class PriceCollectorTests
{
    private readonly FakeSteamMarketClient _client = new();
    private readonly FakeTrackedItemRepository _items = new();
    private readonly FakePriceSnapshotRepository _snapshots = new();

    private static MarketPriceResult ValidPrice() => new(new Money(10m, "BRL"), null, 5);

    private void AddItem(string name) =>
        _items.Items.Add(new TrackedItem(730, name, name, ItemType.Unknown));

    private PriceCollector CreateCollector(int currency = 7, TimeSpan? delay = null) =>
        new(_client, _items, _snapshots,
            Options.Create(new SteamMarketOptions { Currency = currency }),
            Options.Create(new PriceCollectionOptions { DelayBetweenItems = delay ?? TimeSpan.Zero }),
            NullLogger<PriceCollector>.Instance);

    [Fact]
    public async Task CollectAsync_ItensComPreco_GravaUmSnapshotPorItem()
    {
        AddItem("A");
        AddItem("B");
        _client.Responses["A"] = ValidPrice;
        _client.Responses["B"] = ValidPrice;

        var result = await CreateCollector().CollectAsync(CancellationToken.None);

        Assert.Equal(2, _snapshots.Saved.Count);
        Assert.Equal(new CollectionResult(2, 0, 0), result);
    }

    [Fact]
    public async Task CollectAsync_SteamRetornaNull_PulaItem()
    {
        AddItem("A");

        var result = await CreateCollector().CollectAsync(CancellationToken.None);

        Assert.Equal(new CollectionResult(0, 1, 0), result);
        Assert.Empty(_snapshots.Saved);
    }

    [Fact]
    public async Task CollectAsync_ResultadoSemLowestPrice_PulaItem()
    {
        AddItem("A");
        _client.Responses["A"] = () => new MarketPriceResult(null, null, null);

        var result = await CreateCollector().CollectAsync(CancellationToken.None);

        Assert.Equal(new CollectionResult(0, 1, 0), result);
        Assert.Empty(_snapshots.Saved);
    }

    [Fact]
    public async Task CollectAsync_SteamIndisponivelNumItem_ContinuaNosProximos()
    {
        AddItem("A");
        AddItem("B");
        _client.Responses["A"] = () => throw new SteamMarketUnavailableException(HttpStatusCode.TooManyRequests);
        _client.Responses["B"] = ValidPrice;

        var result = await CreateCollector().CollectAsync(CancellationToken.None);

        Assert.Equal(new CollectionResult(1, 0, 1), result);
        Assert.Equal(2, _client.Calls.Count);
    }

    [Fact]
    public async Task CollectAsync_CircuitoAberto_AbortaRestoDoCiclo()
    {
        AddItem("A");
        AddItem("B");
        AddItem("C");
        _client.Responses["A"] = ValidPrice;
        _client.Responses["B"] = () => throw new BrokenCircuitException();
        _client.Responses["C"] = ValidPrice;

        var result = await CreateCollector().CollectAsync(CancellationToken.None);

        Assert.Equal(new CollectionResult(1, 2, 0), result);
        Assert.Equal(2, _client.Calls.Count);
    }

    [Fact]
    public async Task CollectAsync_UsaMoedaConfigurada()
    {
        AddItem("A");
        AddItem("B");

        await CreateCollector(currency: 3).CollectAsync(CancellationToken.None);

        Assert.Equal(2, _client.Calls.Count);
        Assert.All(_client.Calls, call => Assert.Equal(3, call.Currency));
    }

    [Fact]
    public async Task CollectAsync_TokenCancelado_PropagaCancelamento()
    {
        using var cts = new CancellationTokenSource();
        AddItem("A");
        AddItem("B");
        _client.Responses["A"] = () =>
        {
            cts.Cancel();
            return ValidPrice();
        };

        var collector = CreateCollector(delay: TimeSpan.FromMinutes(10));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => collector.CollectAsync(cts.Token));
    }
}

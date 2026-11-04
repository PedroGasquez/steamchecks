using SteamTracker.Core.Abstractions;

namespace SteamTracker.Tests.Fakes;

/// <summary>Cliente fake: responde por marketHashName; chave ausente devolve null.</summary>
public sealed class FakeSteamMarketClient : ISteamMarketClient
{
    public List<(int AppId, string MarketHashName, int Currency)> Calls { get; } = [];

    public Dictionary<string, Func<MarketPriceResult?>> Responses { get; } = [];

    public Task<MarketPriceResult?> GetPriceAsync(
        int appId, string marketHashName, int currency, CancellationToken cancellationToken = default)
    {
        Calls.Add((appId, marketHashName, currency));

        return Task.FromResult(
            Responses.TryGetValue(marketHashName, out var response) ? response() : null);
    }
}

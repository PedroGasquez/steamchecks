using SteamTracker.Core.ValueObjects;

namespace SteamTracker.Core.Abstractions;

public interface ISteamMarketClient
{
    Task<MarketPriceResult?> GetPriceAsync(
        int appId,
        string marketHashName,
        int currency,
        CancellationToken cancellationToken = default);
}

public record MarketPriceResult(
    Money? LowestPrice,
    Money? MedianPrice,
    int? Volume);

using Microsoft.Extensions.Options;
using Polly.CircuitBreaker;
using Polly.Timeout;
using SteamTracker.Core.Abstractions;
using SteamTracker.Core.Entities;
using SteamTracker.Infrastructure.Steam;

namespace SteamTracker.Worker;

public sealed record CollectionResult(int Saved, int Skipped, int Failed);

/// <summary>Executa um ciclo de coleta: consulta a Steam para cada item ativo e grava um PriceSnapshot.</summary>
public sealed class PriceCollector(
    ISteamMarketClient client,
    ITrackedItemRepository items,
    IPriceSnapshotRepository snapshots,
    IOptions<SteamMarketOptions> steamOptions,
    IOptions<PriceCollectionOptions> collectionOptions,
    ILogger<PriceCollector> logger)
{
    public async Task<CollectionResult> CollectAsync(CancellationToken cancellationToken)
    {
        var active = await items.GetActiveAsync(cancellationToken);
        var currency = steamOptions.Value.Currency;
        var delay = collectionOptions.Value.DelayBetweenItems;

        int saved = 0, skipped = 0, failed = 0;

        for (var i = 0; i < active.Count; i++)
        {
            var item = active[i];

            // Pausa só entre itens (não antes do primeiro nem depois do último).
            if (i > 0 && delay > TimeSpan.Zero)
                await Task.Delay(delay, cancellationToken);

            try
            {
                var price = await client.GetPriceAsync(item.AppId, item.MarketHashName, currency, cancellationToken);

                if (price is null)
                {
                    logger.LogWarning("Steam não retornou preço para {Item}", item.MarketHashName);
                    skipped++;
                }
                else if (price.LowestPrice is null)
                {
                    logger.LogInformation("{Item} sem preço neste ciclo", item.MarketHashName);
                    skipped++;
                }
                else
                {
                    await snapshots.AddAsync(
                        new PriceSnapshot(item.Id, price.LowestPrice.Value, price.MedianPrice, price.Volume),
                        cancellationToken);
                    saved++;
                }
            }
            catch (BrokenCircuitException ex)
            {
                // Circuito aberto = Steam bloqueando; insistir aumenta o risco de shadow-ban.
                var remaining = active.Count - i;
                logger.LogWarning(ex, "Circuito aberto; abortando ciclo com {Remaining} itens restantes", remaining);
                skipped += remaining;
                break;
            }
            catch (Exception ex) when (ex is SteamMarketUnavailableException
                                           or HttpRequestException
                                           or TimeoutRejectedException)
            {
                logger.LogError(ex, "Falha ao consultar {Item}", item.MarketHashName);
                failed++;
            }
        }

        logger.LogInformation("Coleta concluída: {Saved} salvos, {Skipped} pulados, {Failed} falhas",
            saved, skipped, failed);

        return new CollectionResult(saved, skipped, failed);
    }
}

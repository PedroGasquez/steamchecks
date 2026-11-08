using Microsoft.Extensions.Options;

namespace SteamTracker.Worker;

/// <summary>Executa a coleta de preços periodicamente, sem morrer por falhas de um ciclo.</summary>
public class Worker(
    IServiceScopeFactory scopeFactory,
    IOptions<PriceCollectionOptions> options,
    ILogger<Worker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await RunCycleAsync(stoppingToken);

            using var timer = new PeriodicTimer(options.Value.Interval);
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                await RunCycleAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Desligamento normal: encerra em silêncio.
        }
    }

    private async Task RunCycleAsync(CancellationToken stoppingToken)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var collector = scope.ServiceProvider.GetRequiredService<PriceCollector>();
            await collector.CollectAsync(stoppingToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
        {
            logger.LogError(ex, "Ciclo de coleta falhou; tentando novamente no próximo intervalo");
        }
    }
}

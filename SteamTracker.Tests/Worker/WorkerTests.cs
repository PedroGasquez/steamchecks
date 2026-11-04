using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SteamTracker.Core.Abstractions;
using SteamTracker.Infrastructure.Steam;
using SteamTracker.Tests.Fakes;
using SteamTracker.Worker;
using WorkerService = SteamTracker.Worker.Worker;

namespace SteamTracker.Tests.Worker;

public class WorkerTests
{
    private readonly FakeTrackedItemRepository _items = new();

    private WorkerService CreateWorker()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<ITrackedItemRepository>(_items);
        services.AddSingleton<IPriceSnapshotRepository>(new FakePriceSnapshotRepository());
        services.AddSingleton<ISteamMarketClient>(new FakeSteamMarketClient());
        services.AddSingleton(Options.Create(new SteamMarketOptions()));
        services.AddSingleton(Options.Create(new PriceCollectionOptions
        {
            Interval = TimeSpan.FromMilliseconds(50),
            DelayBetweenItems = TimeSpan.Zero
        }));
        services.AddScoped<PriceCollector>();

        var provider = services.BuildServiceProvider();
        return new WorkerService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            provider.GetRequiredService<IOptions<PriceCollectionOptions>>(),
            provider.GetRequiredService<ILogger<WorkerService>>());
    }

    private async Task AguardarCiclosAsync(int minimo)
    {
        var limite = DateTime.UtcNow.AddSeconds(5);
        while (_items.GetActiveCalls < minimo)
        {
            if (DateTime.UtcNow > limite) return;
            await Task.Delay(10);
        }
    }

    [Fact]
    public async Task ExecuteAsync_RodaPrimeiroCicloAoIniciar()
    {
        var worker = CreateWorker();

        await worker.StartAsync(CancellationToken.None);
        try
        {
            await AguardarCiclosAsync(1);
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }

        Assert.True(_items.GetActiveCalls >= 1);
    }

    [Fact]
    public async Task ExecuteAsync_ExcecaoNumCiclo_ProximoCicloRoda()
    {
        _items.GetActiveFailures.Enqueue(new InvalidOperationException("banco fora do ar"));
        var worker = CreateWorker();

        await worker.StartAsync(CancellationToken.None);
        try
        {
            await AguardarCiclosAsync(2);
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }

        Assert.True(_items.GetActiveCalls >= 2);
        Assert.False(worker.ExecuteTask!.IsFaulted);
    }
}

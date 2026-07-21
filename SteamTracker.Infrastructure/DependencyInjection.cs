using Microsoft.Extensions.DependencyInjection;
using SteamTracker.Core.Abstractions;
using SteamTracker.Infrastructure.Steam;

namespace SteamTracker.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services)
    {
        services.AddHttpClient<ISteamMarketClient, SteamMarketClient>(client =>
        {
            client.BaseAddress = new Uri("https://steamcommunity.com/");
            client.DefaultRequestHeaders.UserAgent.ParseAdd(
                "SteamTrackerV2/1.0 (+https://github.com/PedroGasquez/steamchecks)");
        });

        return services;
    }
}

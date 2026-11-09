using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Polly;
using Polly.CircuitBreaker;
using Polly.Retry;
using SteamTracker.Core.Abstractions;
using SteamTracker.Infrastructure.Persistence;
using SteamTracker.Infrastructure.Persistence.Repositories;
using SteamTracker.Infrastructure.Steam;

namespace SteamTracker.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddHttpClient<ISteamMarketClient, SteamMarketClient>(client =>
            {
                client.BaseAddress = new Uri("https://steamcommunity.com/");
                client.DefaultRequestHeaders.UserAgent.ParseAdd(
                    "SteamTrackerV2/1.0 (+https://github.com/PedroGasquez/steamchecks)");
            })
            .AddResilienceHandler("steam-market", ConfigureSteamResilience);

        services.Configure<SteamMarketOptions>(configuration.GetSection("SteamMarket"));

        var connectionString = configuration.GetConnectionString("SteamTracker")
            ?? throw new InvalidOperationException(
                "Connection string 'SteamTracker' não configurada (ver ConnectionStrings em appsettings).");

        services.AddDbContext<SteamTrackerDbContext>(options => options.UseNpgsql(connectionString));
        services.AddScoped<ITrackedItemRepository, TrackedItemRepository>();
        services.AddScoped<IPriceSnapshotRepository, PriceSnapshotRepository>();

        return services;
    }

    // Extraído da lambda de AddResilienceHandler pra ser testável isoladamente
    // (SteamTracker.Tests monta esse pipeline sem precisar de HttpClient real).
    internal static void ConfigureSteamResilience(ResiliencePipelineBuilder<HttpResponseMessage> builder)
    {
        builder.AddRetry(CreateRetryOptions());
        builder.AddCircuitBreaker(CreateCircuitBreakerOptions());
        builder.AddTimeout(TimeSpan.FromSeconds(10));
    }

    // O endpoint de mercado da Steam não é oficial: sem rate limit
    // documentado, mas uso agressivo devolve 429 e pode levar a
    // shadow-ban do IP. Retry existe pra reagir a isso, não pra insistir
    // contra um bloqueio real.
    internal static RetryStrategyOptions<HttpResponseMessage> CreateRetryOptions() => new()
    {
        ShouldHandle = args => IsTransientFailureAsync(args.Outcome, args.Context.CancellationToken),
        MaxRetryAttempts = 3,
        BackoffType = DelayBackoffType.Exponential,
        UseJitter = true,
        Delay = TimeSpan.FromSeconds(2)
    };

    internal static CircuitBreakerStrategyOptions<HttpResponseMessage> CreateCircuitBreakerOptions() => new()
    {
        ShouldHandle = args => IsTransientFailureAsync(args.Outcome, args.Context.CancellationToken),
        // Volume de chamadas é baixo (tracker pessoal, não scraper em
        // massa) — limiar baixo pra o breaker disparar de verdade.
        FailureRatio = 0.5,
        MinimumThroughput = 4,
        SamplingDuration = TimeSpan.FromSeconds(30),
        BreakDuration = TimeSpan.FromSeconds(30)
    };

    // 429/5xx são transitórios, exceto quando o corpo é {"success":false}: aí a
    // Steam já deu a resposta definitiva ("item inexistente"). Sem essa exceção,
    // um nome digitado errado gastaria 4 tentativas e, pelo circuit breaker,
    // bloquearia por 30 s as consultas a todos os outros itens.
    internal static async ValueTask<bool> IsTransientFailureAsync(
        Outcome<HttpResponseMessage> outcome,
        CancellationToken cancellationToken = default)
    {
        if (outcome.Exception is not null)
            return true;

        var response = outcome.Result;
        if (response?.StatusCode is not (HttpStatusCode.TooManyRequests or >= HttpStatusCode.InternalServerError))
            return false;

        // O predicado roda antes do SteamMarketClient ler a resposta.
        // ReadAsStringAsync bufferiza o conteúdo, então o client ainda consegue
        // ler o corpo depois (não trocar por ReadAsStreamAsync, que o consumiria).
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        return !SteamPriceOverviewResponse.IsSuccessFalse(body);
    }
}

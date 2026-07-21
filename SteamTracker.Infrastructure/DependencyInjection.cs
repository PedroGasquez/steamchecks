using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Polly;
using Polly.CircuitBreaker;
using Polly.Retry;
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
            })
            .AddResilienceHandler("steam-market", builder =>
            {
                // O endpoint de mercado da Steam não é oficial: sem rate limit
                // documentado, mas uso agressivo devolve 429 e pode levar a
                // shadow-ban do IP. Retry + circuit breaker existem pra reagir
                // a isso, não pra insistir contra um bloqueio real.
                builder.AddRetry(new RetryStrategyOptions<HttpResponseMessage>
                {
                    ShouldHandle = args => ValueTask.FromResult(IsTransientFailure(args.Outcome)),
                    MaxRetryAttempts = 3,
                    BackoffType = DelayBackoffType.Exponential,
                    UseJitter = true,
                    Delay = TimeSpan.FromSeconds(2)
                });

                builder.AddCircuitBreaker(new CircuitBreakerStrategyOptions<HttpResponseMessage>
                {
                    ShouldHandle = args => ValueTask.FromResult(IsTransientFailure(args.Outcome)),
                    // Volume de chamadas é baixo (tracker pessoal, não scraper em
                    // massa) — limiar baixo pra o breaker disparar de verdade.
                    FailureRatio = 0.5,
                    MinimumThroughput = 4,
                    SamplingDuration = TimeSpan.FromSeconds(30),
                    BreakDuration = TimeSpan.FromSeconds(30)
                });

                builder.AddTimeout(TimeSpan.FromSeconds(10));
            });

        return services;
    }

    private static bool IsTransientFailure(Outcome<HttpResponseMessage> outcome)
    {
        if (outcome.Exception is not null)
            return true;

        return outcome.Result?.StatusCode is HttpStatusCode.TooManyRequests
            or >= HttpStatusCode.InternalServerError;
    }
}

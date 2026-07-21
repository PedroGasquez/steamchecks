using System.Net;
using Polly;
using Polly.CircuitBreaker;
using Polly.Retry;
using SteamTracker.Infrastructure;

namespace SteamTracker.Tests.Resilience;

public class SteamResilienceTests
{
    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests, true)]
    [InlineData(HttpStatusCode.InternalServerError, true)]
    [InlineData(HttpStatusCode.BadGateway, true)]
    [InlineData(HttpStatusCode.ServiceUnavailable, true)]
    [InlineData(HttpStatusCode.OK, false)]
    [InlineData(HttpStatusCode.NotFound, false)]
    [InlineData(HttpStatusCode.BadRequest, false)]
    public void IsTransientFailure_ClassificaStatusCodeCorretamente(HttpStatusCode statusCode, bool esperadoTransiente)
    {
        var outcome = Outcome.FromResult(new HttpResponseMessage(statusCode));

        Assert.Equal(esperadoTransiente, DependencyInjection.IsTransientFailure(outcome));
    }

    [Fact]
    public void IsTransientFailure_ComExcecao_SempreTransiente()
    {
        var outcome = Outcome.FromException<HttpResponseMessage>(new HttpRequestException("timeout"));

        Assert.True(DependencyInjection.IsTransientFailure(outcome));
    }

    [Fact]
    public void CreateRetryOptions_ReflecteDecisaoDeDesign()
    {
        // Config espinhosa: 3 tentativas com backoff exponencial + jitter,
        // pra não martelar um endpoint não-oficial. Este teste existe pra
        // pegar mudança acidental desses parâmetros.
        var options = DependencyInjection.CreateRetryOptions();

        Assert.Equal(3, options.MaxRetryAttempts);
        Assert.Equal(DelayBackoffType.Exponential, options.BackoffType);
        Assert.True(options.UseJitter);
        Assert.Equal(TimeSpan.FromSeconds(2), options.Delay);
    }

    [Fact]
    public void CreateCircuitBreakerOptions_ReflecteDecisaoDeDesign()
    {
        // Limiar baixo de propósito: tracker pessoal de baixo volume, não
        // scraper em massa (ver PROGRESS.md, Dia 5).
        var options = DependencyInjection.CreateCircuitBreakerOptions();

        Assert.Equal(0.5, options.FailureRatio);
        Assert.Equal(4, options.MinimumThroughput);
        Assert.Equal(TimeSpan.FromSeconds(30), options.SamplingDuration);
        Assert.Equal(TimeSpan.FromSeconds(30), options.BreakDuration);
    }

    [Fact]
    public async Task Retry_TentaNovamenteEmFalhaTransiente_AteEsgotarTentativas()
    {
        // Reusa o predicado real (IsTransientFailure) mas com delay mínimo,
        // só pra não esperar o backoff de produção (2s/4s/8s) no teste.
        var attempts = 0;
        var builder = new ResiliencePipelineBuilder<HttpResponseMessage>();
        builder.AddRetry(new RetryStrategyOptions<HttpResponseMessage>
        {
            ShouldHandle = args => ValueTask.FromResult(DependencyInjection.IsTransientFailure(args.Outcome)),
            MaxRetryAttempts = 3,
            Delay = TimeSpan.FromMilliseconds(1)
        });
        var pipeline = builder.Build();

        var result = await pipeline.ExecuteAsync(async _ =>
        {
            attempts++;
            await Task.Yield();
            return new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        });

        Assert.Equal(4, attempts); // 1 tentativa inicial + 3 retries
        Assert.Equal(HttpStatusCode.TooManyRequests, result.StatusCode);
    }

    [Fact]
    public async Task Retry_NaoRetentaQuandoRespostaNaoETransiente()
    {
        var attempts = 0;
        var builder = new ResiliencePipelineBuilder<HttpResponseMessage>();
        builder.AddRetry(new RetryStrategyOptions<HttpResponseMessage>
        {
            ShouldHandle = args => ValueTask.FromResult(DependencyInjection.IsTransientFailure(args.Outcome)),
            MaxRetryAttempts = 3,
            Delay = TimeSpan.FromMilliseconds(1)
        });
        var pipeline = builder.Build();

        var result = await pipeline.ExecuteAsync(async _ =>
        {
            attempts++;
            await Task.Yield();
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        Assert.Equal(1, attempts);
        Assert.Equal(HttpStatusCode.NotFound, result.StatusCode);
    }

    [Fact]
    public async Task CircuitBreaker_AbreAposUltrapassarLimiarDeFalhas_ERejeitaChamadasSeguintes()
    {
        var builder = new ResiliencePipelineBuilder<HttpResponseMessage>();
        builder.AddCircuitBreaker(new CircuitBreakerStrategyOptions<HttpResponseMessage>
        {
            ShouldHandle = args => ValueTask.FromResult(DependencyInjection.IsTransientFailure(args.Outcome)),
            FailureRatio = 0.5,
            MinimumThroughput = 4,
            SamplingDuration = TimeSpan.FromSeconds(30),
            BreakDuration = TimeSpan.FromSeconds(30)
        });
        var pipeline = builder.Build();

        // 4 chamadas falhas seguidas ultrapassam o throughput mínimo e o
        // failure ratio de 50%, abrindo o circuito.
        for (var i = 0; i < 4; i++)
        {
            await pipeline.ExecuteAsync(async _ =>
            {
                await Task.Yield();
                return new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            });
        }

        await Assert.ThrowsAsync<BrokenCircuitException>(async () =>
            await pipeline.ExecuteAsync(async _ =>
            {
                await Task.Yield();
                return new HttpResponseMessage(HttpStatusCode.OK);
            }));
    }
}

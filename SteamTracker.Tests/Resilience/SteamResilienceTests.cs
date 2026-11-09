using System.Net;
using System.Text;
using Polly;
using Polly.CircuitBreaker;
using Polly.Retry;
using SteamTracker.Infrastructure;

namespace SteamTracker.Tests.Resilience;

public class SteamResilienceTests
{
    private const string CorpoSuccessFalse = """{"success":false}""";

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests, true)]
    [InlineData(HttpStatusCode.InternalServerError, true)]
    [InlineData(HttpStatusCode.BadGateway, true)]
    [InlineData(HttpStatusCode.ServiceUnavailable, true)]
    [InlineData(HttpStatusCode.OK, false)]
    [InlineData(HttpStatusCode.NotFound, false)]
    [InlineData(HttpStatusCode.BadRequest, false)]
    public async Task IsTransientFailureAsync_ClassificaStatusCodeCorretamente(HttpStatusCode statusCode, bool esperadoTransiente)
    {
        var outcome = Outcome.FromResult(new HttpResponseMessage(statusCode));

        Assert.Equal(esperadoTransiente, await DependencyInjection.IsTransientFailureAsync(outcome));
    }

    [Fact]
    public async Task IsTransientFailureAsync_ComExcecao_SempreTransiente()
    {
        var outcome = Outcome.FromException<HttpResponseMessage>(new HttpRequestException("timeout"));

        Assert.True(await DependencyInjection.IsTransientFailureAsync(outcome));
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    public async Task IsTransientFailureAsync_CorpoSuccessFalse_NaoETransiente(HttpStatusCode statusCode)
    {
        // {"success":false} é a resposta definitiva "item inexistente": insistir
        // não muda o resultado e só consome a cota baixa da Steam.
        var outcome = Outcome.FromResult(Resposta(statusCode, CorpoSuccessFalse));

        Assert.False(await DependencyInjection.IsTransientFailureAsync(outcome));
    }

    [Theory]
    [InlineData("<html>erro</html>")]
    [InlineData("""{"error":"x"}""")]
    [InlineData("")]
    public async Task IsTransientFailureAsync_500SemSuccessFalse_ETransiente(string corpo)
    {
        var outcome = Outcome.FromResult(Resposta(HttpStatusCode.InternalServerError, corpo));

        Assert.True(await DependencyInjection.IsTransientFailureAsync(outcome));
    }

    [Fact]
    public async Task IsTransientFailureAsync_DepoisDeLerCorpo_ConteudoContinuaLegivel()
    {
        // O predicado roda dentro do handler de resiliência, antes do
        // SteamMarketClient ler a mesma resposta. Com um stream que não volta
        // (como o da rede), ler sem bufferizar consumiria o corpo.
        var resposta = new HttpResponseMessage(HttpStatusCode.InternalServerError)
        {
            Content = new StreamContent(new StreamSemSeek(Encoding.UTF8.GetBytes(CorpoSuccessFalse)))
        };

        await DependencyInjection.IsTransientFailureAsync(Outcome.FromResult(resposta));

        Assert.Equal(CorpoSuccessFalse, await resposta.Content.ReadAsStringAsync());
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
        var attempts = 0;
        var pipeline = PipelineDeRetry();

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
        var pipeline = PipelineDeRetry();

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
    public async Task Retry_500ComSuccessFalse_NaoRetenta()
    {
        var attempts = 0;
        var pipeline = PipelineDeRetry();

        var result = await pipeline.ExecuteAsync(async _ =>
        {
            attempts++;
            await Task.Yield();
            return Resposta(HttpStatusCode.InternalServerError, CorpoSuccessFalse);
        });

        Assert.Equal(1, attempts);
        Assert.Equal(HttpStatusCode.InternalServerError, result.StatusCode);
    }

    [Fact]
    public async Task CircuitBreaker_AbreAposUltrapassarLimiarDeFalhas_ERejeitaChamadasSeguintes()
    {
        var pipeline = PipelineDeCircuitBreaker();

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

    [Fact]
    public async Task CircuitBreaker_RespostasSuccessFalse_NaoAbremCircuito()
    {
        // Um nome digitado errado (ou um item que saiu do mercado) não pode
        // derrubar as consultas aos outros itens.
        var pipeline = PipelineDeCircuitBreaker();

        for (var i = 0; i < 4; i++)
        {
            await pipeline.ExecuteAsync(async _ =>
            {
                await Task.Yield();
                return Resposta(HttpStatusCode.InternalServerError, CorpoSuccessFalse);
            });
        }

        var result = await pipeline.ExecuteAsync(async _ =>
        {
            await Task.Yield();
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        Assert.Equal(HttpStatusCode.OK, result.StatusCode);
    }

    // Opções de produção (predicado real), só com o delay reduzido pra não
    // esperar o backoff de 2s/4s/8s no teste.
    private static ResiliencePipeline<HttpResponseMessage> PipelineDeRetry()
    {
        var options = DependencyInjection.CreateRetryOptions();
        options.Delay = TimeSpan.FromMilliseconds(1);
        options.UseJitter = false;
        return new ResiliencePipelineBuilder<HttpResponseMessage>().AddRetry(options).Build();
    }

    private static ResiliencePipeline<HttpResponseMessage> PipelineDeCircuitBreaker() =>
        new ResiliencePipelineBuilder<HttpResponseMessage>()
            .AddCircuitBreaker(DependencyInjection.CreateCircuitBreakerOptions())
            .Build();

    private static HttpResponseMessage Resposta(HttpStatusCode status, string corpo) =>
        new(status) { Content = new StringContent(corpo, Encoding.UTF8, "application/json") };

    /// <summary>Stream só de leitura e sem seek, como o corpo vindo da rede.</summary>
    private sealed class StreamSemSeek(byte[] dados) : MemoryStream(dados)
    {
        public override bool CanSeek => false;
        public override long Position
        {
            get => base.Position;
            set => throw new NotSupportedException();
        }
        public override long Seek(long offset, SeekOrigin loc) => throw new NotSupportedException();
    }
}

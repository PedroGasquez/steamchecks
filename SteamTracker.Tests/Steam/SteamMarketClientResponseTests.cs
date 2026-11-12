using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using SteamTracker.Core.Abstractions;
using SteamTracker.Core.ValueObjects;
using SteamTracker.Infrastructure.Steam;
using SteamTracker.Tests.Fakes;

namespace SteamTracker.Tests.Steam;

public class SteamMarketClientResponseTests
{
    private static SteamMarketClient CriarClient(StubHttpMessageHandler handler) =>
        new(new HttpClient(handler) { BaseAddress = new Uri("https://steamcommunity.com/") },
            NullLogger<SteamMarketClient>.Instance);

    [Fact]
    public async Task GetPriceAsync_200ComSucesso_RetornaPreco()
    {
        var handler = new StubHttpMessageHandler(HttpStatusCode.OK,
            """{"success":true,"lowest_price":"R$ 11,42","median_price":"R$ 12,00","volume":"1.234"}""");

        var result = await CriarClient(handler).GetPriceAsync(730, "AK-47 | Redline (Field-Tested)", 7);

        Assert.NotNull(result);
        Assert.Equal(new Money(11.42m, "BRL"), result!.LowestPrice);
        Assert.Equal(1234, result.Volume);
    }

    [Theory]
    [InlineData(HttpStatusCode.OK)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task GetPriceAsync_CorpoSuccessFalse_RetornaNull(HttpStatusCode status)
    {
        var handler = new StubHttpMessageHandler(status, """{"success":false}""");

        var result = await CriarClient(handler).GetPriceAsync(730, "Item Inexistente", 7);

        Assert.Null(result);
    }

    // Resposta real da Steam (2026-11-12) para item inexistente: 200 com
    // success:true e nenhum campo de mercado — não {"success":false}.
    [Theory]
    [InlineData("""{"success":true}""")]
    [InlineData("""{"success":true,"lowest_price":"","median_price":"","volume":""}""")]
    public async Task GetPriceAsync_SuccessTrueSemDadosDeMercado_RetornaNull(string corpo)
    {
        var handler = new StubHttpMessageHandler(HttpStatusCode.OK, corpo);

        var result = await CriarClient(handler).GetPriceAsync(730, "Item Que Nao Existe XYZ", 7);

        Assert.Null(result);
    }

    [Fact]
    public async Task GetPriceAsync_SuccessTrueComPrecoNaoParseavel_RetornaResultadoParcial()
    {
        var handler = new StubHttpMessageHandler(HttpStatusCode.OK,
            """{"success":true,"lowest_price":"abc"}""");

        var result = await CriarClient(handler).GetPriceAsync(730, "Item Qualquer", 7);

        Assert.NotNull(result);
        Assert.Null(result!.LowestPrice);
    }

    [Fact]
    public async Task GetPriceAsync_SuccessTrueSoComMedianaEVolume_RetornaResultadoParcial()
    {
        var handler = new StubHttpMessageHandler(HttpStatusCode.OK,
            """{"success":true,"median_price":"R$ 12,00","volume":"3"}""");

        var result = await CriarClient(handler).GetPriceAsync(730, "Item Sem Ofertas Agora", 7);

        Assert.NotNull(result);
        Assert.Null(result!.LowestPrice);
        Assert.Equal(new Money(12.00m, "BRL"), result.MedianPrice);
        Assert.Equal(3, result.Volume);
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task GetPriceAsync_429ou5xxSemCorpo_LancaSteamMarketUnavailable(HttpStatusCode status)
    {
        var handler = new StubHttpMessageHandler(status, null);

        var ex = await Assert.ThrowsAsync<SteamMarketUnavailableException>(
            () => CriarClient(handler).GetPriceAsync(730, "Qualquer Item", 7));

        Assert.Equal(status, ex.StatusCode);
    }

    [Fact]
    public async Task GetPriceAsync_500ComHtml_LancaSteamMarketUnavailable()
    {
        var handler = new StubHttpMessageHandler(HttpStatusCode.InternalServerError, "<html>erro</html>");

        await Assert.ThrowsAsync<SteamMarketUnavailableException>(
            () => CriarClient(handler).GetPriceAsync(730, "Qualquer Item", 7));
    }

    [Theory]
    [InlineData("")]
    [InlineData("<html/>")]
    public async Task GetPriceAsync_200ComCorpoVazioOuNaoJson_LancaSteamMarketUnavailable(string corpo)
    {
        var handler = new StubHttpMessageHandler(HttpStatusCode.OK, corpo);

        await Assert.ThrowsAsync<SteamMarketUnavailableException>(
            () => CriarClient(handler).GetPriceAsync(730, "Qualquer Item", 7));
    }

    [Fact]
    public async Task GetPriceAsync_503ComJsonSemSuccess_LancaSteamMarketUnavailable()
    {
        var handler = new StubHttpMessageHandler(HttpStatusCode.ServiceUnavailable, """{"error":"x"}""");

        await Assert.ThrowsAsync<SteamMarketUnavailableException>(
            () => CriarClient(handler).GetPriceAsync(730, "Qualquer Item", 7));
    }

    [Fact]
    public async Task GetPriceAsync_404_RetornaNull()
    {
        var handler = new StubHttpMessageHandler(HttpStatusCode.NotFound, null);

        var result = await CriarClient(handler).GetPriceAsync(730, "Qualquer Item", 7);

        Assert.Null(result);
    }

    [Fact]
    public async Task GetPriceAsync_NomeComCaracteresEspeciais_EnviaUrlEncoded()
    {
        var nome = "StatTrak™ AK-47 | Redline (Field-Tested)";
        var handler = new StubHttpMessageHandler(HttpStatusCode.OK, """{"success":false}""");

        await CriarClient(handler).GetPriceAsync(730, nome, 7);

        Assert.Contains(Uri.EscapeDataString(nome), handler.LastRequestUri!.Query);
    }
}

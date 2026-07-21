using SteamTracker.Infrastructure.Steam;

namespace SteamTracker.Tests.Steam;

public class SteamMarketClientParsingTests
{
    [Theory]
    [InlineData("R$ 4,50", 4.50)]
    [InlineData("R$ 0,50", 0.50)]
    [InlineData("R$ 1.234,56", 1234.56)]
    [InlineData("R$ 12.345,00", 12345.00)]
    public void ParsePrice_FormatoBrasileiro_RetornaValorCorreto(string raw, decimal esperado)
    {
        var money = SteamMarketClient.ParsePrice(raw, "BRL");

        Assert.NotNull(money);
        Assert.Equal(esperado, money!.Value.Amount);
        Assert.Equal("BRL", money.Value.Currency);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ParsePrice_EntradaVaziaOuNula_RetornaNull(string? raw)
    {
        Assert.Null(SteamMarketClient.ParsePrice(raw, "BRL"));
    }

    [Fact]
    public void ParsePrice_SemDigitos_RetornaNull()
    {
        Assert.Null(SteamMarketClient.ParsePrice("indisponível", "BRL"));
    }

    [Fact]
    public void ParsePrice_FormatoAmericano_LimitacaoConhecida()
    {
        // Limitação documentada no PROGRESS.md: o parser assume separador de
        // milhar com ponto e decimal com vírgula (padrão BR). Em moedas como
        // USD, que usam ponto como decimal, o resultado sai errado — este
        // teste caracteriza o comportamento atual (buggy) pra não regredir
        // silenciosamente até o parser suportar múltiplas moedas.
        var money = SteamMarketClient.ParsePrice("$4.50", "USD");

        Assert.NotNull(money);
        Assert.Equal(450m, money!.Value.Amount);
    }

    [Theory]
    [InlineData("1,234", 1234)]
    [InlineData("1.234", 1234)]
    [InlineData("42", 42)]
    public void ParseVolume_FormatoValido_RetornaInteiro(string raw, int esperado)
    {
        Assert.Equal(esperado, SteamMarketClient.ParseVolume(raw));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("abc")]
    public void ParseVolume_EntradaInvalida_RetornaNull(string? raw)
    {
        Assert.Null(SteamMarketClient.ParseVolume(raw));
    }
}

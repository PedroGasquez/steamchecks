using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Polly.CircuitBreaker;
using Polly.Timeout;
using SteamTracker.Core.Abstractions;
using SteamTracker.Core.Entities;
using SteamTracker.Core.Enums;
using SteamTracker.Core.ValueObjects;
using SteamTracker.Tests.Support;

namespace SteamTracker.Tests.Api;

public sealed class ItemEndpointsTests : IDisposable
{
    private readonly ApiFactory _factory = new();
    private readonly HttpClient _client;

    public ItemEndpointsTests() => _client = _factory.CreateClient();

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    private static MarketPriceResult PrecoValido()
        => new(new Money(11.42m, "BRL"), new Money(12m, "BRL"), 100);

    private Task<HttpResponseMessage> Postar(int appId, string? nome)
        => _client.PostAsJsonAsync("/api/items", new { appId, marketHashName = nome });

    private static async Task<JsonElement> Json(HttpResponseMessage resposta)
    {
        using var doc = JsonDocument.Parse(await resposta.Content.ReadAsStringAsync());
        return doc.RootElement.Clone();
    }

    private async Task<Guid> CriarItemComSnapshots(params DateTime[] datas)
    {
        var item = new TrackedItem(730, "Prisma Case", "Prisma Case", ItemType.Unknown);
        using var ctx = _factory.Database.CreateContext();
        ctx.TrackedItems.Add(item);
        foreach (var data in datas)
        {
            var s = new PriceSnapshot(item.Id, new Money(10m, "BRL"), null, null);
            ctx.PriceSnapshots.Add(s);
            ctx.Entry(s).Property(x => x.CapturedAt).CurrentValue = data;
        }
        await ctx.SaveChangesAsync();
        return item.Id;
    }

    private static DateTime[] TresDatas()
    {
        var baseDate = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        return [baseDate, baseDate.AddHours(1), baseDate.AddHours(2)];
    }

    private int ContarItens()
    {
        using var ctx = _factory.Database.CreateContext();
        return ctx.TrackedItems.Count();
    }

    [Theory]
    [InlineData(0, "Prisma Case")]
    [InlineData(730, "")]
    [InlineData(730, "   ")]
    [InlineData(730, null)]
    public async Task Post_DadosInvalidos_Retorna400(int appId, string? nome)
    {
        var resposta = await Postar(appId, nome);

        Assert.Equal(HttpStatusCode.BadRequest, resposta.StatusCode);
        var erros = (await Json(resposta)).GetProperty("errors");
        Assert.True(erros.TryGetProperty(appId <= 0 ? "appId" : "marketHashName", out _));
        Assert.Empty(_factory.Steam.Calls);
    }

    [Fact]
    public async Task Post_ItemJaRastreado_Retorna409()
    {
        _factory.Steam.Responses["Prisma Case"] = PrecoValido;
        await Postar(730, "Prisma Case");

        var resposta = await Postar(730, "Prisma Case");

        Assert.Equal(HttpStatusCode.Conflict, resposta.StatusCode);
        Assert.Equal(1, ContarItens());
    }

    [Fact]
    public async Task Post_SteamRetornaNull_Retorna422_ENaoSalva()
    {
        var resposta = await Postar(730, "Item Que Nao Existe");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, resposta.StatusCode);
        Assert.Equal(0, ContarItens());
    }

    public static IEnumerable<object[]> ExcecoesDeIndisponibilidade()
    {
        yield return [new SteamMarketUnavailableException(HttpStatusCode.ServiceUnavailable)];
        yield return [new BrokenCircuitException()];
        yield return [new TimeoutRejectedException(TimeSpan.FromSeconds(10))];
        yield return [new HttpRequestException("falha de rede")];
    }

    [Theory]
    [MemberData(nameof(ExcecoesDeIndisponibilidade))]
    public async Task Post_SteamIndisponivel_Retorna503_ENaoSalva(Exception ex)
    {
        _factory.Steam.Responses["Prisma Case"] = () => throw ex;

        var resposta = await Postar(730, "Prisma Case");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, resposta.StatusCode);
        Assert.Equal(0, ContarItens());
    }

    [Fact]
    public async Task Post_Sucesso_Retorna201_ComLocationEPrimeiroSnapshot()
    {
        _factory.Steam.Responses["Prisma Case"] = PrecoValido;

        var resposta = await Postar(730, "Prisma Case");

        Assert.Equal(HttpStatusCode.Created, resposta.StatusCode);
        var corpo = await Json(resposta);
        var id = corpo.GetProperty("id").GetGuid();
        Assert.Equal($"/api/items/{id}/snapshots", resposta.Headers.Location!.OriginalString);
        Assert.Equal("Prisma Case", corpo.GetProperty("displayName").GetString());
        Assert.Equal(0, corpo.GetProperty("type").GetInt32());

        var snapshots = await Json(await _client.GetAsync(resposta.Headers.Location));
        Assert.Equal(1, snapshots.GetArrayLength());
        Assert.Equal(11.42m, snapshots[0].GetProperty("lowestPrice").GetProperty("amount").GetDecimal());
    }

    [Fact]
    public async Task Post_ValidacaoSemLowestPrice_Retorna201_SemSnapshot()
    {
        _factory.Steam.Responses["Prisma Case"] = () => new MarketPriceResult(null, null, null);

        var resposta = await Postar(730, "Prisma Case");

        Assert.Equal(HttpStatusCode.Created, resposta.StatusCode);
        var snapshots = await Json(await _client.GetAsync(resposta.Headers.Location));
        Assert.Equal(0, snapshots.GetArrayLength());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Post_DisplayNameVazio_UsaMarketHashName(string displayName)
    {
        _factory.Steam.Responses["Prisma Case"] = PrecoValido;

        var resposta = await _client.PostAsJsonAsync("/api/items",
            new { appId = 730, marketHashName = "Prisma Case", displayName });

        Assert.Equal(HttpStatusCode.Created, resposta.StatusCode);
        Assert.Equal("Prisma Case", (await Json(resposta)).GetProperty("displayName").GetString());
    }

    [Theory]
    [InlineData("marketHashName")]
    [InlineData("displayName")]
    public async Task Post_TextoMaiorQue255_Retorna400(string campo)
    {
        var longo = new string('a', 256);
        var corpo = new Dictionary<string, object?>
        {
            ["appId"] = 730,
            ["marketHashName"] = campo == "marketHashName" ? longo : "Prisma Case",
            ["displayName"] = campo == "displayName" ? longo : "Prisma Case"
        };

        var resposta = await _client.PostAsJsonAsync("/api/items", corpo);

        Assert.Equal(HttpStatusCode.BadRequest, resposta.StatusCode);
        Assert.True((await Json(resposta)).GetProperty("errors").TryGetProperty(campo, out _));
        Assert.Empty(_factory.Steam.Calls);
    }

    [Fact]
    public async Task Post_TypeInexistente_Retorna400()
    {
        var resposta = await _client.PostAsJsonAsync("/api/items",
            new { appId = 730, marketHashName = "Prisma Case", type = 42 });

        Assert.Equal(HttpStatusCode.BadRequest, resposta.StatusCode);
        Assert.True((await Json(resposta)).GetProperty("errors").TryGetProperty("type", out _));
        Assert.Empty(_factory.Steam.Calls);
    }

    [Fact]
    public async Task Post_NomeComCaracteresEspeciais_GravaExatamente()
    {
        const string nome = "StatTrak™ AK-47 | Redline (Field-Tested)";
        _factory.Steam.Responses[nome] = PrecoValido;

        var resposta = await Postar(730, nome);

        Assert.Equal(HttpStatusCode.Created, resposta.StatusCode);
        Assert.Equal(nome, (await Json(resposta)).GetProperty("marketHashName").GetString());
        Assert.Equal(nome, _factory.Steam.Calls[0].MarketHashName);
    }

    [Fact]
    public async Task GetSnapshots_ItemInexistente_Retorna404()
    {
        var resposta = await _client.GetAsync($"/api/items/{Guid.NewGuid()}/snapshots");

        Assert.Equal(HttpStatusCode.NotFound, resposta.StatusCode);
    }

    [Fact]
    public async Task GetSnapshots_OrdenaDoMaisRecente_ERespeitaLimit()
    {
        var datas = TresDatas();
        var id = await CriarItemComSnapshots(datas);

        var corpo = await Json(await _client.GetAsync($"/api/items/{id}/snapshots?limit=2"));

        Assert.Equal(2, corpo.GetArrayLength());
        Assert.Equal(datas[2].Ticks, corpo[0].GetProperty("capturedAt").GetDateTime().Ticks);
        Assert.Equal(datas[1].Ticks, corpo[1].GetProperty("capturedAt").GetDateTime().Ticks);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(-5, 1)]
    [InlineData(1000, 3)]
    public async Task GetSnapshots_LimitForaDoIntervalo_AjustaSemErro(int limit, int esperado)
    {
        var id = await CriarItemComSnapshots(TresDatas());

        var resposta = await _client.GetAsync($"/api/items/{id}/snapshots?limit={limit}");

        Assert.Equal(HttpStatusCode.OK, resposta.StatusCode);
        Assert.Equal(esperado, (await Json(resposta)).GetArrayLength());
    }

    [Fact]
    public async Task GetPrice_SteamIndisponivel_Retorna503()
    {
        _factory.Steam.Responses["Prisma Case"] =
            () => throw new SteamMarketUnavailableException(HttpStatusCode.TooManyRequests);

        var resposta = await _client.GetAsync("/api/items/730/Prisma Case/price?currency=7");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, resposta.StatusCode);
    }
}

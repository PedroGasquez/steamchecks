using Microsoft.Extensions.Options;
using Polly.CircuitBreaker;
using Polly.Timeout;
using SteamTracker.Core.Abstractions;
using SteamTracker.Core.Entities;
using SteamTracker.Core.Enums;
using SteamTracker.Infrastructure.Steam;

namespace SteamTracker.Api.Endpoints;

public static class ItemEndpoints
{
    public static IEndpointRouteBuilder MapItemEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/items");

        group.MapGet("/{appId:int}/{marketHashName}/price", GetPrice).WithName("GetItemPrice");
        group.MapPost("/", Create);
        group.MapGet("/{id:guid}/snapshots", GetSnapshots);

        return app;
    }

    private static async Task<IResult> GetPrice(
        int appId, string marketHashName, int currency, ISteamMarketClient client, CancellationToken ct)
    {
        try
        {
            var result = await client.GetPriceAsync(appId, marketHashName, currency, ct);
            return result is not null ? Results.Ok(result) : Results.NotFound();
        }
        catch (Exception ex) when (EhIndisponibilidade(ex))
        {
            return SteamIndisponivel();
        }
    }

    private static async Task<IResult> Create(
        CreateTrackedItemRequest request,
        ISteamMarketClient steam,
        ITrackedItemRepository items,
        IPriceSnapshotRepository snapshots,
        IOptions<SteamMarketOptions> options,
        CancellationToken ct)
    {
        // Valida antes de construir a entidade (o construtor lança em dados inválidos).
        var erros = new Dictionary<string, string[]>();
        if (request.AppId <= 0)
            erros["appId"] = ["appId deve ser maior que zero."];
        if (string.IsNullOrWhiteSpace(request.MarketHashName))
            erros["marketHashName"] = ["marketHashName é obrigatório."];
        else if (request.MarketHashName.Length > 255)
            erros["marketHashName"] = ["marketHashName deve ter no máximo 255 caracteres."];
        if (request.DisplayName is { Length: > 255 })
            erros["displayName"] = ["displayName deve ter no máximo 255 caracteres."];
        if (request.Type is { } tipo && !Enum.IsDefined(tipo))
            erros["type"] = ["type não é um valor válido de ItemType."];
        if (erros.Count > 0)
            return Results.ValidationProblem(erros);

        var marketHashName = request.MarketHashName!;

        if (await items.ExistsAsync(request.AppId, marketHashName, ct))
            return Results.Problem(statusCode: StatusCodes.Status409Conflict, title: "Item já está sendo rastreado");

        MarketPriceResult? preco;
        try
        {
            preco = await steam.GetPriceAsync(request.AppId, marketHashName, options.Value.Currency, ct);
        }
        catch (Exception ex) when (EhIndisponibilidade(ex))
        {
            return SteamIndisponivel();
        }

        if (preco is null)
            return Results.Problem(
                statusCode: StatusCodes.Status422UnprocessableEntity,
                title: "Item não encontrado ou sem dados de mercado no Steam Market");

        var item = new TrackedItem(
            request.AppId,
            marketHashName,
            string.IsNullOrWhiteSpace(request.DisplayName) ? marketHashName : request.DisplayName,
            request.Type ?? ItemType.Unknown);
        await items.AddAsync(item, ct);

        // Reaproveita a validação como primeiro snapshot, sem nova chamada à Steam.
        if (preco.LowestPrice is { } lowest)
            await snapshots.AddAsync(new PriceSnapshot(item.Id, lowest, preco.MedianPrice, preco.Volume), ct);

        var response = new TrackedItemResponse(
            item.Id, item.AppId, item.MarketHashName, item.DisplayName, item.Type, item.CreatedAt, item.IsActive);
        return Results.Created($"/api/items/{item.Id}/snapshots", response);
    }

    private static async Task<IResult> GetSnapshots(
        Guid id,
        int? limit,
        ITrackedItemRepository items,
        IPriceSnapshotRepository snapshots,
        CancellationToken ct)
    {
        if (await items.GetByIdAsync(id, ct) is null)
            return Results.NotFound();

        var lista = await snapshots.GetByItemAsync(id, Math.Clamp(limit ?? 100, 1, 500), ct);
        return Results.Ok(lista.Select(s => new PriceSnapshotResponse(s.LowestPrice, s.MedianPrice, s.Volume, s.CapturedAt)));
    }

    private static bool EhIndisponibilidade(Exception ex)
        => ex is SteamMarketUnavailableException or BrokenCircuitException or TimeoutRejectedException or HttpRequestException;

    private static IResult SteamIndisponivel()
        => Results.Problem(
            statusCode: StatusCodes.Status503ServiceUnavailable,
            title: "Steam indisponível, tente novamente mais tarde");
}

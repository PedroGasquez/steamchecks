using System.Text.Json.Serialization;

namespace SteamTracker.Infrastructure.Steam;

/// <summary>
/// Espelho cru da resposta do endpoint /market/priceoverview/.
/// Todos os preços chegam como string formatada (ex.: "R$ 4,50").
/// </summary>
internal sealed class SteamPriceOverviewResponse
{
    [JsonPropertyName("success")]
    public bool Success { get; set; }

    [JsonPropertyName("lowest_price")]
    public string? LowestPrice { get; set; }

    [JsonPropertyName("median_price")]
    public string? MedianPrice { get; set; }

    [JsonPropertyName("volume")]
    public string? Volume { get; set; }
}

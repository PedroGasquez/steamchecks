using System.Text.Json;
using System.Text.Json.Serialization;

namespace SteamTracker.Infrastructure.Steam;

/// <summary>
/// Espelho cru da resposta do endpoint /market/priceoverview/.
/// Todos os preços chegam como string formatada (ex.: "R$ 4,50").
/// </summary>
internal sealed class SteamPriceOverviewResponse
{
    [JsonPropertyName("success")]
    public bool? Success { get; set; }

    [JsonPropertyName("lowest_price")]
    public string? LowestPrice { get; set; }

    [JsonPropertyName("median_price")]
    public string? MedianPrice { get; set; }

    [JsonPropertyName("volume")]
    public string? Volume { get; set; }

    /// <summary>
    /// Corpo é exatamente o "não" definitivo da Steam ({"success":false}):
    /// item inexistente ou sem mercado. Usado pelo client (vira null) e pelo
    /// predicado de resiliência (não é falha transitória, não retenta).
    /// Corpo vazio, não-JSON ou JSON sem "success" não conta.
    /// </summary>
    internal static bool IsSuccessFalse(string body)
    {
        try
        {
            return JsonSerializer.Deserialize<SteamPriceOverviewResponse>(body) is { Success: false };
        }
        catch (JsonException)
        {
            return false;
        }
    }
}

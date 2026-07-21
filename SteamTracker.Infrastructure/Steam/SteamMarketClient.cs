using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using SteamTracker.Core.Abstractions;
using SteamTracker.Core.ValueObjects;

namespace SteamTracker.Infrastructure.Steam;

public sealed partial class SteamMarketClient : ISteamMarketClient
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<SteamMarketClient> _logger;

    public SteamMarketClient(HttpClient httpClient, ILogger<SteamMarketClient> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    public async Task<MarketPriceResult?> GetPriceAsync(
        int appId,
        string marketHashName,
        int currency,
        CancellationToken cancellationToken = default)
    {
        // O endpoint espera o nome do item url-encoded.
        var encodedName = Uri.EscapeDataString(marketHashName);
        var url = $"market/priceoverview/?appid={appId}&currency={currency}&market_hash_name={encodedName}";

        using var response = await _httpClient.GetAsync(url, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning(
                "Steam retornou {StatusCode} para {AppId}/{Item}",
                (int)response.StatusCode, appId, marketHashName);
            return null;
        }

        var json = await response.Content.ReadAsStreamAsync(cancellationToken);
        var data = await JsonSerializer.DeserializeAsync<SteamPriceOverviewResponse>(
            json, cancellationToken: cancellationToken);

        // success=false significa item inexistente ou sem dados de mercado.
        if (data is null || !data.Success)
            return null;

        var currencyCode = CurrencyCodeFromSteam(currency);

        return new MarketPriceResult(
            LowestPrice: ParsePrice(data.LowestPrice, currencyCode),
            MedianPrice: ParsePrice(data.MedianPrice, currencyCode),
            Volume: ParseVolume(data.Volume));
    }

    /// <summary>
    /// Converte "R$ 4,50" em Money. A Steam manda símbolo, milhar e
    /// decimal com vírgula — extraímos só os dígitos e o separador.
    /// </summary>
    internal static Money? ParsePrice(string? raw, string currency)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        // Remove tudo que não for dígito, vírgula ou ponto.
        var cleaned = PriceCharsRegex().Replace(raw, "");

        // Formato brasileiro: milhar com ponto, decimal com vírgula.
        // Removemos o ponto de milhar e trocamos a vírgula por ponto.
        cleaned = cleaned.Replace(".", "").Replace(",", ".");

        if (decimal.TryParse(cleaned, NumberStyles.Number,
                CultureInfo.InvariantCulture, out var amount))
        {
            return new Money(amount, currency);
        }

        return null;
    }

    /// <summary>Converte "1,234" (volume) em inteiro.</summary>
    internal static int? ParseVolume(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        var digitsOnly = raw.Replace(".", "").Replace(",", "");
        return int.TryParse(digitsOnly, out var volume) ? volume : null;
    }

    private static string CurrencyCodeFromSteam(int currency) => currency switch
    {
        1 => "USD",
        7 => "BRL",
        3 => "EUR",
        _ => "USD"
    };

    [GeneratedRegex(@"[^\d.,]")]
    private static partial Regex PriceCharsRegex();
}
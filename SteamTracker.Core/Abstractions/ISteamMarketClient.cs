using SteamTracker.Core.ValueObjects;

/// <summary>
/// Contrato para consultar preços no Steam Community Market.
/// Definido no Core; implementado na Infrastructure (inversão de dependência).
/// </summary>

public interface ISteamMarketClient
{
    /// <summary>
    /// Consulta o preço atual de um item. Retorna null quando o item
    /// não é encontrado ou não há dados de mercado disponíveis.
    /// </summary>

    Task<MarketPriceResult?> GetMarketPriceAsync(
        string appId,
        string marketHashName,
        int currency,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Resultado já traduzido para o domínio — não é o JSON cru da Steam.
    /// A conversão dos dados brutos acontece dentro da implementação.
    /// </summary>
    public record MarketPriceResult(
        Money? LowestPrice,
        Money? MedianPrice,
        int? Volume);
        
}

namespace SteamTracker.Infrastructure.Steam;

/// <summary>Configuração da seção "SteamMarket".</summary>
public sealed class SteamMarketOptions
{
    /// <summary>Código de moeda da Steam (7 = BRL).</summary>
    public int Currency { get; set; } = 7;
}

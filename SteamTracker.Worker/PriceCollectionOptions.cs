namespace SteamTracker.Worker;

/// <summary>Configuração da seção "PriceCollection".</summary>
public sealed class PriceCollectionOptions
{
    /// <summary>Intervalo entre ciclos de coleta.</summary>
    public TimeSpan Interval { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>Pausa entre itens do mesmo ciclo, para não sobrecarregar a Steam.</summary>
    public TimeSpan DelayBetweenItems { get; set; } = TimeSpan.FromSeconds(3);
}

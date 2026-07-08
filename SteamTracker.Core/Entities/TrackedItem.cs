using SteamTracker.Core.Enums;

namespace SteamTracker.Core.Entities;

public class TrackedItem
{
    public Guid Id { get; private set; } = Guid.NewGuid();

    /// <summary>ID do app na Steam (ex.: 730 = CS2).</summary>
    public int AppId { get; private set; }

    /// <summary>Nome único do item no Market (market_hash_name).</summary>
    public string MarketHashName { get; private set; } = null!;

    public string DisplayName { get; private set; } = null!;
    public ItemType Type { get; private set; }
    public DateTime CreatedAt { get; private set; } = DateTime.UtcNow;
    public bool IsActive { get; private set; } = true;

    // Navegação
    public List<PriceSnapshot> Snapshots { get; private set; } = [];
    public List<Alert> Alerts { get; private set; } = [];

    // EF Core precisa de um construtor sem parâmetros
    private TrackedItem() { }

    public TrackedItem(int appId, string marketHashName, string displayName, ItemType type)
    {
        if (appId <= 0)
            throw new ArgumentOutOfRangeException(nameof(appId));
        if (string.IsNullOrWhiteSpace(marketHashName))
            throw new ArgumentException("market_hash_name é obrigatório.", nameof(marketHashName));

        AppId = appId;
        MarketHashName = marketHashName;
        DisplayName = displayName;
        Type = type;
    }

    public void Deactivate() => IsActive = false;
    public void Activate() => IsActive = true;
}
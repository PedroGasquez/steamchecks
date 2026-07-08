using SteamTracker.Core.ValueObjects;

namespace SteamTracker.Core.Entities;

/// <summary>
/// Uma leitura de preço num instante. Append-only: nunca é atualizado,
/// só inserido. O conjunto de snapshots forma o histórico do item.
/// </summary>
public class PriceSnapshot
{
    public Guid Id { get; private set; } = Guid.NewGuid();
    public Guid TrackedItemId { get; private set; }

    public Money LowestPrice { get; private set; }
    public Money? MedianPrice { get; private set; }
    public int? Volume { get; private set; }
    public DateTime CapturedAt { get; private set; } = DateTime.UtcNow;

    public TrackedItem TrackedItem { get; private set; } = null!;

    private PriceSnapshot() { }

    public PriceSnapshot(Guid trackedItemId, Money lowestPrice, Money? medianPrice, int? volume)
    {
        TrackedItemId = trackedItemId;
        LowestPrice = lowestPrice;
        MedianPrice = medianPrice;
        Volume = volume;
    }
}
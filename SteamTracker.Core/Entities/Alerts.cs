using SteamTracker.Core.Enums;
using SteamTracker.Core.ValueObjects;

namespace SteamTracker.Core.Entities;

public class Alert
{
    public Guid Id { get; private set; } = Guid.NewGuid();
    public Guid TrackedItemId { get; private set; }

    public AlertType Type { get; private set; }
    public NotificationChannel Channel { get; private set; }

    /// <summary>Alvo em valor absoluto (para BelowPrice).</summary>
    public Money? TargetPrice { get; private set; }

    /// <summary>Percentual de queda, ex.: 10 = 10% (para PercentDrop).</summary>
    public decimal? DropPercentage { get; private set; }

    /// <summary>Destino da notificação (webhook do Discord ou email).</summary>
    public string Destination { get; private set; } = null!;

    public bool IsEnabled { get; private set; } = true;
    public DateTime? LastTriggeredAt { get; private set; }
    public DateTime CreatedAt { get; private set; } = DateTime.UtcNow;

    public TrackedItem TrackedItem { get; private set; } = null!;

    private Alert() { }

    public Alert(Guid trackedItemId, AlertType type, NotificationChannel channel, string destination)
    {
        TrackedItemId = trackedItemId;
        Type = type;
        Channel = channel;
        Destination = destination;
    }

    public void MarkTriggered() => LastTriggeredAt = DateTime.UtcNow;
    public void Disable() => IsEnabled = false;
}
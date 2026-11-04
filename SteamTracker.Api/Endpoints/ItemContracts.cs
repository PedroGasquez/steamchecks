using SteamTracker.Core.Enums;
using SteamTracker.Core.ValueObjects;

namespace SteamTracker.Api.Endpoints;

/// <summary>Corpo do POST /api/items. DisplayName e Type são opcionais.</summary>
public sealed record CreateTrackedItemRequest(int AppId, string? MarketHashName, string? DisplayName, ItemType? Type);

public sealed record TrackedItemResponse(
    Guid Id, int AppId, string MarketHashName, string DisplayName, ItemType Type, DateTime CreatedAt, bool IsActive);

public sealed record PriceSnapshotResponse(Money LowestPrice, Money? MedianPrice, int? Volume, DateTime CapturedAt);

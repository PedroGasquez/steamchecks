using System.Text.Json;
using SteamTracker.Core.ValueObjects;

namespace SteamTracker.Infrastructure.Persistence;

/// <summary>
/// Conversão de Money? para/de json. JsonSerializer.Deserialize&lt;Money&gt; devolve
/// default(Money) (Money é struct com propriedades só de leitura e o STJ usa o
/// construtor sem parâmetros), então reconstruímos via construtor público.
/// O formato gravado é o mesmo de antes: {"Amount":..,"Currency":".."}.
/// </summary>
internal static class MoneyJsonConversion
{
    private sealed record MoneyJson(decimal Amount, string Currency);

    public static string? Serialize(Money? money)
        => money is null
            ? null
            : JsonSerializer.Serialize(new MoneyJson(money.Value.Amount, money.Value.Currency));

    public static Money? Deserialize(string? json)
    {
        if (json is null)
            return null;

        var dto = JsonSerializer.Deserialize<MoneyJson>(json)
            ?? throw new JsonException("Json de Money inválido.");
        return new Money(dto.Amount, dto.Currency);
    }
}

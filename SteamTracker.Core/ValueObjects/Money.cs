namespace SteamTracker.Core.ValueObjects;

public readonly record struct Money
{
    public decimal Amount { get; }
    public string Currency { get; }

    public Money(decimal amount, string currency)
    {
        if (amount < 0)
            throw new ArgumentOutOfRangeException(nameof(amount), "Preço não pode ser negativo.");
        if (string.IsNullOrWhiteSpace(currency))
            throw new ArgumentException("Moeda é obrigatória.", nameof(currency));

        Amount = amount;
        Currency = currency.ToUpperInvariant();
    }

    public override string ToString() => $"{Amount:0.00} {Currency}";
}
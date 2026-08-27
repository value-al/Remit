namespace Remit.BuildingBlocks;

/// <summary>
/// An amount in a single currency. Minor units are not modelled yet (ADR-0001 non-goal);
/// <see cref="decimal"/> is used deliberately over <see cref="double"/>.
/// </summary>
public readonly record struct Money(decimal Amount, string Currency)
{
    public static Money Of(decimal amount, string currency)
    {
        if (string.IsNullOrWhiteSpace(currency) || currency.Length != 3)
        {
            throw new ArgumentException("Currency must be a 3-letter ISO code.", nameof(currency));
        }

        return new Money(amount, currency.ToUpperInvariant());
    }

    public bool IsPositive => Amount > 0;

    public Money Add(Money other)
    {
        EnsureSameCurrency(other);
        return this with { Amount = Amount + other.Amount };
    }

    public Money Negate() => this with { Amount = -Amount };

    private void EnsureSameCurrency(Money other)
    {
        if (!string.Equals(Currency, other.Currency, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Cannot combine {Currency} with {other.Currency}.");
        }
    }

    public override string ToString() => $"{Amount:0.00} {Currency}";
}

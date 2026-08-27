using Remit.BuildingBlocks;
using Remit.Ledger;

namespace Remit.Ledger.Tests;

public class JournalEntryTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 27, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Balanced_entry_is_accepted()
    {
        var entry = JournalEntry.Create("Deposit settled", Now,
        [
            new Posting("psp:receivable", Money.Of(100m, "EUR"), Side.Debit),
            new Posting("client:wallet:42", Money.Of(100m, "EUR"), Side.Credit),
        ]);

        Assert.Equal(2, entry.Postings.Count);
    }

    [Fact]
    public void Unbalanced_entry_is_rejected_with_the_currency_and_the_difference()
    {
        var ex = Assert.Throws<UnbalancedEntryException>(() => JournalEntry.Create("Broken", Now,
        [
            new Posting("psp:receivable", Money.Of(100m, "EUR"), Side.Debit),
            new Posting("client:wallet:42", Money.Of(90m, "EUR"), Side.Credit),
        ]));

        Assert.Contains("EUR off by 10.00", ex.Message);
    }

    [Fact]
    public void Balance_is_checked_per_currency_not_across_currencies()
    {
        Assert.Throws<UnbalancedEntryException>(() => JournalEntry.Create("Cross-currency", Now,
        [
            new Posting("a", Money.Of(100m, "EUR"), Side.Debit),
            new Posting("b", Money.Of(100m, "USD"), Side.Credit),
        ]));
    }

    [Fact]
    public void Multi_currency_entry_balances_when_each_currency_balances()
    {
        var entry = JournalEntry.Create("FX", Now,
        [
            new Posting("a", Money.Of(100m, "EUR"), Side.Debit),
            new Posting("b", Money.Of(100m, "EUR"), Side.Credit),
            new Posting("c", Money.Of(108m, "USD"), Side.Debit),
            new Posting("d", Money.Of(108m, "USD"), Side.Credit),
        ]);

        Assert.Equal(4, entry.Postings.Count);
    }

    [Fact]
    public void Negative_posting_amounts_are_rejected()
    {
        Assert.Throws<UnbalancedEntryException>(() => JournalEntry.Create("Negative", Now,
        [
            new Posting("a", Money.Of(-100m, "EUR"), Side.Credit),
            new Posting("b", Money.Of(100m, "EUR"), Side.Credit),
        ]));
    }
}

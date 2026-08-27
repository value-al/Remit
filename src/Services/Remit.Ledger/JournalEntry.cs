using Remit.BuildingBlocks;

namespace Remit.Ledger;

public enum Side
{
    Debit,
    Credit,
}

/// <summary>One line of a journal entry: an account, an amount, a side.</summary>
public sealed record Posting(string Account, Money Amount, Side Side)
{
    public Money Signed => Side == Side.Debit ? Amount : Amount.Negate();
}

/// <summary>
/// A double-entry journal entry (ADR-0004). It is immutable, and it is only constructible
/// balanced: for every currency, debits equal credits. Corrections are new entries, never edits.
/// </summary>
public sealed class JournalEntry
{
    private JournalEntry(Guid id, string description, DateTimeOffset postedAt, IReadOnlyList<Posting> postings)
    {
        Id = id;
        Description = description;
        PostedAt = postedAt;
        Postings = postings;
    }

    public Guid Id { get; }
    public string Description { get; }
    public DateTimeOffset PostedAt { get; }
    public IReadOnlyList<Posting> Postings { get; }

    public static JournalEntry Create(string description, DateTimeOffset postedAt, IReadOnlyList<Posting> postings)
    {
        if (postings.Count < 2)
        {
            throw new UnbalancedEntryException("A journal entry needs at least two postings.");
        }

        if (postings.Any(p => !p.Amount.IsPositive))
        {
            throw new UnbalancedEntryException("Posting amounts must be positive; the side carries the sign.");
        }

        var unbalanced = postings
            .GroupBy(p => p.Amount.Currency, StringComparer.Ordinal)
            .Select(g => (Currency: g.Key, Net: g.Sum(p => p.Signed.Amount)))
            .Where(x => x.Net != 0m)
            .ToList();

        if (unbalanced.Count > 0)
        {
            var detail = string.Join(", ", unbalanced.Select(x => $"{x.Currency} off by {x.Net:0.00}"));
            throw new UnbalancedEntryException($"Debits and credits do not balance: {detail}.");
        }

        return new JournalEntry(Guid.NewGuid(), description, postedAt, [.. postings]);
    }
}

public sealed class UnbalancedEntryException(string message) : InvalidOperationException(message);

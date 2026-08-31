using Remit.BuildingBlocks;

namespace Remit.Reconciliation.Matching;

public enum MovementKind
{
    Deposit,
    Withdrawal,
}

/// <summary>One line of a provider statement, already parsed. What the PSP says happened.</summary>
public sealed record StatementLine(string Reference, MovementKind Kind, Money Amount, DateTimeOffset SettledAt);

/// <summary>What Funding said would happen, as far as the reconciliation service has heard.</summary>
public sealed record ExpectedMovement(Guid Id, string Provider, string Reference, MovementKind Kind, Money Amount, string Status, DateTimeOffset LastEventAt);

public enum ExceptionKind
{
    /// <summary>The statement has a reference we never issued, or in a state we never reached.</summary>
    UnknownAtPsp,

    /// <summary>Same reference, different amount or currency.</summary>
    AmountMismatch,

    /// <summary>We recorded it as final; the provider's statement for that period does not show it.</summary>
    MissingAtPsp,

    /// <summary>Statement says it settled; our record is still in flight — a webhook we never received.</summary>
    SettledButNotFinal,
}

public sealed record MatchResult(
    IReadOnlyList<(StatementLine Line, ExpectedMovement Movement)> Matched,
    IReadOnlyList<(ExceptionKind Kind, StatementLine? Line, ExpectedMovement? Movement, string Detail)> Exceptions);

/// <summary>
/// Compares a provider's statement to what we expected from that provider (ADR-0009). Pure:
/// no I/O, no clock. The only join key is the PSP reference — amounts are evidence, never keys,
/// because two deposits of 50 EUR on one day are normal and two references are not.
/// </summary>
public static class StatementMatcher
{
    public static MatchResult Match(
        IReadOnlyCollection<StatementLine> lines,
        IReadOnlyCollection<ExpectedMovement> expected,
        DateTimeOffset periodStart,
        DateTimeOffset periodEnd)
    {
        var matched = new List<(StatementLine, ExpectedMovement)>();
        var exceptions = new List<(ExceptionKind, StatementLine?, ExpectedMovement?, string)>();

        var byReference = expected.ToDictionary(m => m.Reference, StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var line in lines)
        {
            if (!byReference.TryGetValue(line.Reference, out var movement))
            {
                exceptions.Add((ExceptionKind.UnknownAtPsp, line, null, $"Reference {line.Reference} ({line.Kind}, {line.Amount}) is on the statement but not in our records."));
                continue;
            }

            seen.Add(line.Reference);

            if (movement.Amount != line.Amount || movement.Kind != line.Kind)
            {
                exceptions.Add((ExceptionKind.AmountMismatch, line, movement, $"Reference {line.Reference}: we recorded {movement.Kind} {movement.Amount}, statement says {line.Kind} {line.Amount}."));
                continue;
            }

            if (!IsFinal(movement.Status))
            {
                exceptions.Add((ExceptionKind.SettledButNotFinal, line, movement, $"Reference {line.Reference} settled at the provider on {line.SettledAt:u} but our record is still {movement.Status}."));
                continue;
            }

            matched.Add((line, movement));
        }

        // Final on our side, inside the statement period, absent from the statement.
        foreach (var movement in expected)
        {
            if (seen.Contains(movement.Reference) || !IsFinal(movement.Status))
            {
                continue;
            }

            if (movement.LastEventAt >= periodStart && movement.LastEventAt < periodEnd)
            {
                exceptions.Add((ExceptionKind.MissingAtPsp, null, movement, $"Reference {movement.Reference} ({movement.Kind} {movement.Amount}) is {movement.Status} in our records on {movement.LastEventAt:u} but absent from the statement for {periodStart:d}–{periodEnd:d}."));
            }
        }

        return new MatchResult(matched, exceptions);
    }

    public static bool IsFinal(string status) => status is "Settled" or "Paid";
}

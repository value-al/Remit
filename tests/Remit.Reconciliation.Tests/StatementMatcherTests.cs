using Remit.BuildingBlocks;
using Remit.Reconciliation.Matching;

namespace Remit.Reconciliation.Tests;

public class StatementMatcherTests
{
    private static readonly DateTimeOffset Start = new(2026, 8, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset End = new(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Mid = new(2026, 8, 15, 12, 0, 0, TimeSpan.Zero);

    private static ExpectedMovement Expected(string reference, decimal amount, string status = "Settled", MovementKind kind = MovementKind.Deposit, DateTimeOffset? at = null) =>
        new(Guid.NewGuid(), "alpha", reference, kind, Money.Of(amount, "EUR"), status, at ?? Mid);

    private static StatementLine Line(string reference, decimal amount, MovementKind kind = MovementKind.Deposit) =>
        new(reference, kind, Money.Of(amount, "EUR"), Mid);

    [Fact]
    public void Same_reference_same_amount_final_status_matches()
    {
        var result = StatementMatcher.Match([Line("r1", 100m)], [Expected("r1", 100m)], Start, End);

        Assert.Single(result.Matched);
        Assert.Empty(result.Exceptions);
    }

    [Fact]
    public void Unknown_reference_on_the_statement_is_an_exception()
    {
        var result = StatementMatcher.Match([Line("ghost", 5m)], [], Start, End);

        var ex = Assert.Single(result.Exceptions);
        Assert.Equal(ExceptionKind.UnknownAtPsp, ex.Kind);
        Assert.Null(ex.Movement);
    }

    [Fact]
    public void Different_amount_is_a_mismatch_not_a_match()
    {
        var result = StatementMatcher.Match([Line("r1", 99.99m)], [Expected("r1", 100m)], Start, End);

        var ex = Assert.Single(result.Exceptions);
        Assert.Equal(ExceptionKind.AmountMismatch, ex.Kind);
        Assert.Empty(result.Matched);
    }

    [Fact]
    public void Statement_settled_but_our_record_still_in_flight_means_a_lost_webhook()
    {
        var result = StatementMatcher.Match([Line("r1", 100m)], [Expected("r1", 100m, status: "SubmittedToPsp")], Start, End);

        var ex = Assert.Single(result.Exceptions);
        Assert.Equal(ExceptionKind.SettledButNotFinal, ex.Kind);
    }

    [Fact]
    public void Final_on_our_side_inside_the_period_but_absent_from_the_statement_is_missing_at_psp()
    {
        var result = StatementMatcher.Match([], [Expected("r1", 100m)], Start, End);

        var ex = Assert.Single(result.Exceptions);
        Assert.Equal(ExceptionKind.MissingAtPsp, ex.Kind);
    }

    [Fact]
    public void Movements_outside_the_period_are_not_expected_on_this_statement()
    {
        var lastMonth = Start.AddDays(-3);
        var result = StatementMatcher.Match([], [Expected("old", 100m, at: lastMonth)], Start, End);

        Assert.Empty(result.Exceptions);
    }

    [Fact]
    public void Failed_movements_are_never_expected_on_a_statement()
    {
        var result = StatementMatcher.Match([], [Expected("f1", 100m, status: "Failed")], Start, End);

        Assert.Empty(result.Exceptions);
    }

    [Fact]
    public void Two_identical_amounts_are_two_movements_because_references_differ()
    {
        var result = StatementMatcher.Match(
            [Line("a", 50m), Line("b", 50m)],
            [Expected("a", 50m), Expected("b", 50m)],
            Start, End);

        Assert.Equal(2, result.Matched.Count);
        Assert.Empty(result.Exceptions);
    }

    [Fact]
    public void Withdrawals_use_paid_as_final()
    {
        var result = StatementMatcher.Match(
            [Line("w1", 30m, MovementKind.Withdrawal)],
            [Expected("w1", 30m, status: "Paid", kind: MovementKind.Withdrawal)],
            Start, End);

        Assert.Single(result.Matched);
    }

    [Fact]
    public void Csv_parses_the_documented_format_and_rejects_anything_else()
    {
        var lines = StatementCsv.Parse("reference,kind,amount,currency,settled_at\nr1,deposit,100.00,EUR,2026-08-15T12:00:00Z\nw1,withdrawal,30,eur,2026-08-16T09:30:00Z\n");

        Assert.Equal(2, lines.Count);
        Assert.Equal(Money.Of(100m, "EUR"), lines[0].Amount);
        Assert.Equal(MovementKind.Withdrawal, lines[1].Kind);
        Assert.Equal("EUR", lines[1].Amount.Currency);

        Assert.Throws<FormatException>(() => StatementCsv.Parse("ref,amount\nr1,1"));
        Assert.Throws<FormatException>(() => StatementCsv.Parse("reference,kind,amount,currency,settled_at\nr1,deposit,abc,EUR,2026-08-15T12:00:00Z"));
        Assert.Throws<FormatException>(() => StatementCsv.Parse("reference,kind,amount,currency,settled_at\nr1,refund,1,EUR,2026-08-15T12:00:00Z"));
    }
}

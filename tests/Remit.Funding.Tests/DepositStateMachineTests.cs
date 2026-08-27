using Microsoft.Extensions.Time.Testing;
using Remit.BuildingBlocks;
using Remit.Funding.Deposits;

namespace Remit.Funding.Tests;

public class DepositStateMachineTests
{
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 8, 27, 12, 0, 0, TimeSpan.Zero));

    private Deposit NewDeposit() => Deposit.Request(Guid.NewGuid(), Money.Of(50m, "EUR"), _clock);

    [Fact]
    public void Happy_path_requested_submitted_settled()
    {
        var d = NewDeposit();
        d.MarkSubmitted("psp-ref-1", _clock);
        d.MarkSettled(_clock);

        Assert.Equal(DepositStatus.Settled, d.Status);
        Assert.Equal("psp-ref-1", d.PspReference);
        Assert.True(d.IsTerminal);
        Assert.Equal(2, d.History.Count);
    }

    [Fact]
    public void A_duplicate_settlement_webhook_cannot_settle_twice()
    {
        var d = NewDeposit();
        d.MarkSubmitted("psp-ref-1", _clock);
        d.MarkSettled(_clock);

        var ex = Assert.Throws<InvalidDepositTransitionException>(() => d.MarkSettled(_clock));
        Assert.Equal(DepositStatus.Settled, ex.From);
        Assert.Equal(DepositStatus.Settled, ex.To);
    }

    [Fact]
    public void Cannot_settle_what_was_never_submitted()
    {
        var d = NewDeposit();
        Assert.Throws<InvalidDepositTransitionException>(() => d.MarkSettled(_clock));
    }

    [Fact]
    public void Failure_is_terminal()
    {
        var d = NewDeposit();
        d.MarkFailed("card declined", _clock);

        Assert.True(d.IsTerminal);
        Assert.Throws<InvalidDepositTransitionException>(() => d.MarkSubmitted("late", _clock));
    }

    [Fact]
    public void Zero_or_negative_deposits_are_rejected_at_the_door()
    {
        Assert.Throws<ArgumentException>(() => Deposit.Request(Guid.NewGuid(), Money.Of(0m, "EUR"), _clock));
        Assert.Throws<ArgumentException>(() => Deposit.Request(Guid.NewGuid(), Money.Of(-1m, "EUR"), _clock));
    }
}

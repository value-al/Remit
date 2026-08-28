using Remit.BuildingBlocks;

namespace Remit.Funding.Withdrawals;

public enum WithdrawalStatus
{
    /// <summary>Accepted from the client; balance was sufficient at the time of the check.</summary>
    Requested,

    /// <summary>Handed to a PSP for payout; awaiting its asynchronous outcome.</summary>
    SubmittedToPsp,

    /// <summary>PSP confirmed the funds left; ledger posting emitted.</summary>
    Paid,

    /// <summary>Terminal failure. No funds moved.</summary>
    Failed,
}

public sealed record WithdrawalTransition(WithdrawalStatus From, WithdrawalStatus To, DateTimeOffset At);

/// <summary>
/// Mirrors <see cref="Deposits.Deposit"/> in the other direction (ADR-0007): the same explicit
/// state machine, the same closed edges against duplicate webhooks.
/// </summary>
public sealed class Withdrawal
{
    private static readonly IReadOnlyDictionary<WithdrawalStatus, WithdrawalStatus[]> AllowedTransitions =
        new Dictionary<WithdrawalStatus, WithdrawalStatus[]>
        {
            [WithdrawalStatus.Requested] = [WithdrawalStatus.SubmittedToPsp, WithdrawalStatus.Failed],
            [WithdrawalStatus.SubmittedToPsp] = [WithdrawalStatus.Paid, WithdrawalStatus.Failed],
            [WithdrawalStatus.Paid] = [],
            [WithdrawalStatus.Failed] = [],
        };

    private readonly List<WithdrawalTransition> _history = [];

    private Withdrawal(Guid id, Guid accountId, Money amount, DateTimeOffset requestedAt)
    {
        Id = id;
        AccountId = accountId;
        Amount = amount;
        Status = WithdrawalStatus.Requested;
        RequestedAt = requestedAt;
    }

    private Withdrawal()
    {
    }

    public Guid Id { get; private set; }
    public Guid AccountId { get; private set; }
    public Money Amount { get; private set; }
    public WithdrawalStatus Status { get; private set; }
    public DateTimeOffset RequestedAt { get; private set; }
    public string? Provider { get; private set; }
    public string? PspReference { get; private set; }
    public string? FailureReason { get; private set; }
    public IReadOnlyList<WithdrawalTransition> History => _history;

    public static Withdrawal Request(Guid accountId, Money amount, TimeProvider clock)
    {
        if (!amount.IsPositive)
        {
            throw new ArgumentException("Withdrawal amount must be positive.", nameof(amount));
        }

        return new Withdrawal(Guid.NewGuid(), accountId, amount, clock.GetUtcNow());
    }

    public void MarkSubmitted(string provider, string pspReference, TimeProvider clock)
    {
        Transition(WithdrawalStatus.SubmittedToPsp, clock);
        Provider = provider;
        PspReference = pspReference;
    }

    public void MarkPaid(TimeProvider clock) => Transition(WithdrawalStatus.Paid, clock);

    public void MarkFailed(string reason, TimeProvider clock)
    {
        Transition(WithdrawalStatus.Failed, clock);
        FailureReason = reason;
    }

    public bool IsTerminal => AllowedTransitions[Status].Length == 0;

    private void Transition(WithdrawalStatus to, TimeProvider clock)
    {
        if (!AllowedTransitions[Status].Contains(to))
        {
            throw new InvalidWithdrawalTransitionException(Id, Status, to);
        }

        _history.Add(new WithdrawalTransition(Status, to, clock.GetUtcNow()));
        Status = to;
    }
}

public sealed class InvalidWithdrawalTransitionException(Guid withdrawalId, WithdrawalStatus from, WithdrawalStatus to)
    : InvalidOperationException($"Withdrawal {withdrawalId}: transition {from} -> {to} is not allowed.")
{
    public Guid WithdrawalId { get; } = withdrawalId;
    public WithdrawalStatus From { get; } = from;
    public WithdrawalStatus To { get; } = to;
}

using Remit.BuildingBlocks;

namespace Remit.Funding.Deposits;

public enum DepositStatus
{
    /// <summary>Accepted from the client; nothing sent to a PSP yet.</summary>
    Requested,

    /// <summary>Handed to a PSP; awaiting its asynchronous outcome.</summary>
    SubmittedToPsp,

    /// <summary>PSP confirmed the funds; ledger posting emitted.</summary>
    Settled,

    /// <summary>Terminal failure. No funds moved, or a reversal has been posted.</summary>
    Failed,
}

/// <summary>
/// A deposit is an explicit state machine (ADR-0002). Every transition is named and the
/// allowed edges are the whole list below; anything else throws. PSP retries and duplicate
/// webhooks therefore cannot move a deposit twice — the second attempt hits a closed edge.
/// </summary>
public sealed class Deposit
{
    private static readonly IReadOnlyDictionary<DepositStatus, DepositStatus[]> AllowedTransitions =
        new Dictionary<DepositStatus, DepositStatus[]>
        {
            [DepositStatus.Requested] = [DepositStatus.SubmittedToPsp, DepositStatus.Failed],
            [DepositStatus.SubmittedToPsp] = [DepositStatus.Settled, DepositStatus.Failed],
            [DepositStatus.Settled] = [],
            [DepositStatus.Failed] = [],
        };

    private readonly List<(DepositStatus From, DepositStatus To, DateTimeOffset At)> _history = [];

    private Deposit(Guid id, Guid accountId, Money amount, DateTimeOffset requestedAt)
    {
        Id = id;
        AccountId = accountId;
        Amount = amount;
        Status = DepositStatus.Requested;
        RequestedAt = requestedAt;
    }

    public Guid Id { get; }
    public Guid AccountId { get; }
    public Money Amount { get; }
    public DepositStatus Status { get; private set; }
    public DateTimeOffset RequestedAt { get; }
    public string? PspReference { get; private set; }
    public string? FailureReason { get; private set; }
    public IReadOnlyList<(DepositStatus From, DepositStatus To, DateTimeOffset At)> History => _history;

    public static Deposit Request(Guid accountId, Money amount, TimeProvider clock)
    {
        if (!amount.IsPositive)
        {
            throw new ArgumentException("Deposit amount must be positive.", nameof(amount));
        }

        return new Deposit(Guid.NewGuid(), accountId, amount, clock.GetUtcNow());
    }

    public void MarkSubmitted(string pspReference, TimeProvider clock)
    {
        Transition(DepositStatus.SubmittedToPsp, clock);
        PspReference = pspReference;
    }

    public void MarkSettled(TimeProvider clock) => Transition(DepositStatus.Settled, clock);

    public void MarkFailed(string reason, TimeProvider clock)
    {
        Transition(DepositStatus.Failed, clock);
        FailureReason = reason;
    }

    public bool IsTerminal => AllowedTransitions[Status].Length == 0;

    private void Transition(DepositStatus to, TimeProvider clock)
    {
        if (!AllowedTransitions[Status].Contains(to))
        {
            throw new InvalidDepositTransitionException(Id, Status, to);
        }

        _history.Add((Status, to, clock.GetUtcNow()));
        Status = to;
    }
}

public sealed class InvalidDepositTransitionException(Guid depositId, DepositStatus from, DepositStatus to)
    : InvalidOperationException($"Deposit {depositId}: transition {from} -> {to} is not allowed.")
{
    public Guid DepositId { get; } = depositId;
    public DepositStatus From { get; } = from;
    public DepositStatus To { get; } = to;
}

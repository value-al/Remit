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

/// <summary>One recorded edge of the state machine, kept as an audit trail.</summary>
public sealed record DepositTransition(DepositStatus From, DepositStatus To, DateTimeOffset At);

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

    private readonly List<DepositTransition> _history = [];

    private Deposit(Guid id, Guid accountId, Money amount, DateTimeOffset requestedAt)
    {
        Id = id;
        AccountId = accountId;
        Amount = amount;
        Status = DepositStatus.Requested;
        RequestedAt = requestedAt;
    }

    // Materialisation constructor for the persistence layer; never used by domain code.
    private Deposit()
    {
    }

    public Guid Id { get; private set; }
    public Guid AccountId { get; private set; }
    public Money Amount { get; private set; }
    public DepositStatus Status { get; private set; }
    public DateTimeOffset RequestedAt { get; private set; }
    public string? Provider { get; private set; }
    public string? PspReference { get; private set; }
    public string? FailureReason { get; private set; }
    public IReadOnlyList<DepositTransition> History => _history;

    public static Deposit Request(Guid accountId, Money amount, TimeProvider clock)
    {
        if (!amount.IsPositive)
        {
            throw new ArgumentException("Deposit amount must be positive.", nameof(amount));
        }

        return new Deposit(Guid.NewGuid(), accountId, amount, clock.GetUtcNow());
    }

    public void MarkSubmitted(string provider, string pspReference, TimeProvider clock)
    {
        Transition(DepositStatus.SubmittedToPsp, clock);
        Provider = provider;
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

        _history.Add(new DepositTransition(Status, to, clock.GetUtcNow()));
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

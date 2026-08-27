using System.Collections.Concurrent;

namespace Remit.Funding.Deposits;

public interface IDepositRepository
{
    Task<Deposit?> FindAsync(Guid id, CancellationToken cancellationToken);
    Task SaveAsync(Deposit deposit, CancellationToken cancellationToken);
}

/// <summary>Replaced by a PostgreSQL repository in week 4; the API surface stays.</summary>
public sealed class InMemoryDepositRepository : IDepositRepository
{
    private readonly ConcurrentDictionary<Guid, Deposit> _deposits = new();

    public Task<Deposit?> FindAsync(Guid id, CancellationToken cancellationToken)
    {
        _deposits.TryGetValue(id, out var deposit);
        return Task.FromResult(deposit);
    }

    public Task SaveAsync(Deposit deposit, CancellationToken cancellationToken)
    {
        _deposits[deposit.Id] = deposit;
        return Task.CompletedTask;
    }
}

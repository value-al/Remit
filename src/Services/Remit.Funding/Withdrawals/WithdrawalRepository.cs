using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Remit.Funding.Persistence;

namespace Remit.Funding.Withdrawals;

public interface IWithdrawalRepository
{
    Task<Withdrawal?> FindAsync(Guid id, CancellationToken cancellationToken);
    Task SaveAsync(Withdrawal withdrawal, CancellationToken cancellationToken);
}

public sealed class InMemoryWithdrawalRepository : IWithdrawalRepository
{
    private readonly ConcurrentDictionary<Guid, Withdrawal> _withdrawals = new();

    public Task<Withdrawal?> FindAsync(Guid id, CancellationToken cancellationToken)
    {
        _withdrawals.TryGetValue(id, out var withdrawal);
        return Task.FromResult(withdrawal);
    }

    public Task SaveAsync(Withdrawal withdrawal, CancellationToken cancellationToken)
    {
        _withdrawals[withdrawal.Id] = withdrawal;
        return Task.CompletedTask;
    }
}

public sealed class EfWithdrawalRepository(FundingDbContext db) : IWithdrawalRepository
{
    public Task<Withdrawal?> FindAsync(Guid id, CancellationToken cancellationToken) =>
        db.Withdrawals.FirstOrDefaultAsync(w => w.Id == id, cancellationToken);

    public Task SaveAsync(Withdrawal withdrawal, CancellationToken cancellationToken)
    {
        if (db.Entry(withdrawal).State == EntityState.Detached)
        {
            db.Withdrawals.Add(withdrawal);
        }

        return Task.CompletedTask;
    }
}

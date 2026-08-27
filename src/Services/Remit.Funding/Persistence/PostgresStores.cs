using Microsoft.EntityFrameworkCore;
using Npgsql;
using Remit.BuildingBlocks;
using Remit.BuildingBlocks.Idempotency;
using Remit.BuildingBlocks.Outbox;
using Remit.Funding.Deposits;

namespace Remit.Funding.Persistence;

/// <summary>Stages the aggregate; the unit of work commits it.</summary>
public sealed class EfDepositRepository(FundingDbContext db) : IDepositRepository
{
    public Task<Deposit?> FindAsync(Guid id, CancellationToken cancellationToken) =>
        db.Deposits.FirstOrDefaultAsync(d => d.Id == id, cancellationToken);

    public Task SaveAsync(Deposit deposit, CancellationToken cancellationToken)
    {
        if (db.Entry(deposit).State == EntityState.Detached)
        {
            db.Deposits.Add(deposit);
        }

        return Task.CompletedTask;
    }
}

/// <summary>Stages the message in the same DbContext as the aggregate — one commit, one transaction.</summary>
public sealed class EfOutbox(FundingDbContext db) : IOutbox
{
    public Task EnqueueAsync(OutboxMessage message, CancellationToken cancellationToken)
    {
        db.Outbox.Add(OutboxRecord.From(message));
        return Task.CompletedTask;
    }
}

public sealed class EfUnitOfWork(FundingDbContext db) : IUnitOfWork
{
    public Task CommitAsync(CancellationToken cancellationToken) => db.SaveChangesAsync(cancellationToken);
}

/// <summary>
/// Keys live in <c>funding.idempotency_keys</c>. The primary key is the claim: two concurrent
/// requests with the same key race on the insert and exactly one wins (23505 for the other).
/// A claim that never completed — the process died mid-request — becomes takeable after
/// <see cref="ClaimGrace"/>, so a crash cannot pin a key forever.
/// </summary>
public sealed class PostgresIdempotencyStore(FundingDbContext db, TimeProvider clock) : IIdempotencyStore
{
    public static readonly TimeSpan ClaimGrace = TimeSpan.FromSeconds(60);

    public async Task<StoredResponse?> GetAsync(string key, CancellationToken cancellationToken)
    {
        var record = await db.IdempotencyKeys.AsNoTracking().FirstOrDefaultAsync(i => i.Key == key, cancellationToken);
        return record is { IsCompleted: true }
            ? new StoredResponse(record.RequestHash, record.StatusCode!.Value, record.ContentType!, record.Body!)
            : null;
    }

    public async Task<bool> TryClaimAsync(string key, string requestHash, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var existing = await db.IdempotencyKeys.FirstOrDefaultAsync(i => i.Key == key, cancellationToken);

        if (existing is not null)
        {
            if (!existing.IsStaleClaim(now, ClaimGrace))
            {
                return false;
            }

            existing.Reclaim(requestHash, now);
            await db.SaveChangesAsync(cancellationToken);
            return true;
        }

        var record = IdempotencyRecord.Claim(key, requestHash, now);
        db.IdempotencyKeys.Add(record);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateException e) when (e.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // Lost the race. Detach so the handler's later commit is not poisoned by this entry.
            db.Entry(record).State = EntityState.Detached;
            return false;
        }
    }

    public async Task CompleteAsync(string key, StoredResponse response, CancellationToken cancellationToken)
    {
        var record = await db.IdempotencyKeys.FirstAsync(i => i.Key == key, cancellationToken);
        record.Complete(response.StatusCode, response.ContentType, response.Body, clock.GetUtcNow());
        await db.SaveChangesAsync(cancellationToken);
    }

    public Task ReleaseAsync(string key, CancellationToken cancellationToken) =>
        db.IdempotencyKeys.Where(i => i.Key == key).ExecuteDeleteAsync(cancellationToken);
}

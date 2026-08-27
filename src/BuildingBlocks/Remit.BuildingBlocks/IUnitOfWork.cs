namespace Remit.BuildingBlocks;

/// <summary>
/// The transaction boundary of a request. Repositories and the outbox stage changes;
/// <see cref="CommitAsync"/> writes them atomically (ADR-0003). Handlers call it exactly once.
/// </summary>
public interface IUnitOfWork
{
    Task CommitAsync(CancellationToken cancellationToken);
}

/// <summary>For the in-memory configuration, where every write is already "committed".</summary>
public sealed class NoOpUnitOfWork : IUnitOfWork
{
    public Task CommitAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

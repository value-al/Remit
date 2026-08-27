using System.Collections.Concurrent;

namespace Remit.BuildingBlocks.Idempotency;

/// <summary>
/// Process-local store. Correct for a single instance and for tests; the PostgreSQL
/// implementation (week 4) keys on (tenant, key) with a unique index and an expiry.
/// </summary>
public sealed class InMemoryIdempotencyStore : IIdempotencyStore
{
    private sealed record Entry(string RequestHash, StoredResponse? Response);

    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    public Task<StoredResponse?> GetAsync(string key, CancellationToken cancellationToken)
    {
        _entries.TryGetValue(key, out var entry);
        return Task.FromResult(entry?.Response);
    }

    public Task<bool> TryClaimAsync(string key, string requestHash, CancellationToken cancellationToken)
    {
        var claimed = _entries.TryAdd(key, new Entry(requestHash, null));
        return Task.FromResult(claimed);
    }

    public Task CompleteAsync(string key, StoredResponse response, CancellationToken cancellationToken)
    {
        _entries[key] = new Entry(response.RequestHash, response);
        return Task.CompletedTask;
    }

    public Task ReleaseAsync(string key, CancellationToken cancellationToken)
    {
        _entries.TryRemove(key, out _);
        return Task.CompletedTask;
    }

    /// <summary>Exposed for the middleware: the hash recorded at claim time.</summary>
    public string? RequestHashFor(string key) =>
        _entries.TryGetValue(key, out var entry) ? entry.RequestHash : null;
}

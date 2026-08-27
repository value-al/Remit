namespace Remit.BuildingBlocks.Idempotency;

/// <summary>
/// A response captured for an idempotency key, replayed verbatim on retry.
/// </summary>
public sealed record StoredResponse(string RequestHash, int StatusCode, string ContentType, byte[] Body);

/// <summary>
/// Stores the first response produced for an idempotency key so that retries with the same
/// key and the same request body receive the same response, and retries with a different
/// body are rejected (ADR-0002).
/// </summary>
public interface IIdempotencyStore
{
    Task<StoredResponse?> GetAsync(string key, CancellationToken cancellationToken);

    /// <summary>
    /// Claims the key for the calling request. Returns <c>false</c> when the key is already
    /// claimed by a request that has not finished yet — the caller should return 409.
    /// </summary>
    Task<bool> TryClaimAsync(string key, string requestHash, CancellationToken cancellationToken);

    Task CompleteAsync(string key, StoredResponse response, CancellationToken cancellationToken);

    Task ReleaseAsync(string key, CancellationToken cancellationToken);
}

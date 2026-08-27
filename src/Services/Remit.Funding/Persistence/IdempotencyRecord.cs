namespace Remit.Funding.Persistence;

/// <summary>A claimed or completed <c>Idempotency-Key</c> (ADR-0002, ADR-0005).</summary>
public sealed class IdempotencyRecord
{
    private IdempotencyRecord()
    {
    }

    public string Key { get; private set; } = default!;
    public string RequestHash { get; private set; } = default!;
    public int? StatusCode { get; private set; }
    public string? ContentType { get; private set; }
    public byte[]? Body { get; private set; }
    public DateTimeOffset ClaimedAt { get; private set; }
    public DateTimeOffset? CompletedAt { get; private set; }

    public bool IsCompleted => CompletedAt is not null;

    public static IdempotencyRecord Claim(string key, string requestHash, DateTimeOffset at) => new()
    {
        Key = key,
        RequestHash = requestHash,
        ClaimedAt = at,
    };

    /// <summary>A crashed request leaves a claim behind; after a grace period it may be taken over.</summary>
    public bool IsStaleClaim(DateTimeOffset now, TimeSpan grace) => !IsCompleted && now - ClaimedAt > grace;

    public void Reclaim(string requestHash, DateTimeOffset at)
    {
        RequestHash = requestHash;
        ClaimedAt = at;
    }

    public void Complete(int statusCode, string contentType, byte[] body, DateTimeOffset at)
    {
        StatusCode = statusCode;
        ContentType = contentType;
        Body = body;
        CompletedAt = at;
    }
}

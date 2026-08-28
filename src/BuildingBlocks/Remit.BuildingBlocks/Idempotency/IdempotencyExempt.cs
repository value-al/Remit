using Microsoft.AspNetCore.Builder;

namespace Remit.BuildingBlocks.Idempotency;

/// <summary>
/// Endpoint metadata: this POST is not driven by a client-supplied <c>Idempotency-Key</c>.
/// Used for inbound webhooks, whose idempotency comes from the provider's event identity and
/// the aggregate's state machine instead (ADR-0006).
/// </summary>
public sealed class IdempotencyExemptMetadata
{
    public static readonly IdempotencyExemptMetadata Instance = new();
}

public static class IdempotencyExemptExtensions
{
    public static TBuilder ExemptFromIdempotency<TBuilder>(this TBuilder builder) where TBuilder : IEndpointConventionBuilder =>
        builder.WithMetadata(IdempotencyExemptMetadata.Instance);
}

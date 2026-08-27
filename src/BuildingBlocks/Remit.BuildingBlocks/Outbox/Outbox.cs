using System.Collections.Concurrent;

namespace Remit.BuildingBlocks.Outbox;

/// <summary>
/// A message recorded in the same unit of work as the state change that caused it, and
/// published to the broker afterwards by a relay (ADR-0003). Never publish directly from a
/// request handler: a crash between the commit and the publish is the dual-write bug.
/// </summary>
public sealed record OutboxMessage(
    Guid Id,
    string Type,
    string Payload,
    DateTimeOffset OccurredAt,
    string? CorrelationId = null);

public interface IOutbox
{
    Task EnqueueAsync(OutboxMessage message, CancellationToken cancellationToken);
}

/// <summary>
/// In-memory stand-in. The real implementation writes to an <c>outbox</c> table inside the
/// aggregate's transaction; a relay polls it and publishes to RabbitMQ with at-least-once
/// delivery, so every consumer is required to be idempotent on <see cref="OutboxMessage.Id"/>.
/// </summary>
public sealed class InMemoryOutbox : IOutbox
{
    private readonly ConcurrentQueue<OutboxMessage> _messages = new();

    public IReadOnlyCollection<OutboxMessage> Pending => [.. _messages];

    public Task EnqueueAsync(OutboxMessage message, CancellationToken cancellationToken)
    {
        _messages.Enqueue(message);
        return Task.CompletedTask;
    }
}

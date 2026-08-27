using Remit.BuildingBlocks.Outbox;

namespace Remit.Funding.Messaging;

/// <summary>The transport behind the outbox relay. Must not return before the broker has accepted the message.</summary>
public interface IMessagePublisher
{
    Task PublishAsync(OutboxMessage message, CancellationToken cancellationToken);
}

/// <summary>For the in-memory configuration and for tests that only need the relay's bookkeeping.</summary>
public sealed class NullMessagePublisher : IMessagePublisher
{
    public Task PublishAsync(OutboxMessage message, CancellationToken cancellationToken) => Task.CompletedTask;
}

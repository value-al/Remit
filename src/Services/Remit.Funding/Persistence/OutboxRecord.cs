using Remit.BuildingBlocks.Outbox;

namespace Remit.Funding.Persistence;

/// <summary>A row in <c>funding.outbox</c>. Written with the aggregate, drained by the relay.</summary>
public sealed class OutboxRecord
{
    private OutboxRecord()
    {
    }

    public Guid Id { get; private set; }
    public string Type { get; private set; } = default!;
    public string Payload { get; private set; } = default!;
    public DateTimeOffset OccurredAt { get; private set; }
    public string? CorrelationId { get; private set; }
    public DateTimeOffset? SentAt { get; private set; }
    public int Attempts { get; private set; }
    public string? LastError { get; private set; }

    public static OutboxRecord From(OutboxMessage message) => new()
    {
        Id = message.Id,
        Type = message.Type,
        Payload = message.Payload,
        OccurredAt = message.OccurredAt,
        CorrelationId = message.CorrelationId,
    };

    public OutboxMessage ToMessage() => new(Id, Type, Payload, OccurredAt, CorrelationId);

    public void MarkSent(DateTimeOffset at)
    {
        Attempts++;
        SentAt = at;
        LastError = null;
    }

    public void MarkFailed(string error)
    {
        Attempts++;
        LastError = error.Length <= 1024 ? error : error[..1024];
    }
}

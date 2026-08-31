using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Remit.BuildingBlocks.Messaging;
using Remit.Reconciliation.Matching;
using Remit.Reconciliation.Persistence;

namespace Remit.Reconciliation.Consuming;

public sealed record MovementPayload(
    [property: JsonPropertyName("Id")] Guid Id,
    [property: JsonPropertyName("AccountId")] Guid AccountId,
    [property: JsonPropertyName("Amount")] decimal Amount,
    [property: JsonPropertyName("Currency")] string Currency,
    [property: JsonPropertyName("Provider")] string? Provider,
    [property: JsonPropertyName("PspReference")] string? PspReference);

/// <summary>
/// Builds the reconciliation service's own record of every deposit and withdrawal from
/// Funding's events — requested, submitted, settled/paid, failed — so it can compare provider
/// statements against what we believed, without reading another service's tables (ADR-0009).
/// Idempotent on message id through the inbox, like the ledger. Events may arrive out of order;
/// a terminal status is never overwritten by an earlier one.
/// </summary>
public sealed class MovementHandler(IServiceScopeFactory scopes, TimeProvider clock, ILogger<MovementHandler> logger) : IMessageHandler
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static readonly IReadOnlyDictionary<string, (MovementKind Kind, string Status)> Types = new Dictionary<string, (MovementKind, string)>(StringComparer.Ordinal)
    {
        ["funding.deposit.requested.v1"] = (MovementKind.Deposit, "Requested"),
        ["funding.deposit.submitted.v1"] = (MovementKind.Deposit, "SubmittedToPsp"),
        ["funding.deposit.settled.v1"] = (MovementKind.Deposit, "Settled"),
        ["funding.deposit.failed.v1"] = (MovementKind.Deposit, "Failed"),
        ["funding.withdrawal.requested.v1"] = (MovementKind.Withdrawal, "Requested"),
        ["funding.withdrawal.submitted.v1"] = (MovementKind.Withdrawal, "SubmittedToPsp"),
        ["funding.withdrawal.paid.v1"] = (MovementKind.Withdrawal, "Paid"),
        ["funding.withdrawal.failed.v1"] = (MovementKind.Withdrawal, "Failed"),
    };

    private static readonly IReadOnlyDictionary<string, int> Rank = new Dictionary<string, int>(StringComparer.Ordinal)
    {
        ["Requested"] = 0,
        ["SubmittedToPsp"] = 1,
        ["Settled"] = 2,
        ["Paid"] = 2,
        ["Failed"] = 2,
    };

    public IReadOnlyList<string> Bindings { get; } = ["funding.deposit.#", "funding.withdrawal.#"];

    public async Task HandleAsync(IncomingMessage message, CancellationToken cancellationToken)
    {
        if (!Types.TryGetValue(message.Type, out var type))
        {
            logger.LogWarning("Ignoring unexpected message type {Type}.", message.Type);
            return;
        }

        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ReconciliationDbContext>();

        if (await db.Inbox.AnyAsync(i => i.MessageId == message.Id, cancellationToken))
        {
            return;
        }

        var payload = JsonSerializer.Deserialize<MovementPayload>(message.Payload, Json)
            ?? throw new InvalidOperationException($"Message {message.Id} has no payload.");

        var movement = await db.Movements.FirstOrDefaultAsync(m => m.Id == payload.Id, cancellationToken);
        if (movement is null)
        {
            db.Movements.Add(MovementRecord.Start(payload.Id, type.Kind, payload.AccountId, payload.Amount, payload.Currency, type.Status, payload.Provider, payload.PspReference, message.OccurredAt));
        }
        else if (Rank[type.Status] >= Rank[movement.Status])
        {
            movement.Apply(type.Status, payload.Provider, payload.PspReference, message.OccurredAt);
        }
        else
        {
            // A late "submitted" after "settled": keep the terminal state, still take the reference.
            movement.Apply(movement.Status, payload.Provider, payload.PspReference, movement.LastEventAt);
        }

        db.Inbox.Add(InboxRecord.For(message.Id, clock.GetUtcNow()));

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException e) when (e.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            logger.LogInformation("Message {MessageId} handled concurrently elsewhere; skipping.", message.Id);
        }
    }
}

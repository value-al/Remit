using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Remit.BuildingBlocks;
using Remit.BuildingBlocks.Messaging;
using Remit.Ledger.Persistence;

namespace Remit.Ledger.Consuming;

/// <summary>The fields the ledger needs from a Funding settlement message; everything else is ignored.</summary>
public sealed record SettlementPayload(
    [property: JsonPropertyName("Id")] Guid Id,
    [property: JsonPropertyName("AccountId")] Guid AccountId,
    [property: JsonPropertyName("Amount")] decimal Amount,
    [property: JsonPropertyName("Currency")] string Currency,
    [property: JsonPropertyName("Provider")] string? Provider);

/// <summary>
/// Turns Funding's settlement events into journal entries (ADR-0007).
///
///   deposit settled   → debit  psp:receivable:{provider}   credit client:wallet:{account}
///   withdrawal paid   → debit  client:wallet:{account}      credit psp:payable:{provider}
///
/// Idempotent on the message id: the inbox row and the entry are written in one transaction,
/// so a redelivery either finds the inbox row and does nothing, or loses the race on the
/// primary key and rolls back. Client wallets are liabilities — credit-normal.
/// </summary>
public sealed class SettlementHandler(IServiceScopeFactory scopes, TimeProvider clock, ILogger<SettlementHandler> logger) : IMessageHandler
{
    public const string DepositSettled = "funding.deposit.settled.v1";
    public const string WithdrawalPaid = "funding.withdrawal.paid.v1";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public IReadOnlyList<string> Bindings { get; } = [DepositSettled, WithdrawalPaid];

    public async Task HandleAsync(IncomingMessage message, CancellationToken cancellationToken)
    {
        if (message.Type is not (DepositSettled or WithdrawalPaid))
        {
            logger.LogWarning("Ignoring unexpected message type {Type}.", message.Type);
            return;
        }

        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LedgerDbContext>();

        if (await db.Inbox.AnyAsync(i => i.MessageId == message.Id, cancellationToken))
        {
            logger.LogInformation("Message {MessageId} already posted; skipping.", message.Id);
            return;
        }

        var payload = JsonSerializer.Deserialize<SettlementPayload>(message.Payload, Json)
            ?? throw new InvalidOperationException($"Message {message.Id} has no payload.");

        var entry = Build(message.Type, payload);

        db.Inbox.Add(InboxRecord.For(message.Id, message.Type, clock.GetUtcNow()));
        db.Entries.Add(JournalEntryRecord.From(entry, message.CorrelationId));

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException e) when (e.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // Two deliveries raced; the other one won. Nothing to do.
            logger.LogInformation("Message {MessageId} posted concurrently by another consumer; skipping.", message.Id);
        }
    }

    public JournalEntry Build(string type, SettlementPayload payload)
    {
        var amount = Money.Of(payload.Amount, payload.Currency);
        var wallet = $"client:wallet:{payload.AccountId}";
        var provider = payload.Provider ?? "unknown";

        return type switch
        {
            DepositSettled => JournalEntry.Create($"Deposit {payload.Id} settled via {provider}", clock.GetUtcNow(),
            [
                new Posting($"psp:receivable:{provider}", amount, Side.Debit),
                new Posting(wallet, amount, Side.Credit),
            ]),
            WithdrawalPaid => JournalEntry.Create($"Withdrawal {payload.Id} paid via {provider}", clock.GetUtcNow(),
            [
                new Posting(wallet, amount, Side.Debit),
                new Posting($"psp:payable:{provider}", amount, Side.Credit),
            ]),
            _ => throw new ArgumentOutOfRangeException(nameof(type)),
        };
    }
}

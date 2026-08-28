using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Remit.BuildingBlocks.Messaging;
using Remit.Funding.Persistence;

namespace Remit.Funding.Messaging;

public sealed class OutboxRelayOptions
{
    public const string Section = "OutboxRelay";

    public TimeSpan PollInterval { get; set; } = TimeSpan.FromMilliseconds(500);
    public int BatchSize { get; set; } = 50;
}

/// <summary>
/// Drains <c>funding.outbox</c> to the broker (ADR-0003). Each pass locks a batch of unsent rows
/// with <c>FOR UPDATE SKIP LOCKED</c>, so several relay instances can run side by side without
/// publishing the same row twice; a failed publish records the error and leaves the row for the
/// next pass. Ordering is per batch, not global — consumers must not depend on it.
/// </summary>
public sealed class OutboxRelay(
    IServiceScopeFactory scopes,
    IMessagePublisher publisher,
    IOptions<OutboxRelayOptions> options,
    TimeProvider clock,
    ILogger<OutboxRelay> logger) : BackgroundService
{
    private static readonly ActivitySource Activity = new("Remit.Outbox");

    private readonly OutboxRelayOptions _options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Outbox relay started; polling every {Interval} in batches of {Batch}.", _options.PollInterval, _options.BatchSize);

        while (!stoppingToken.IsCancellationRequested)
        {
            int relayed;
            try
            {
                relayed = await RelayOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception e)
            {
                logger.LogError(e, "Outbox relay pass failed; will retry.");
                relayed = 0;
            }

            if (relayed == 0)
            {
                await Task.Delay(_options.PollInterval, stoppingToken);
            }
        }
    }

    /// <summary>One pass. Public so tests can drive the relay deterministically.</summary>
    public async Task<int> RelayOnceAsync(CancellationToken cancellationToken)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FundingDbContext>();

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        var batch = await db.Outbox
            .FromSqlRaw(
                $"SELECT * FROM {FundingDbContext.Schema}.outbox WHERE sent_at IS NULL ORDER BY occurred_at LIMIT {{0}} FOR UPDATE SKIP LOCKED",
                _options.BatchSize)
            .ToListAsync(cancellationToken);

        foreach (var record in batch)
        {
            // The relay runs outside any request, so each publish starts its own trace here;
            // the consumer's span becomes its child through the message headers.
            using var span = Activity.StartActivity($"relay {record.Type}", ActivityKind.Internal);
            span?.SetTag("messaging.message.id", record.Id.ToString());
            span?.SetTag("remit.correlation_id", record.CorrelationId);
            try
            {
                await publisher.PublishAsync(record.ToMessage(), cancellationToken);
                record.MarkSent(clock.GetUtcNow());
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                record.MarkFailed(e.Message);
                logger.LogWarning(e, "Publishing outbox message {MessageId} ({Type}) failed on attempt {Attempt}.", record.Id, record.Type, record.Attempts);
            }
        }

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return batch.Count;
    }
}

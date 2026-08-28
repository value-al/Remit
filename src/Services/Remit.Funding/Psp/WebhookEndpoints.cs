using System.Text.Json;
using System.Text.Json.Serialization;
using Countersign;
using Microsoft.Extensions.Options;
using Remit.BuildingBlocks;
using Remit.BuildingBlocks.Idempotency;
using Remit.BuildingBlocks.Outbox;
using Remit.Funding.Deposits;
using Remit.Funding.Withdrawals;

namespace Remit.Funding.Psp;

/// <summary>
/// The shape every simulated provider posts back. Real adapters translate to this. Exactly one
/// of <see cref="DepositId"/> / <see cref="WithdrawalId"/> is set.
/// </summary>
public sealed record PspWebhook(
    [property: JsonPropertyName("eventId")] string EventId,
    [property: JsonPropertyName("reference")] string Reference,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("depositId")] Guid? DepositId = null,
    [property: JsonPropertyName("withdrawalId")] Guid? WithdrawalId = null,
    [property: JsonPropertyName("reason")] string? Reason = null);

/// <summary>One verifier per provider, each with that provider's own webhook secret.</summary>
public sealed class WebhookVerifiers
{
    public const string TimestampHeader = "X-Timestamp";
    public const string SignatureHeader = "X-Signature";
    public static readonly TimeSpan Tolerance = TimeSpan.FromMinutes(5);

    private readonly Dictionary<string, WebhookVerifier> _verifiers;

    public WebhookVerifiers(IOptions<PspOptions> options, TimeProvider clock)
    {
        _verifiers = new Dictionary<string, WebhookVerifier>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, provider) in options.Value.Providers)
        {
            if (string.IsNullOrEmpty(provider.WebhookSecret))
            {
                continue;
            }

            _verifiers[name] = new WebhookVerifier(
                provider.WebhookSecret,
                CanonicalForms.TimestampDotBody,
                tolerance: Tolerance,
                clock: clock.GetUtcNow);
        }
    }

    public WebhookVerifier? For(string provider) => _verifiers.GetValueOrDefault(provider);
}

public static class WebhookEndpoints
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static IEndpointRouteBuilder MapPspWebhooks(this IEndpointRouteBuilder app)
    {
        // Exempt from Idempotency-Key: providers do not send one. Replays are handled by the
        // signature's timestamp tolerance and by the aggregates' state machines (ADR-0006).
        app.MapPost("/webhooks/psp/{provider}", HandleAsync)
            .WithTags("Webhooks")
            .ExemptFromIdempotency();

        return app;
    }

    private static async Task<IResult> HandleAsync(
        string provider,
        HttpRequest request,
        WebhookVerifiers verifiers,
        IDepositRepository deposits,
        IWithdrawalRepository withdrawals,
        IOutbox outbox,
        IUnitOfWork unitOfWork,
        TimeProvider clock,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        var logger = loggerFactory.CreateLogger("Remit.Funding.Webhooks");

        var verifier = verifiers.For(provider);
        if (verifier is null)
        {
            return Results.NotFound();
        }

        // Verify over the exact bytes received — never a re-serialised body.
        using var buffer = new MemoryStream();
        await request.Body.CopyToAsync(buffer, cancellationToken);
        var rawBody = buffer.ToArray();

        var timestamp = request.Headers[WebhookVerifiers.TimestampHeader].ToString();
        var signature = request.Headers[WebhookVerifiers.SignatureHeader].ToString();
        if (string.IsNullOrEmpty(timestamp) || string.IsNullOrEmpty(signature) || !long.TryParse(timestamp, out var unixSeconds))
        {
            return Results.Unauthorized();
        }

        var verification = verifier.Verify(
            new SignatureContext(rawBody, timestamp: timestamp),
            signature,
            DateTimeOffset.FromUnixTimeSeconds(unixSeconds));

        if (verification != VerificationResult.Valid)
        {
            logger.LogWarning("Rejected webhook from {Provider}: {Result}.", provider, verification);
            return Results.Unauthorized();
        }

        PspWebhook? webhook;
        try
        {
            webhook = JsonSerializer.Deserialize<PspWebhook>(rawBody, Json);
        }
        catch (JsonException)
        {
            webhook = null;
        }

        if (webhook is null || (webhook.DepositId is null) == (webhook.WithdrawalId is null))
        {
            return Results.BadRequest("Webhook must name exactly one of depositId or withdrawalId.");
        }

        return webhook.DepositId is { } depositId
            ? await ApplyToDepositAsync(provider, webhook, depositId, deposits, outbox, unitOfWork, clock, logger, cancellationToken)
            : await ApplyToWithdrawalAsync(provider, webhook, webhook.WithdrawalId!.Value, withdrawals, outbox, unitOfWork, clock, logger, cancellationToken);
    }

    private static async Task<IResult> ApplyToDepositAsync(
        string provider, PspWebhook webhook, Guid depositId, IDepositRepository deposits, IOutbox outbox, IUnitOfWork unitOfWork, TimeProvider clock, ILogger logger, CancellationToken cancellationToken)
    {
        var deposit = await deposits.FindAsync(depositId, cancellationToken);
        if (deposit is null)
        {
            // Ack it: a 404 would make the provider retry a deposit we will never have.
            logger.LogWarning("Webhook {EventId} from {Provider} references unknown deposit {DepositId}.", webhook.EventId, provider, depositId);
            return Results.Ok(new { applied = false, reason = "unknown-deposit" });
        }

        if (!string.Equals(deposit.Provider, provider, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(deposit.PspReference, webhook.Reference, StringComparison.Ordinal))
        {
            logger.LogWarning("Webhook {EventId} from {Provider} does not match deposit {DepositId} (provider {DepositProvider}, reference {Reference}).", webhook.EventId, provider, deposit.Id, deposit.Provider, deposit.PspReference);
            return Results.Ok(new { applied = false, reason = "reference-mismatch" });
        }

        string messageType;
        try
        {
            switch (webhook.Status.ToLowerInvariant())
            {
                case "settled":
                    deposit.MarkSettled(clock);
                    messageType = "funding.deposit.settled.v1";
                    break;
                case "failed":
                    deposit.MarkFailed(webhook.Reason ?? $"{provider} reported failure.", clock);
                    messageType = "funding.deposit.failed.v1";
                    break;
                default:
                    return Results.BadRequest($"Unknown deposit status '{webhook.Status}'.");
            }
        }
        catch (InvalidDepositTransitionException e)
        {
            // Duplicate or late delivery. The state machine refused it; the provider gets a 200
            // so it stops retrying. Nothing changed, so nothing is committed.
            logger.LogInformation("Webhook {EventId} from {Provider} ignored: {Reason}.", webhook.EventId, provider, e.Message);
            return Results.Ok(new { applied = false, reason = "already-final", status = deposit.Status.ToString() });
        }

        await deposits.SaveAsync(deposit, cancellationToken);
        await outbox.EnqueueAsync(
            new OutboxMessage(
                Id: Guid.NewGuid(),
                Type: messageType,
                Payload: JsonSerializer.Serialize(new { deposit.Id, deposit.AccountId, deposit.Amount.Amount, deposit.Amount.Currency, deposit.Provider, deposit.PspReference, webhook.EventId, deposit.FailureReason }),
                OccurredAt: clock.GetUtcNow(),
                CorrelationId: deposit.Id.ToString()),
            cancellationToken);
        await unitOfWork.CommitAsync(cancellationToken);

        return Results.Ok(new { applied = true, status = deposit.Status.ToString() });
    }

    private static async Task<IResult> ApplyToWithdrawalAsync(
        string provider, PspWebhook webhook, Guid withdrawalId, IWithdrawalRepository withdrawals, IOutbox outbox, IUnitOfWork unitOfWork, TimeProvider clock, ILogger logger, CancellationToken cancellationToken)
    {
        var withdrawal = await withdrawals.FindAsync(withdrawalId, cancellationToken);
        if (withdrawal is null)
        {
            logger.LogWarning("Webhook {EventId} from {Provider} references unknown withdrawal {WithdrawalId}.", webhook.EventId, provider, withdrawalId);
            return Results.Ok(new { applied = false, reason = "unknown-withdrawal" });
        }

        if (!string.Equals(withdrawal.Provider, provider, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(withdrawal.PspReference, webhook.Reference, StringComparison.Ordinal))
        {
            logger.LogWarning("Webhook {EventId} from {Provider} does not match withdrawal {WithdrawalId}.", webhook.EventId, provider, withdrawal.Id);
            return Results.Ok(new { applied = false, reason = "reference-mismatch" });
        }

        string messageType;
        try
        {
            switch (webhook.Status.ToLowerInvariant())
            {
                case "paid":
                    withdrawal.MarkPaid(clock);
                    messageType = "funding.withdrawal.paid.v1";
                    break;
                case "failed":
                    withdrawal.MarkFailed(webhook.Reason ?? $"{provider} reported failure.", clock);
                    messageType = "funding.withdrawal.failed.v1";
                    break;
                default:
                    return Results.BadRequest($"Unknown withdrawal status '{webhook.Status}'.");
            }
        }
        catch (InvalidWithdrawalTransitionException e)
        {
            logger.LogInformation("Webhook {EventId} from {Provider} ignored: {Reason}.", webhook.EventId, provider, e.Message);
            return Results.Ok(new { applied = false, reason = "already-final", status = withdrawal.Status.ToString() });
        }

        await withdrawals.SaveAsync(withdrawal, cancellationToken);
        await outbox.EnqueueAsync(WithdrawalEndpoints.Message(messageType, withdrawal, clock), cancellationToken);
        await unitOfWork.CommitAsync(cancellationToken);

        return Results.Ok(new { applied = true, status = withdrawal.Status.ToString() });
    }
}

using System.Text.Json;
using Remit.BuildingBlocks;
using Remit.BuildingBlocks.Idempotency;
using Remit.BuildingBlocks.Outbox;
using Remit.Funding.Psp;

namespace Remit.Funding.Deposits;

public sealed record RequestDepositCommand(Guid AccountId, decimal Amount, string Currency);

public sealed record DepositResponse(
    Guid Id,
    Guid AccountId,
    decimal Amount,
    string Currency,
    string Status,
    string? Provider,
    string? PspReference,
    string? FailureReason);

public static class DepositEndpoints
{
    public static IEndpointRouteBuilder MapDeposits(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/deposits").WithTags("Deposits");

        group.MapPost("/", async (
            RequestDepositCommand command,
            HttpRequest request,
            IDepositRepository deposits,
            IOutbox outbox,
            IUnitOfWork unitOfWork,
            PspRouter router,
            TimeProvider clock,
            CancellationToken cancellationToken) =>
        {
            Money amount;
            try
            {
                amount = Money.Of(command.Amount, command.Currency);
            }
            catch (ArgumentException e)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["currency"] = [e.Message] });
            }

            if (!amount.IsPositive)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["amount"] = ["Amount must be positive."] });
            }

            var deposit = Deposit.Request(command.AccountId, amount, clock);

            // Commit 1 — the deposit exists before any provider hears about it (ADR-0006).
            // The row and its outbox message are one unit of work (ADR-0003).
            await deposits.SaveAsync(deposit, cancellationToken);
            await outbox.EnqueueAsync(Message("funding.deposit.requested.v1", deposit, clock), cancellationToken);
            await unitOfWork.CommitAsync(cancellationToken);

            // Submit synchronously: the client learns at once whether a provider took the charge.
            // The provider's own idempotency key is ours, so a retried submission cannot double-charge.
            var outcome = await router.ChargeAsync(
                new PspChargeRequest(deposit.Id, deposit.AccountId, deposit.Amount, request.Headers[IdempotencyMiddleware.HeaderName].ToString()),
                cancellationToken);

            string messageType;
            switch (outcome.Result)
            {
                case PspChargeResult.Accepted accepted:
                    deposit.MarkSubmitted(outcome.Provider!, accepted.Reference, clock);
                    messageType = "funding.deposit.submitted.v1";
                    break;
                case PspChargeResult.Rejected rejected:
                    deposit.MarkFailed($"{outcome.Provider ?? "routing"}: {rejected.Reason}", clock);
                    messageType = "funding.deposit.failed.v1";
                    break;
                case PspChargeResult.Unavailable unavailable:
                    deposit.MarkFailed($"No provider available after {string.Join(", ", outcome.Attempted)}: {unavailable.Error}", clock);
                    messageType = "funding.deposit.failed.v1";
                    break;
                default:
                    throw new InvalidOperationException("Unknown routing result.");
            }

            // Commit 2 — the outcome, with its message.
            await deposits.SaveAsync(deposit, cancellationToken);
            await outbox.EnqueueAsync(Message(messageType, deposit, clock), cancellationToken);
            await unitOfWork.CommitAsync(cancellationToken);

            return Results.Accepted($"/deposits/{deposit.Id}", ToResponse(deposit));
        });

        group.MapGet("/{id:guid}", async (Guid id, IDepositRepository deposits, CancellationToken cancellationToken) =>
        {
            var deposit = await deposits.FindAsync(id, cancellationToken);
            return deposit is null ? Results.NotFound() : Results.Ok(ToResponse(deposit));
        });

        return app;
    }

    private static OutboxMessage Message(string type, Deposit deposit, TimeProvider clock) =>
        new(
            Id: Guid.NewGuid(),
            Type: type,
            Payload: JsonSerializer.Serialize(new { deposit.Id, deposit.AccountId, deposit.Amount.Amount, deposit.Amount.Currency, deposit.Provider, deposit.PspReference, deposit.FailureReason }),
            OccurredAt: clock.GetUtcNow(),
            CorrelationId: deposit.Id.ToString());

    private static DepositResponse ToResponse(Deposit d) =>
        new(d.Id, d.AccountId, d.Amount.Amount, d.Amount.Currency, d.Status.ToString(), d.Provider, d.PspReference, d.FailureReason);
}

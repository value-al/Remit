using System.Text.Json;
using Remit.BuildingBlocks;
using Remit.BuildingBlocks.Idempotency;
using Remit.BuildingBlocks.Outbox;
using Remit.Funding.Psp;

namespace Remit.Funding.Withdrawals;

public sealed record RequestWithdrawalCommand(Guid AccountId, decimal Amount, string Currency, string Destination);

public sealed record WithdrawalResponse(
    Guid Id,
    Guid AccountId,
    decimal Amount,
    string Currency,
    string Status,
    string? Provider,
    string? PspReference,
    string? FailureReason);

public static class WithdrawalEndpoints
{
    public static IEndpointRouteBuilder MapWithdrawals(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/withdrawals").WithTags("Withdrawals");

        group.MapPost("/", async (
            RequestWithdrawalCommand command,
            HttpRequest request,
            IWithdrawalRepository withdrawals,
            ILedgerBalances balances,
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

            if (string.IsNullOrWhiteSpace(command.Destination))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["destination"] = ["A payout destination reference is required."] });
            }

            // Advisory check against the journal (ADR-0007). Not a hold.
            var available = await balances.AvailableAsync(command.AccountId, amount.Currency, cancellationToken);
            if (available.Amount < amount.Amount)
            {
                return Results.Problem(
                    title: "Insufficient funds",
                    detail: $"Available {available}, requested {amount}.",
                    statusCode: StatusCodes.Status422UnprocessableEntity);
            }

            var withdrawal = Withdrawal.Request(command.AccountId, amount, clock);

            await withdrawals.SaveAsync(withdrawal, cancellationToken);
            await outbox.EnqueueAsync(Message("funding.withdrawal.requested.v1", withdrawal, clock), cancellationToken);
            await unitOfWork.CommitAsync(cancellationToken);

            var outcome = await router.PayoutAsync(
                new PspPayoutRequest(withdrawal.Id, withdrawal.AccountId, withdrawal.Amount, command.Destination, request.Headers[IdempotencyMiddleware.HeaderName].ToString()),
                cancellationToken);

            string messageType;
            switch (outcome.Result)
            {
                case PspChargeResult.Accepted accepted:
                    withdrawal.MarkSubmitted(outcome.Provider!, accepted.Reference, clock);
                    messageType = "funding.withdrawal.submitted.v1";
                    break;
                case PspChargeResult.Rejected rejected:
                    withdrawal.MarkFailed($"{outcome.Provider ?? "routing"}: {rejected.Reason}", clock);
                    messageType = "funding.withdrawal.failed.v1";
                    break;
                case PspChargeResult.Unavailable unavailable:
                    withdrawal.MarkFailed($"No provider available after {string.Join(", ", outcome.Attempted)}: {unavailable.Error}", clock);
                    messageType = "funding.withdrawal.failed.v1";
                    break;
                default:
                    throw new InvalidOperationException("Unknown routing result.");
            }

            await withdrawals.SaveAsync(withdrawal, cancellationToken);
            await outbox.EnqueueAsync(Message(messageType, withdrawal, clock), cancellationToken);
            await unitOfWork.CommitAsync(cancellationToken);

            return Results.Accepted($"/withdrawals/{withdrawal.Id}", ToResponse(withdrawal));
        });

        group.MapGet("/{id:guid}", async (Guid id, IWithdrawalRepository withdrawals, CancellationToken cancellationToken) =>
        {
            var withdrawal = await withdrawals.FindAsync(id, cancellationToken);
            return withdrawal is null ? Results.NotFound() : Results.Ok(ToResponse(withdrawal));
        });

        return app;
    }

    public static OutboxMessage Message(string type, Withdrawal w, TimeProvider clock) =>
        new(
            Id: Guid.NewGuid(),
            Type: type,
            Payload: JsonSerializer.Serialize(new { w.Id, w.AccountId, w.Amount.Amount, w.Amount.Currency, w.Provider, w.PspReference, w.FailureReason }),
            OccurredAt: clock.GetUtcNow(),
            CorrelationId: w.Id.ToString());

    public static WithdrawalResponse ToResponse(Withdrawal w) =>
        new(w.Id, w.AccountId, w.Amount.Amount, w.Amount.Currency, w.Status.ToString(), w.Provider, w.PspReference, w.FailureReason);
}

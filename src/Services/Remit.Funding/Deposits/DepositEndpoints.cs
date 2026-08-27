using System.Text.Json;
using Remit.BuildingBlocks;
using Remit.BuildingBlocks.Outbox;

namespace Remit.Funding.Deposits;

public sealed record RequestDepositCommand(Guid AccountId, decimal Amount, string Currency);

public sealed record DepositResponse(Guid Id, Guid AccountId, decimal Amount, string Currency, string Status, string? PspReference);

public static class DepositEndpoints
{
    public static IEndpointRouteBuilder MapDeposits(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/deposits").WithTags("Deposits");

        group.MapPost("/", async (
            RequestDepositCommand command,
            IDepositRepository deposits,
            IOutbox outbox,
            IUnitOfWork unitOfWork,
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

            // The deposit row and its outbox message are one unit of work (ADR-0003):
            // both are staged here and written by a single commit below.
            await deposits.SaveAsync(deposit, cancellationToken);
            await outbox.EnqueueAsync(
                new OutboxMessage(
                    Id: Guid.NewGuid(),
                    Type: "funding.deposit.requested.v1",
                    Payload: JsonSerializer.Serialize(new { deposit.Id, deposit.AccountId, deposit.Amount.Amount, deposit.Amount.Currency }),
                    OccurredAt: deposit.RequestedAt,
                    CorrelationId: deposit.Id.ToString()),
                cancellationToken);
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

    private static DepositResponse ToResponse(Deposit d) =>
        new(d.Id, d.AccountId, d.Amount.Amount, d.Amount.Currency, d.Status.ToString(), d.PspReference);
}

using Microsoft.EntityFrameworkCore;
using Remit.Ledger.Persistence;

namespace Remit.Ledger.Balances;

public sealed record BalanceResponse(Guid AccountId, string Currency, decimal Balance, int Postings);

public sealed record EntryResponse(Guid Id, string Description, DateTimeOffset PostedAt, string? CorrelationId, IReadOnlyList<PostingResponse> Postings);

public sealed record PostingResponse(string Account, decimal Amount, string Currency, string Side);

public static class BalanceEndpoints
{
    public static IEndpointRouteBuilder MapLedger(this IEndpointRouteBuilder app)
    {
        // Balance is derived from the journal every time (ADR-0004). No stored balance exists to drift.
        app.MapGet("/accounts/{accountId:guid}/balance", async (Guid accountId, string currency, LedgerDbContext db, CancellationToken cancellationToken) =>
        {
            var account = $"client:wallet:{accountId}";
            var upper = currency.ToUpperInvariant();

            var rows = await db.Postings
                .Where(p => p.Account == account && p.Currency == upper)
                .Select(p => new { p.Amount, p.Side })
                .ToListAsync(cancellationToken);

            var balance = rows.Sum(r => r.Side == Side.Credit ? r.Amount : -r.Amount);
            return Results.Ok(new BalanceResponse(accountId, upper, balance, rows.Count));
        }).WithTags("Balances");

        app.MapGet("/entries/{id:guid}", async (Guid id, LedgerDbContext db, CancellationToken cancellationToken) =>
        {
            var entry = await db.Entries.AsNoTracking().FirstOrDefaultAsync(e => e.Id == id, cancellationToken);
            return entry is null
                ? Results.NotFound()
                : Results.Ok(new EntryResponse(entry.Id, entry.Description, entry.PostedAt, entry.CorrelationId,
                    entry.Postings.Select(p => new PostingResponse(p.Account, p.Amount, p.Currency, p.Side.ToString())).ToList()));
        }).WithTags("Entries");

        app.MapGet("/entries", async (string? correlationId, LedgerDbContext db, CancellationToken cancellationToken) =>
        {
            var query = db.Entries.AsNoTracking();
            if (!string.IsNullOrEmpty(correlationId))
            {
                query = query.Where(e => e.CorrelationId == correlationId);
            }

            var entries = await query.OrderByDescending(e => e.PostedAt).Take(100).ToListAsync(cancellationToken);
            return Results.Ok(entries.Select(entry => new EntryResponse(entry.Id, entry.Description, entry.PostedAt, entry.CorrelationId,
                entry.Postings.Select(p => new PostingResponse(p.Account, p.Amount, p.Currency, p.Side.ToString())).ToList())));
        }).WithTags("Entries");

        return app;
    }
}

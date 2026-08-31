using Microsoft.EntityFrameworkCore;
using Remit.Reconciliation.Matching;
using Remit.Reconciliation.Persistence;

namespace Remit.Reconciliation.Statements;

public sealed record StatementResult(Guid StatementId, string Provider, int Lines, int Matched, int Exceptions, IReadOnlyList<ExceptionView> Raised);

public sealed record ExceptionView(Guid Id, string Kind, string Provider, string? Reference, Guid? MovementId, string Detail, DateTimeOffset RaisedAt, DateTimeOffset? ResolvedAt, string? Resolution);

public sealed record ResolveCommand(string Resolution);

public static class ReconciliationEndpoints
{
    public static IEndpointRouteBuilder MapReconciliation(this IEndpointRouteBuilder app)
    {
        // POST /statements/{provider}?from=2026-08-01&to=2026-09-01   body: text/csv
        // The statement is matched against what we expected from that provider in that period.
        // Re-posting the same statement raises no duplicate exceptions (unique open exception per reference).
        app.MapPost("/statements/{provider}", async (
            string provider,
            DateTimeOffset from,
            DateTimeOffset to,
            HttpRequest request,
            ReconciliationDbContext db,
            TimeProvider clock,
            CancellationToken cancellationToken) =>
        {
            using var reader = new StreamReader(request.Body);
            IReadOnlyList<StatementLine> lines;
            try
            {
                lines = StatementCsv.Parse(await reader.ReadToEndAsync(cancellationToken));
            }
            catch (FormatException e)
            {
                return Results.Problem(title: "Statement did not parse", detail: e.Message, statusCode: StatusCodes.Status400BadRequest);
            }

            var expected = await db.Movements
                .Where(m => m.Provider == provider && m.Reference != null)
                .ToListAsync(cancellationToken);

            var result = StatementMatcher.Match(lines, expected.Select(m => m.ToExpected()).ToList(), from, to);

            var now = clock.GetUtcNow();
            var statement = StatementRecord.Create(provider, from, to, lines.Count, result.Matched.Count, result.Exceptions.Count, now);
            db.Statements.Add(statement);

            var openKeys = (await db.Exceptions
                    .Where(e => e.Provider == provider && e.ResolvedAt == null && e.Reference != null)
                    .Select(e => new { e.Kind, e.Reference })
                    .ToListAsync(cancellationToken))
                .Select(x => (x.Kind, x.Reference!))
                .ToHashSet();

            var raised = new List<ExceptionRecord>();
            foreach (var (kind, line, movement, detail) in result.Exceptions)
            {
                var reference = line?.Reference ?? movement?.Reference;
                if (reference is not null && !openKeys.Add((kind.ToString(), reference)))
                {
                    continue; // already open from a previous statement
                }

                var record = ExceptionRecord.Raise(kind.ToString(), provider, reference, movement?.Id, statement.Id, detail, now);
                db.Exceptions.Add(record);
                raised.Add(record);
            }

            await db.SaveChangesAsync(cancellationToken);

            return Results.Ok(new StatementResult(statement.Id, provider, lines.Count, result.Matched.Count, result.Exceptions.Count, raised.Select(View).ToList()));
        }).WithTags("Statements").Accepts<string>("text/csv");

        app.MapGet("/exceptions", async (string? provider, bool? open, ReconciliationDbContext db, CancellationToken cancellationToken) =>
        {
            var query = db.Exceptions.AsNoTracking();
            if (!string.IsNullOrEmpty(provider))
            {
                query = query.Where(e => e.Provider == provider);
            }

            if (open ?? true)
            {
                query = query.Where(e => e.ResolvedAt == null);
            }

            var items = await query.OrderBy(e => e.RaisedAt).Take(500).ToListAsync(cancellationToken);
            return Results.Ok(items.Select(View));
        }).WithTags("Exceptions");

        // Resolution is a human decision with a written reason. The service records it; it never decides.
        app.MapPost("/exceptions/{id:guid}/resolve", async (Guid id, ResolveCommand command, ReconciliationDbContext db, TimeProvider clock, CancellationToken cancellationToken) =>
        {
            if (string.IsNullOrWhiteSpace(command.Resolution))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["resolution"] = ["A written resolution is required."] });
            }

            var exception = await db.Exceptions.FirstOrDefaultAsync(e => e.Id == id, cancellationToken);
            if (exception is null)
            {
                return Results.NotFound();
            }

            if (!exception.IsOpen)
            {
                return Results.Conflict(new { reason = "already-resolved", exception.ResolvedAt });
            }

            exception.Resolve(command.Resolution.Trim(), clock.GetUtcNow());
            await db.SaveChangesAsync(cancellationToken);
            return Results.Ok(View(exception));
        }).WithTags("Exceptions");

        app.MapGet("/movements/{id:guid}", async (Guid id, ReconciliationDbContext db, CancellationToken cancellationToken) =>
        {
            var m = await db.Movements.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
            return m is null ? Results.NotFound() : Results.Ok(new { m.Id, Kind = m.Kind.ToString(), m.AccountId, m.Amount, m.Currency, m.Status, m.Provider, m.Reference, m.FirstEventAt, m.LastEventAt });
        }).WithTags("Movements");

        return app;
    }

    public static ExceptionView View(ExceptionRecord e) =>
        new(e.Id, e.Kind, e.Provider, e.Reference, e.MovementId, e.Detail, e.RaisedAt, e.ResolvedAt, e.Resolution);
}

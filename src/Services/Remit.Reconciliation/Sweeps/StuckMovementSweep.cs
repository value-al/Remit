using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Remit.Reconciliation.Persistence;

namespace Remit.Reconciliation.Sweeps;

public sealed class SweepOptions
{
    public const string Section = "Sweeps";

    /// <summary>How long a movement may sit in Requested or SubmittedToPsp before it is an exception.</summary>
    public TimeSpan StuckAfter { get; set; } = TimeSpan.FromMinutes(30);

    public TimeSpan Interval { get; set; } = TimeSpan.FromMinutes(5);
}

/// <summary>
/// Closes the gap ADR-0006 left open: a crash between Funding's two commits leaves a movement in
/// Requested with no provider; a webhook that never arrives leaves one in SubmittedToPsp. Neither
/// shows up on any statement, so nothing else would ever notice. This sweep raises a Stuck
/// exception for each, once, and leaves the decision to a person.
/// </summary>
public sealed class StuckMovementSweep(IServiceScopeFactory scopes, IOptions<SweepOptions> options, TimeProvider clock, ILogger<StuckMovementSweep> logger) : BackgroundService
{
    public const string Kind = "Stuck";

    private readonly SweepOptions _options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await SweepOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception e)
            {
                logger.LogError(e, "Stuck-movement sweep failed; will retry.");
            }

            try
            {
                await Task.Delay(_options.Interval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>One pass. Public so tests and the /sweeps/stuck endpoint can run it on demand.</summary>
    public async Task<int> SweepOnceAsync(CancellationToken cancellationToken)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ReconciliationDbContext>();
        var now = clock.GetUtcNow();
        var cutoff = now - _options.StuckAfter;

        var stuck = await db.Movements
            .Where(m => (m.Status == "Requested" || m.Status == "SubmittedToPsp") && m.LastEventAt < cutoff)
            .Where(m => !db.Exceptions.Any(e => e.Kind == Kind && e.MovementId == m.Id && e.ResolvedAt == null))
            .ToListAsync(cancellationToken);

        foreach (var m in stuck)
        {
            db.Exceptions.Add(ExceptionRecord.Raise(
                Kind,
                m.Provider ?? "none",
                m.Reference,
                m.Id,
                null,
                $"{m.Kind} {m.Id} has been {m.Status} since {m.LastEventAt:u} ({(now - m.LastEventAt).TotalMinutes:0} min) with {(m.Provider is null ? "no provider" : $"provider {m.Provider}")}.",
                now));
        }

        if (stuck.Count > 0)
        {
            await db.SaveChangesAsync(cancellationToken);
            logger.LogWarning("Raised {Count} stuck-movement exception(s).", stuck.Count);
        }

        return stuck.Count;
    }
}

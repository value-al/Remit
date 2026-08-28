using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;

namespace Remit.BuildingBlocks.Hosting;

/// <summary>
/// The two things every Remit service needs to be a well-behaved pod (ADR-0008):
/// liveness/readiness endpoints Kubernetes can probe, and a <c>--migrate</c> mode so schema
/// changes run as a Job before the new version starts, not on boot.
/// </summary>
public static class ServiceHosting
{
    public const string MigrateArgument = "--migrate";

    public static bool IsMigrateOnly(string[] args) => args.Contains(MigrateArgument, StringComparer.Ordinal);

    /// <summary>Liveness never touches dependencies; readiness includes the database.</summary>
    public static IServiceCollection AddRemitHealth<TContext>(this IServiceCollection services) where TContext : DbContext
    {
        services.AddHealthChecks()
            .AddCheck("self", () => HealthCheckResult.Healthy(), tags: ["live"])
            .AddDbContextCheck<TContext>("database", tags: ["ready"]);
        return services;
    }

    public static IServiceCollection AddRemitHealth(this IServiceCollection services)
    {
        services.AddHealthChecks().AddCheck("self", () => HealthCheckResult.Healthy(), tags: ["live"]);
        return services;
    }

    public static IEndpointRouteBuilder MapRemitHealth(this IEndpointRouteBuilder app)
    {
        app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = r => r.Tags.Contains("live") });
        app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = _ => true });
        return app;
    }

    /// <summary>Apply migrations and exit — the Helm pre-upgrade hook runs the image this way.</summary>
    public static async Task<int> MigrateAndExitAsync<TContext>(this WebApplication app) where TContext : DbContext
    {
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TContext>();
        var pending = (await db.Database.GetPendingMigrationsAsync()).ToList();
        await db.Database.MigrateAsync();
        app.Logger.LogInformation("Applied {Count} migration(s): {Names}", pending.Count, string.Join(", ", pending));
        return 0;
    }
}

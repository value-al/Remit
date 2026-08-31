using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Remit.BuildingBlocks.Hosting;

/// <summary>
/// What a service needs when a browser or a reverse proxy sits in front of it:
///  - CORS for the origins in <c>Cors:Origins</c> (any origin in Development when unset), so the
///    console can call every service;
///  - a per-client fixed-window rate limit when <c>RateLimit:PerMinute</c> is set — the public
///    sandbox has no authentication, so this is its only brake;
///  - forwarded headers, so the rate limiter and logs see the client's address, not the proxy's.
/// Production deployments behind a gateway set none of these and get none of them.
/// </summary>
public static class EdgeHosting
{
    public static IServiceCollection AddRemitEdge(this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        var origins = configuration.GetSection("Cors:Origins").Get<string[]>() ?? [];
        if (origins.Length > 0 || environment.IsDevelopment())
        {
            services.AddCors(o => o.AddDefaultPolicy(p =>
            {
                _ = origins.Length > 0 ? p.WithOrigins(origins) : p.AllowAnyOrigin();
                p.AllowAnyHeader().AllowAnyMethod().WithExposedHeaders("Idempotent-Replayed");
            }));
        }

        var perMinute = configuration.GetValue<int?>("RateLimit:PerMinute");
        if (perMinute is > 0)
        {
            services.AddRateLimiter(o =>
            {
                o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
                o.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(ctx =>
                    RateLimitPartition.GetFixedWindowLimiter(
                        ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                        _ => new FixedWindowRateLimiterOptions { PermitLimit = perMinute.Value, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
            });
        }

        services.Configure<ForwardedHeadersOptions>(o =>
        {
            o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
            // Only the reverse proxy on the same Docker network can reach the service.
            o.KnownIPNetworks.Clear();
            o.KnownProxies.Clear();
        });

        return services;
    }

    public static IApplicationBuilder UseRemitEdge(this WebApplication app)
    {
        app.UseForwardedHeaders();

        if (app.Configuration.GetSection("Cors:Origins").Get<string[]>() is { Length: > 0 } || app.Environment.IsDevelopment())
        {
            app.UseCors();
        }

        if (app.Configuration.GetValue<int?>("RateLimit:PerMinute") is > 0)
        {
            app.UseRateLimiter();
        }

        return app;
    }
}

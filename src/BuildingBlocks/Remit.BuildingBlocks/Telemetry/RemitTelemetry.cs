using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace Remit.BuildingBlocks.Telemetry;

/// <summary>
/// One call per service (ADR-0007): traces for ASP.NET Core, outbound HTTP, Npgsql and every
/// <c>Remit.*</c> ActivitySource; runtime and HTTP metrics; OTLP export when
/// <c>Otel:Endpoint</c> is configured (the compose file's Jaeger listens on 4317), nothing otherwise.
/// </summary>
public static class RemitTelemetry
{
    public static IServiceCollection AddRemitTelemetry(this IServiceCollection services, IConfiguration configuration, string serviceName)
    {
        var endpoint = configuration["Otel:Endpoint"];

        var otel = services.AddOpenTelemetry()
            .ConfigureResource(r => r.AddService(serviceName, serviceNamespace: "remit", serviceVersion: typeof(RemitTelemetry).Assembly.GetName().Version?.ToString()))
            .WithTracing(t =>
            {
                t.AddAspNetCoreInstrumentation(o => o.Filter = ctx => !ctx.Request.Path.StartsWithSegments("/health"))
                 .AddHttpClientInstrumentation()
                 .AddNpgsql()
                 .AddSource("Remit.*");
                if (!string.IsNullOrWhiteSpace(endpoint))
                {
                    t.AddOtlpExporter(o => o.Endpoint = new Uri(endpoint));
                }
            })
            .WithMetrics(m =>
            {
                m.AddAspNetCoreInstrumentation()
                 .AddHttpClientInstrumentation()
                 .AddMeter("Remit.*");
                if (!string.IsNullOrWhiteSpace(endpoint))
                {
                    m.AddOtlpExporter(o => o.Endpoint = new Uri(endpoint));
                }
            });

        _ = otel;
        return services;
    }
}

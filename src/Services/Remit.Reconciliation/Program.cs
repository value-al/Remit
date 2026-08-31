using Microsoft.EntityFrameworkCore;
using Remit.BuildingBlocks.Hosting;
using Remit.BuildingBlocks.Messaging;
using Remit.BuildingBlocks.Telemetry;
using Remit.Reconciliation.Consuming;
using Remit.Reconciliation.Persistence;
using Remit.Reconciliation.Statements;
using Remit.Reconciliation.Sweeps;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddRemitTelemetry(builder.Configuration, "reconciliation");

var connectionString = builder.Configuration.GetConnectionString("Reconciliation")
    ?? throw new InvalidOperationException("ConnectionStrings:Reconciliation is required.");
builder.Services.AddDbContext<ReconciliationDbContext>(o => o.UseNpgsql(connectionString));
builder.Services.AddRemitHealth<ReconciliationDbContext>();

builder.Services.AddSingleton<MovementHandler>();
builder.Services.AddSingleton<IMessageHandler>(sp => sp.GetRequiredService<MovementHandler>());

builder.Services.Configure<RabbitMqOptions>(builder.Configuration.GetSection(RabbitMqOptions.Section));
if (builder.Configuration.GetSection(RabbitMqOptions.Section).Exists())
{
    builder.Services.AddHostedService<RabbitMqConsumer>();
}

builder.Services.Configure<SweepOptions>(builder.Configuration.GetSection(SweepOptions.Section));
builder.Services.AddSingleton<StuckMovementSweep>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<StuckMovementSweep>());

var app = builder.Build();

if (ServiceHosting.IsMigrateOnly(args))
{
    return await app.MigrateAndExitAsync<ReconciliationDbContext>();
}

if (app.Configuration.GetValue("Database:MigrateOnStartup", app.Environment.IsDevelopment()))
{
    using var scope = app.Services.CreateScope();
    await scope.ServiceProvider.GetRequiredService<ReconciliationDbContext>().Database.MigrateAsync();
}

app.MapRemitHealth();
app.MapGet("/", () => Results.Text("Remit Reconciliation — POST /statements/{provider}, GET /exceptions"));
app.MapReconciliation();
app.MapPost("/sweeps/stuck", async (StuckMovementSweep sweep, CancellationToken cancellationToken) =>
    Results.Ok(new { raised = await sweep.SweepOnceAsync(cancellationToken) })).WithTags("Sweeps");

await app.RunAsync();
return 0;

// Marker type for WebApplicationFactory<ReconciliationApp>; the generated Program stays internal.
public sealed class ReconciliationApp;

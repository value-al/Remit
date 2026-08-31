using Microsoft.EntityFrameworkCore;
using Remit.BuildingBlocks.Hosting;
using Remit.BuildingBlocks.Messaging;
using Remit.BuildingBlocks.Telemetry;
using Remit.Ledger.Balances;
using Remit.Ledger.Persistence;
using Remit.Ledger.Consuming;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddRemitTelemetry(builder.Configuration, "ledger");

// The ledger has no in-memory mode: a journal that forgets is not a journal.

// The browser console (Funding's /console, or the copy on value.al) calls all three services
// cross-origin. Any origin is allowed in Development only; production has no console.
if (builder.Environment.IsDevelopment())
{
    builder.Services.AddCors(o => o.AddDefaultPolicy(p => p.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod().WithExposedHeaders("Idempotent-Replayed")));
}

var connectionString = builder.Configuration.GetConnectionString("Ledger")
    ?? throw new InvalidOperationException("ConnectionStrings:Ledger is required.");
builder.Services.AddDbContext<LedgerDbContext>(o => o.UseNpgsql(connectionString));
builder.Services.AddRemitHealth<LedgerDbContext>();

builder.Services.AddSingleton<SettlementHandler>();
builder.Services.AddSingleton<IMessageHandler>(sp => sp.GetRequiredService<SettlementHandler>());

builder.Services.Configure<RabbitMqOptions>(builder.Configuration.GetSection(RabbitMqOptions.Section));
if (builder.Configuration.GetSection(RabbitMqOptions.Section).Exists())
{
    builder.Services.AddHostedService<RabbitMqConsumer>();
}

var app = builder.Build();

if (ServiceHosting.IsMigrateOnly(args))
{
    return await app.MigrateAndExitAsync<LedgerDbContext>();
}

if (app.Configuration.GetValue("Database:MigrateOnStartup", app.Environment.IsDevelopment()))
{
    using var scope = app.Services.CreateScope();
    await scope.ServiceProvider.GetRequiredService<LedgerDbContext>().Database.MigrateAsync();
}

if (app.Environment.IsDevelopment())
{
    app.UseCors();
}

app.MapRemitHealth();
app.MapGet("/", () => Results.Text("Remit Ledger — see /accounts/{id}/balance?currency=EUR and /entries"));
app.MapLedger();

await app.RunAsync();
return 0;

// Marker type for WebApplicationFactory<LedgerApp>; the generated Program stays internal.
public sealed class LedgerApp;

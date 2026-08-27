using Microsoft.EntityFrameworkCore;
using Remit.BuildingBlocks;
using Remit.BuildingBlocks.Idempotency;
using Remit.BuildingBlocks.Outbox;
using Remit.Funding.Deposits;
using Remit.Funding.Messaging;
using Remit.Funding.Persistence;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton(TimeProvider.System);

var connectionString = builder.Configuration.GetConnectionString("Funding");
if (!string.IsNullOrWhiteSpace(connectionString))
{
    // PostgreSQL: deposit + outbox in one transaction, keys in a table, relay to RabbitMQ (ADR-0005).
    builder.Services.AddDbContext<FundingDbContext>(o => o.UseNpgsql(connectionString));
    builder.Services.AddScoped<IDepositRepository, EfDepositRepository>();
    builder.Services.AddScoped<IOutbox, EfOutbox>();
    builder.Services.AddScoped<IUnitOfWork, EfUnitOfWork>();
    builder.Services.AddScoped<IIdempotencyStore, PostgresIdempotencyStore>();

    builder.Services.Configure<OutboxRelayOptions>(builder.Configuration.GetSection(OutboxRelayOptions.Section));
    builder.Services.Configure<RabbitMqOptions>(builder.Configuration.GetSection(RabbitMqOptions.Section));
    if (builder.Configuration.GetSection(RabbitMqOptions.Section).Exists())
    {
        builder.Services.AddSingleton<IMessagePublisher, RabbitMqPublisher>();
    }
    else
    {
        builder.Services.AddSingleton<IMessagePublisher, NullMessagePublisher>();
    }

    builder.Services.AddSingleton<OutboxRelay>();
    builder.Services.AddHostedService(sp => sp.GetRequiredService<OutboxRelay>());
}
else
{
    // No database configured: everything in memory. Good for a first run and for fast tests.
    builder.Services.AddSingleton<IIdempotencyStore, InMemoryIdempotencyStore>();
    builder.Services.AddSingleton<IOutbox, InMemoryOutbox>();
    builder.Services.AddSingleton<IDepositRepository, InMemoryDepositRepository>();
    builder.Services.AddSingleton<IUnitOfWork, NoOpUnitOfWork>();
}

var app = builder.Build();

if (!string.IsNullOrWhiteSpace(connectionString) && app.Configuration.GetValue("Database:MigrateOnStartup", app.Environment.IsDevelopment()))
{
    // Convenient locally and in tests. In a real deployment migrations run as a release step, not on boot.
    using var scope = app.Services.CreateScope();
    await scope.ServiceProvider.GetRequiredService<FundingDbContext>().Database.MigrateAsync();
}

app.UseIdempotency();
app.MapGet("/", () => Results.Text("Remit Funding — see /deposits"));
app.MapDeposits();

await app.RunAsync();

// Lets the integration tests host the app through WebApplicationFactory.
public partial class Program;

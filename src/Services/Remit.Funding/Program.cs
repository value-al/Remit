using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Remit.BuildingBlocks;
using Remit.BuildingBlocks.Hosting;
using Remit.BuildingBlocks.Idempotency;
using Remit.BuildingBlocks.Messaging;
using Remit.BuildingBlocks.Outbox;
using Remit.BuildingBlocks.Telemetry;
using Remit.Funding.Deposits;
using Remit.Funding.Messaging;
using Remit.Funding.Persistence;
using Remit.Funding.Psp;
using Remit.Funding.Withdrawals;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddRemitTelemetry(builder.Configuration, "funding");

var connectionString = builder.Configuration.GetConnectionString("Funding");
if (!string.IsNullOrWhiteSpace(connectionString))
{
    // PostgreSQL: aggregate + outbox in one transaction, keys in a table, relay to RabbitMQ (ADR-0005).
    builder.Services.AddDbContext<FundingDbContext>(o => o.UseNpgsql(connectionString));
    builder.Services.AddScoped<IDepositRepository, EfDepositRepository>();
    builder.Services.AddScoped<IWithdrawalRepository, EfWithdrawalRepository>();
    builder.Services.AddScoped<IOutbox, EfOutbox>();
    builder.Services.AddScoped<IUnitOfWork, EfUnitOfWork>();
    builder.Services.AddScoped<IIdempotencyStore, PostgresIdempotencyStore>();
    builder.Services.AddRemitHealth<FundingDbContext>();

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
    builder.Services.AddSingleton<IWithdrawalRepository, InMemoryWithdrawalRepository>();
    builder.Services.AddSingleton<IUnitOfWork, NoOpUnitOfWork>();
    builder.Services.AddRemitHealth();
}

// Payment providers (ADR-0006). Configured under "Psp:Providers"; two simulators by default.
builder.Services.Configure<PspOptions>(builder.Configuration.GetSection(PspOptions.Section));
builder.Services.PostConfigure<PspOptions>(o =>
{
    if (o.Providers.Count == 0)
    {
        o.Providers["alpha"] = new ProviderOptions { Currencies = ["EUR", "USD", "GBP"], WebhookSecret = "whsec_alpha_dev" };
        o.Providers["beta"] = new ProviderOptions { Currencies = ["EUR", "GBP"], WebhookSecret = "whsec_beta_dev" };
    }
});
builder.Services.AddSingleton<IEnumerable<IPaymentProvider>>(sp =>
    sp.GetRequiredService<IOptions<PspOptions>>().Value.Providers
        .Select(kv => (IPaymentProvider)SimulatedProvider.FromOptions(kv.Key, kv.Value))
        .ToList());
builder.Services.AddSingleton<IProviderHealth>(new InMemoryProviderHealth());
builder.Services.AddSingleton<PspRouter>();
builder.Services.AddSingleton<WebhookVerifiers>();

// Balance check for withdrawals (ADR-0007): the ledger over HTTP when configured, else an in-memory stub.
var ledgerBaseUrl = builder.Configuration["Ledger:BaseUrl"];
if (!string.IsNullOrWhiteSpace(ledgerBaseUrl))
{
    builder.Services.AddHttpClient<ILedgerBalances, HttpLedgerBalances>(c => c.BaseAddress = new Uri(ledgerBaseUrl));
}
else
{
    builder.Services.AddSingleton<InMemoryLedgerBalances>();
    builder.Services.AddSingleton<ILedgerBalances>(sp => sp.GetRequiredService<InMemoryLedgerBalances>());
}

var app = builder.Build();

if (ServiceHosting.IsMigrateOnly(args))
{
    // The Helm pre-upgrade Job runs the image this way (ADR-0008): migrate, report, exit.
    if (string.IsNullOrWhiteSpace(connectionString))
    {
        app.Logger.LogError("--migrate needs ConnectionStrings:Funding; nothing to migrate in the in-memory configuration.");
        return 1;
    }

    return await app.MigrateAndExitAsync<FundingDbContext>();
}

if (!string.IsNullOrWhiteSpace(connectionString) && app.Configuration.GetValue("Database:MigrateOnStartup", app.Environment.IsDevelopment()))
{
    // Convenient locally and in tests. In a real deployment migrations run as a release step, not on boot.
    using var scope = app.Services.CreateScope();
    await scope.ServiceProvider.GetRequiredService<FundingDbContext>().Database.MigrateAsync();
}

app.UseIdempotency();
app.MapRemitHealth();
app.MapGet("/", () => Results.Text("Remit Funding — see /deposits and /withdrawals"));
app.MapDeposits();
app.MapWithdrawals();
app.MapPspWebhooks();

await app.RunAsync();
return 0;

// Marker type for WebApplicationFactory<FundingApp>; the generated Program stays internal.
public sealed class FundingApp;

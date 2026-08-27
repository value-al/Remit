using Remit.BuildingBlocks.Idempotency;
using Remit.BuildingBlocks.Outbox;
using Remit.Funding.Deposits;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<IIdempotencyStore, InMemoryIdempotencyStore>();
builder.Services.AddSingleton<IOutbox, InMemoryOutbox>();
builder.Services.AddSingleton<IDepositRepository, InMemoryDepositRepository>();

var app = builder.Build();

app.UseIdempotency();
app.MapGet("/", () => Results.Text("Remit Funding — see /deposits"));
app.MapDeposits();

app.Run();

// Lets the integration tests host the app through WebApplicationFactory.
public partial class Program;

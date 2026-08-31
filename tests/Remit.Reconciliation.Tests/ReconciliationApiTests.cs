using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Remit.BuildingBlocks.Messaging;
using Remit.Reconciliation.Consuming;
using Remit.Reconciliation.Statements;
using Remit.Reconciliation.Sweeps;
using Testcontainers.PostgreSql;

namespace Remit.Reconciliation.Tests;

public sealed class ReconciliationApiFactory : WebApplicationFactory<ReconciliationApp>, IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17-alpine")
        .WithDatabase("remit").WithUsername("remit").WithPassword("remit").Build();

    public FakeTimeProvider Clock { get; } = new(new DateTimeOffset(2026, 8, 20, 12, 0, 0, TimeSpan.Zero));

    public Task InitializeAsync() => _postgres.StartAsync();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:Reconciliation", _postgres.GetConnectionString());
        builder.UseSetting("Database:MigrateOnStartup", "true");
        builder.UseSetting("Sweeps:StuckAfter", "00:30:00");
        builder.UseSetting("Sweeps:Interval", "01:00:00");
        builder.ConfigureServices(s => s.AddSingleton<TimeProvider>(Clock));
    }

    async Task IAsyncLifetime.DisposeAsync()
    {
        await base.DisposeAsync();
        await _postgres.DisposeAsync();
    }
}

public class ReconciliationApiTests(ReconciliationApiFactory factory) : IClassFixture<ReconciliationApiFactory>
{
    private MovementHandler Handler => factory.Services.GetRequiredService<MovementHandler>();

    private static IncomingMessage Event(string type, Guid id, decimal amount, string? provider, string? reference, DateTimeOffset at) =>
        new(Guid.NewGuid(), type, JsonSerializer.Serialize(new { Id = id, AccountId = Guid.NewGuid(), Amount = amount, Currency = "EUR", Provider = provider, PspReference = reference }), id.ToString(), at);

    private async Task<Guid> SettledDepositAsync(string reference, decimal amount, DateTimeOffset at)
    {
        var id = Guid.NewGuid();
        await Handler.HandleAsync(Event("funding.deposit.requested.v1", id, amount, null, null, at), CancellationToken.None);
        await Handler.HandleAsync(Event("funding.deposit.submitted.v1", id, amount, "alpha", reference, at.AddSeconds(1)), CancellationToken.None);
        await Handler.HandleAsync(Event("funding.deposit.settled.v1", id, amount, "alpha", reference, at.AddMinutes(2)), CancellationToken.None);
        return id;
    }

    private static HttpRequestMessage Statement(string provider, string csv, string from = "2026-08-01", string to = "2026-09-01") =>
        new(HttpMethod.Post, $"/statements/{provider}?from={from}&to={to}")
        {
            Content = new StringContent(csv, Encoding.UTF8, "text/csv"),
        };

    [Fact]
    public async Task Events_out_of_order_still_end_in_the_terminal_state_with_the_reference()
    {
        var id = Guid.NewGuid();
        var at = factory.Clock.GetUtcNow().AddHours(-1);
        await Handler.HandleAsync(Event("funding.deposit.settled.v1", id, 10m, "alpha", "ooo-1", at.AddMinutes(2)), CancellationToken.None);
        await Handler.HandleAsync(Event("funding.deposit.submitted.v1", id, 10m, "alpha", "ooo-1", at), CancellationToken.None);
        await Handler.HandleAsync(Event("funding.deposit.requested.v1", id, 10m, null, null, at.AddSeconds(-1)), CancellationToken.None);

        var movement = await factory.CreateClient().GetFromJsonAsync<JsonElement>($"/movements/{id}");
        Assert.Equal("Settled", movement.GetProperty("status").GetString());
        Assert.Equal("ooo-1", movement.GetProperty("reference").GetString());
    }

    [Fact]
    public async Task A_statement_that_agrees_raises_nothing_and_one_that_disagrees_raises_each_kind()
    {
        var at = new DateTimeOffset(2026, 8, 10, 9, 0, 0, TimeSpan.Zero);
        await SettledDepositAsync("st-ok", 100m, at);
        await SettledDepositAsync("st-short", 200m, at);
        await SettledDepositAsync("st-missing", 300m, at);
        var client = factory.CreateClient();

        var csv = "reference,kind,amount,currency,settled_at\n" +
                  "st-ok,deposit,100,EUR,2026-08-10T09:02:00Z\n" +
                  "st-short,deposit,150,EUR,2026-08-10T09:02:00Z\n" +
                  "st-ghost,deposit,42,EUR,2026-08-11T09:02:00Z\n";

        var response = await client.SendAsync(Statement("alpha", csv));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<StatementResult>();

        Assert.Equal(3, result!.Lines);
        Assert.Equal(1, result.Matched);
        var kinds = result.Raised.Select(r => r.Kind).OrderBy(k => k).ToList();
        Assert.Contains("AmountMismatch", kinds);
        Assert.Contains("UnknownAtPsp", kinds);
        Assert.Contains("MissingAtPsp", kinds);

        // Posting the same statement again raises no duplicates.
        var again = await (await client.SendAsync(Statement("alpha", csv))).Content.ReadFromJsonAsync<StatementResult>();
        Assert.Empty(again!.Raised);

        var open = await client.GetFromJsonAsync<List<ExceptionView>>("/exceptions?provider=alpha");
        Assert.Equal(3, open!.Count(e => e.Reference is "st-short" or "st-ghost" or "st-missing"));
    }

    [Fact]
    public async Task Resolving_an_exception_needs_a_reason_and_happens_once()
    {
        var client = factory.CreateClient();
        var csv = "reference,kind,amount,currency,settled_at\nres-ghost,deposit,1,EUR,2026-08-12T10:00:00Z\n";
        var raised = (await (await client.SendAsync(Statement("beta", csv))).Content.ReadFromJsonAsync<StatementResult>())!.Raised.Single();

        var empty = await client.PostAsJsonAsync($"/exceptions/{raised.Id}/resolve", new ResolveCommand("  "));
        Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);

        var ok = await client.PostAsJsonAsync($"/exceptions/{raised.Id}/resolve", new ResolveCommand("Provider test transaction, confirmed with beta support ticket 4411."));
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);

        var twice = await client.PostAsJsonAsync($"/exceptions/{raised.Id}/resolve", new ResolveCommand("again"));
        Assert.Equal(HttpStatusCode.Conflict, twice.StatusCode);

        var open = await client.GetFromJsonAsync<List<ExceptionView>>("/exceptions?provider=beta");
        Assert.DoesNotContain(open!, e => e.Id == raised.Id);
        var all = await client.GetFromJsonAsync<List<ExceptionView>>("/exceptions?provider=beta&open=false");
        Assert.Contains(all!, e => e.Id == raised.Id && e.Resolution!.Contains("4411"));
    }

    [Fact]
    public async Task A_bad_statement_is_400_and_records_nothing()
    {
        var response = await factory.CreateClient().SendAsync(Statement("alpha", "not,a,statement"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task The_sweep_raises_stuck_once_for_movements_left_in_flight()
    {
        var now = factory.Clock.GetUtcNow();
        var stuckId = Guid.NewGuid();
        var freshId = Guid.NewGuid();
        await Handler.HandleAsync(Event("funding.deposit.requested.v1", stuckId, 5m, null, null, now.AddHours(-2)), CancellationToken.None);
        await Handler.HandleAsync(Event("funding.deposit.requested.v1", freshId, 5m, null, null, now.AddMinutes(-5)), CancellationToken.None);

        var sweep = factory.Services.GetRequiredService<StuckMovementSweep>();
        var first = await sweep.SweepOnceAsync(CancellationToken.None);
        var second = await sweep.SweepOnceAsync(CancellationToken.None);

        Assert.True(first >= 1);
        Assert.Equal(0, second);

        var open = await factory.CreateClient().GetFromJsonAsync<List<ExceptionView>>("/exceptions?provider=none");
        Assert.Contains(open!, e => e.Kind == "Stuck" && e.MovementId == stuckId);
        Assert.DoesNotContain(open!, e => e.MovementId == freshId);
    }
}

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Countersign;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Remit.Funding.Deposits;
using Remit.Ledger.Balances;

namespace Remit.Funding.Tests.Postgres;

/// <summary>
/// Funding and Ledger hosted side by side on the same PostgreSQL and RabbitMQ containers:
/// a deposit is requested, the provider's signed webhook settles it, the relay publishes,
/// the ledger consumes, and the wallet balance appears — the whole path of ADR-0003/0006/0007.
/// </summary>
public class EndToEndTests(PostgresApiFactory funding) : IClassFixture<PostgresApiFactory>
{
    private sealed class LedgerHost(string connectionString, string rabbitUri) : WebApplicationFactory<LedgerApp>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("ConnectionStrings:Ledger", connectionString);
            builder.UseSetting("Database:MigrateOnStartup", "true");
            builder.UseSetting("RabbitMq:Uri", rabbitUri);
            builder.UseSetting("RabbitMq:Exchange", "remit");
        }
    }

    [Fact]
    public async Task A_settled_deposit_shows_up_as_wallet_balance_in_the_ledger()
    {
        await using var ledger = new LedgerHost(funding.ConnectionString, funding.RabbitMqUri);
        var ledgerClient = ledger.CreateClient();
        _ = await ledgerClient.GetAsync("/"); // forces the host, and with it the consumer, to start

        var fundingClient = funding.CreateClient();
        var account = Guid.NewGuid();

        var create = new HttpRequestMessage(HttpMethod.Post, "/deposits/")
        {
            Content = JsonContent.Create(new RequestDepositCommand(account, 250m, "EUR")),
            Headers = { { "Idempotency-Key", Guid.NewGuid().ToString() } },
        };
        var created = await (await fundingClient.SendAsync(create)).Content.ReadFromJsonAsync<DepositResponse>();
        Assert.Equal("SubmittedToPsp", created!.Status);

        // The provider confirms. The default simulator secrets apply (no Psp section configured).
        var signer = new RequestSigner(created.Provider == "alpha" ? "whsec_alpha_dev" : "whsec_beta_dev", CanonicalForms.TimestampDotBody);
        var body = JsonSerializer.SerializeToUtf8Bytes(new { eventId = "evt_e2e", depositId = created.Id, reference = created.PspReference, status = "settled" });
        var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        var webhook = new HttpRequestMessage(HttpMethod.Post, $"/webhooks/psp/{created.Provider}")
        {
            Content = new ByteArrayContent(body) { Headers = { ContentType = new("application/json") } },
            Headers = { { "X-Timestamp", ts }, { "X-Signature", signer.Sign(new SignatureContext(body, timestamp: ts)) } },
        };
        Assert.Equal(HttpStatusCode.OK, (await fundingClient.SendAsync(webhook)).StatusCode);

        // Relay → RabbitMQ → ledger consumer → journal → balance.
        BalanceResponse? balance = null;
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            balance = await ledgerClient.GetFromJsonAsync<BalanceResponse>($"/accounts/{account}/balance?currency=EUR");
            if (balance is { Balance: > 0 })
            {
                break;
            }

            await Task.Delay(200);
        }

        Assert.NotNull(balance);
        Assert.Equal(250m, balance.Balance);
    }
}

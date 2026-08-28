using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Countersign;
using Microsoft.Extensions.DependencyInjection;
using Remit.Funding.Withdrawals;

namespace Remit.Funding.Tests;

public class WithdrawalApiTests(InMemoryApiFactory factory) : IClassFixture<InMemoryApiFactory>
{
    private static readonly RequestSigner AlphaSigner = new(InMemoryApiFactory.AlphaSecret, CanonicalForms.TimestampDotBody);

    private HttpRequestMessage Post(Guid accountId, decimal amount, string currency = "USD") =>
        new(HttpMethod.Post, "/withdrawals/")
        {
            Content = JsonContent.Create(new RequestWithdrawalCommand(accountId, amount, currency, "bank-token-1")),
            Headers = { { "Idempotency-Key", Guid.NewGuid().ToString() } },
        };

    private void GiveBalance(Guid accountId, string currency, decimal amount) =>
        factory.Services.GetRequiredService<InMemoryLedgerBalances>().Set(accountId, currency, amount);

    [Fact]
    public async Task Insufficient_funds_is_422_and_nothing_is_created()
    {
        var client = factory.CreateClient();
        var account = Guid.NewGuid();
        GiveBalance(account, "USD", 10m);

        var response = await client.SendAsync(Post(account, 25m));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Contains("Insufficient funds", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Sufficient_funds_submits_the_payout_to_a_provider()
    {
        var client = factory.CreateClient();
        var account = Guid.NewGuid();
        GiveBalance(account, "USD", 100m);

        var response = await client.SendAsync(Post(account, 25m));

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var withdrawal = await response.Content.ReadFromJsonAsync<WithdrawalResponse>();
        Assert.Equal("SubmittedToPsp", withdrawal!.Status);
        Assert.Equal("alpha", withdrawal.Provider);
        Assert.NotNull(withdrawal.PspReference);
    }

    [Fact]
    public async Task A_missing_destination_is_a_validation_problem()
    {
        var client = factory.CreateClient();
        var account = Guid.NewGuid();
        GiveBalance(account, "USD", 100m);

        var request = new HttpRequestMessage(HttpMethod.Post, "/withdrawals/")
        {
            Content = JsonContent.Create(new RequestWithdrawalCommand(account, 5m, "USD", "")),
            Headers = { { "Idempotency-Key", Guid.NewGuid().ToString() } },
        };

        Assert.Equal(HttpStatusCode.BadRequest, (await client.SendAsync(request)).StatusCode);
    }

    [Fact]
    public async Task A_paid_webhook_completes_the_withdrawal_and_a_second_one_is_acknowledged_only()
    {
        var client = factory.CreateClient();
        var account = Guid.NewGuid();
        GiveBalance(account, "USD", 100m);
        var withdrawal = (await (await client.SendAsync(Post(account, 30m))).Content.ReadFromJsonAsync<WithdrawalResponse>())!;

        var body = JsonSerializer.SerializeToUtf8Bytes(new { eventId = "evt_w1", withdrawalId = withdrawal.Id, reference = withdrawal.PspReference, status = "paid" });
        HttpRequestMessage Webhook()
        {
            var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
            return new HttpRequestMessage(HttpMethod.Post, "/webhooks/psp/alpha")
            {
                Content = new ByteArrayContent(body) { Headers = { ContentType = new("application/json") } },
                Headers = { { "X-Timestamp", ts }, { "X-Signature", AlphaSigner.Sign(new SignatureContext(body, timestamp: ts)) } },
            };
        }

        var first = await client.SendAsync(Webhook());
        var second = await client.SendAsync(Webhook());

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Contains("\"applied\":true", await first.Content.ReadAsStringAsync());
        Assert.Contains("\"applied\":false", await second.Content.ReadAsStringAsync());

        var after = await client.GetFromJsonAsync<WithdrawalResponse>($"/withdrawals/{withdrawal.Id}");
        Assert.Equal("Paid", after!.Status);
    }

    [Fact]
    public async Task A_webhook_naming_both_ids_is_rejected()
    {
        var client = factory.CreateClient();
        var body = JsonSerializer.SerializeToUtf8Bytes(new { eventId = "evt_x", depositId = Guid.NewGuid(), withdrawalId = Guid.NewGuid(), reference = "r", status = "paid" });
        var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        var request = new HttpRequestMessage(HttpMethod.Post, "/webhooks/psp/alpha")
        {
            Content = new ByteArrayContent(body) { Headers = { ContentType = new("application/json") } },
            Headers = { { "X-Timestamp", ts }, { "X-Signature", AlphaSigner.Sign(new SignatureContext(body, timestamp: ts)) } },
        };

        Assert.Equal(HttpStatusCode.BadRequest, (await client.SendAsync(request)).StatusCode);
    }
}

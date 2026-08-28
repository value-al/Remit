using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Countersign;
using Remit.Funding.Deposits;

namespace Remit.Funding.Tests;

/// <summary>
/// Drives the webhook endpoint the way a provider would: sign the exact bytes with the
/// provider's webhook secret using Countersign's <see cref="RequestSigner"/>, the mirror of the
/// <see cref="WebhookVerifier"/> the endpoint uses.
/// </summary>
public class WebhookApiTests(InMemoryApiFactory factory) : IClassFixture<InMemoryApiFactory>
{
    private const string AlphaSecret = "whsec_alpha_test";

    private static readonly RequestSigner AlphaSigner = new(AlphaSecret, CanonicalForms.TimestampDotBody);

    private async Task<DepositResponse> CreateDepositAsync(HttpClient client, string currency = "EUR")
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/deposits/")
        {
            Content = JsonContent.Create(new RequestDepositCommand(Guid.NewGuid(), 40m, currency)),
            Headers = { { "Idempotency-Key", Guid.NewGuid().ToString() } },
        };
        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<DepositResponse>())!;
    }

    private static HttpRequestMessage SignedWebhook(string provider, object payload, RequestSigner signer, long? unixSeconds = null)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(payload);
        var timestamp = (unixSeconds ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds()).ToString();
        var signature = signer.Sign(new SignatureContext(body, timestamp: timestamp));

        return new HttpRequestMessage(HttpMethod.Post, $"/webhooks/psp/{provider}")
        {
            Content = new ByteArrayContent(body) { Headers = { ContentType = new("application/json") } },
            Headers =
            {
                { "X-Timestamp", timestamp },
                { "X-Signature", signature },
            },
        };
    }

    [Fact]
    public async Task A_new_deposit_is_submitted_to_a_provider_synchronously()
    {
        var client = factory.CreateClient();
        var deposit = await CreateDepositAsync(client, "USD");

        Assert.Equal("SubmittedToPsp", deposit.Status);
        Assert.Equal("alpha", deposit.Provider); // beta does not take USD
        Assert.StartsWith("alpha-", deposit.PspReference);
    }

    [Fact]
    public async Task A_correctly_signed_settlement_settles_the_deposit()
    {
        var client = factory.CreateClient();
        var deposit = await CreateDepositAsync(client);

        var response = await client.SendAsync(SignedWebhook(deposit.Provider!, new
        {
            eventId = "evt_1",
            depositId = deposit.Id,
            reference = deposit.PspReference,
            status = "settled",
        }, SignerFor(deposit.Provider!)));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var after = await client.GetFromJsonAsync<DepositResponse>($"/deposits/{deposit.Id}");
        Assert.Equal("Settled", after!.Status);
    }

    [Fact]
    public async Task A_bad_signature_is_rejected_and_changes_nothing()
    {
        var client = factory.CreateClient();
        var deposit = await CreateDepositAsync(client);

        var forged = new RequestSigner("not-the-secret", CanonicalForms.TimestampDotBody);
        var response = await client.SendAsync(SignedWebhook(deposit.Provider!, new
        {
            eventId = "evt_forged",
            depositId = deposit.Id,
            reference = deposit.PspReference,
            status = "settled",
        }, forged));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var after = await client.GetFromJsonAsync<DepositResponse>($"/deposits/{deposit.Id}");
        Assert.Equal("SubmittedToPsp", after!.Status);
    }

    [Fact]
    public async Task A_stale_timestamp_is_rejected_as_a_replay()
    {
        var client = factory.CreateClient();
        var deposit = await CreateDepositAsync(client);

        var tenMinutesAgo = DateTimeOffset.UtcNow.AddMinutes(-10).ToUnixTimeSeconds();
        var response = await client.SendAsync(SignedWebhook(deposit.Provider!, new
        {
            eventId = "evt_old",
            depositId = deposit.Id,
            reference = deposit.PspReference,
            status = "settled",
        }, SignerFor(deposit.Provider!), tenMinutesAgo));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task A_duplicate_settlement_is_acknowledged_but_not_applied_twice()
    {
        var client = factory.CreateClient();
        var deposit = await CreateDepositAsync(client);
        var payload = new { eventId = "evt_dup", depositId = deposit.Id, reference = deposit.PspReference, status = "settled" };

        var first = await client.SendAsync(SignedWebhook(deposit.Provider!, payload, SignerFor(deposit.Provider!)));
        var second = await client.SendAsync(SignedWebhook(deposit.Provider!, payload, SignerFor(deposit.Provider!)));

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Contains("\"applied\":true", await first.Content.ReadAsStringAsync());
        Assert.Contains("\"applied\":false", await second.Content.ReadAsStringAsync());

        var after = await client.GetFromJsonAsync<DepositResponse>($"/deposits/{deposit.Id}");
        Assert.Equal("Settled", after!.Status);
    }

    [Fact]
    public async Task A_valid_signature_from_the_wrong_provider_is_not_applied()
    {
        var client = factory.CreateClient();
        var deposit = await CreateDepositAsync(client, "USD"); // routed to alpha

        // beta's key is genuine, but this deposit is not beta's to settle.
        var response = await client.SendAsync(SignedWebhook("beta", new
        {
            eventId = "evt_wrong",
            depositId = deposit.Id,
            reference = deposit.PspReference,
            status = "settled",
        }, SignerFor("beta")));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("reference-mismatch", await response.Content.ReadAsStringAsync());
        var after = await client.GetFromJsonAsync<DepositResponse>($"/deposits/{deposit.Id}");
        Assert.Equal("SubmittedToPsp", after!.Status);
    }

    [Fact]
    public async Task Unknown_provider_is_404_and_the_webhook_route_needs_no_idempotency_key()
    {
        var client = factory.CreateClient();
        var response = await client.SendAsync(SignedWebhook("nobody", new { eventId = "x" }, AlphaSigner));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task A_failed_webhook_fails_the_deposit_with_the_reason()
    {
        var client = factory.CreateClient();
        var deposit = await CreateDepositAsync(client);

        var response = await client.SendAsync(SignedWebhook(deposit.Provider!, new
        {
            eventId = "evt_fail",
            depositId = deposit.Id,
            reference = deposit.PspReference,
            status = "failed",
            reason = "issuer declined",
        }, SignerFor(deposit.Provider!)));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var after = await client.GetFromJsonAsync<DepositResponse>($"/deposits/{deposit.Id}");
        Assert.Equal("Failed", after!.Status);
        Assert.Equal("issuer declined", after.FailureReason);
    }

    private static RequestSigner SignerFor(string provider) => provider switch
    {
        "alpha" => AlphaSigner,
        "beta" => new RequestSigner(InMemoryApiFactory.BetaSecret, CanonicalForms.TimestampDotBody),
        _ => throw new ArgumentOutOfRangeException(nameof(provider)),
    };
}

using System.Net;
using System.Net.Http.Json;
using Remit.Funding.Deposits;

namespace Remit.Funding.Tests;

public class IdempotentDepositApiTests(InMemoryApiFactory factory) : IClassFixture<InMemoryApiFactory>
{
    private static readonly RequestDepositCommand Command = new(Guid.NewGuid(), 25m, "EUR");

    private static HttpRequestMessage Post(string key, object body) =>
        new(HttpMethod.Post, "/deposits/")
        {
            Content = JsonContent.Create(body),
            Headers = { { "Idempotency-Key", key } },
        };

    [Fact]
    public async Task Post_without_key_is_rejected()
    {
        var client = factory.CreateClient();
        var response = await client.PostAsJsonAsync("/deposits/", Command);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Same_key_same_body_replays_the_first_response()
    {
        var client = factory.CreateClient();
        var key = Guid.NewGuid().ToString();

        var first = await client.SendAsync(Post(key, Command));
        var second = await client.SendAsync(Post(key, Command));

        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, second.StatusCode);
        Assert.True(second.Headers.Contains("Idempotent-Replayed"));

        var a = await first.Content.ReadFromJsonAsync<DepositResponse>();
        var b = await second.Content.ReadFromJsonAsync<DepositResponse>();
        Assert.NotNull(a);
        Assert.Equal(a.Id, b!.Id); // one deposit, not two
    }

    [Fact]
    public async Task Same_key_different_body_is_rejected()
    {
        var client = factory.CreateClient();
        var key = Guid.NewGuid().ToString();

        await client.SendAsync(Post(key, Command));
        var changed = await client.SendAsync(Post(key, Command with { Amount = 26m }));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, changed.StatusCode);
    }

    [Fact]
    public async Task Different_keys_create_different_deposits()
    {
        var client = factory.CreateClient();

        var a = await (await client.SendAsync(Post(Guid.NewGuid().ToString(), Command))).Content.ReadFromJsonAsync<DepositResponse>();
        var b = await (await client.SendAsync(Post(Guid.NewGuid().ToString(), Command))).Content.ReadFromJsonAsync<DepositResponse>();

        Assert.NotEqual(a!.Id, b!.Id);
    }

    [Fact]
    public async Task Invalid_currency_is_a_validation_problem_and_the_key_stays_replayable()
    {
        var client = factory.CreateClient();
        var key = Guid.NewGuid().ToString();
        var bad = Command with { Currency = "EURO" };

        var first = await client.SendAsync(Post(key, bad));
        var second = await client.SendAsync(Post(key, bad));

        Assert.Equal(HttpStatusCode.BadRequest, first.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, second.StatusCode);
        Assert.True(second.Headers.Contains("Idempotent-Replayed"));
    }
}

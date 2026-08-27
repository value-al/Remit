using System.Net;
using System.Net.Http.Json;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RabbitMQ.Client;
using Remit.Funding.Deposits;
using Remit.Funding.Persistence;

namespace Remit.Funding.Tests.Postgres;

public class OutboxRelayTests(PostgresApiFactory factory) : IClassFixture<PostgresApiFactory>
{
    private static HttpRequestMessage Post(string key, object body) =>
        new(HttpMethod.Post, "/deposits/")
        {
            Content = JsonContent.Create(body),
            Headers = { { "Idempotency-Key", key } },
        };

    [Fact]
    public async Task Deposit_and_outbox_row_are_written_together_and_the_message_reaches_the_broker()
    {
        // Subscribe before acting, so nothing is missed.
        var connectionFactory = new ConnectionFactory { Uri = new Uri(factory.RabbitMqUri) };
        await using var connection = await connectionFactory.CreateConnectionAsync();
        await using var channel = await connection.CreateChannelAsync();
        await channel.ExchangeDeclareAsync("remit", ExchangeType.Topic, durable: true, autoDelete: false);
        var queue = (await channel.QueueDeclareAsync(queue: string.Empty, durable: false, exclusive: true, autoDelete: true)).QueueName;
        await channel.QueueBindAsync(queue, "remit", "funding.deposit.#");

        var client = factory.CreateClient();
        var response = await client.SendAsync(Post(Guid.NewGuid().ToString(), new RequestDepositCommand(Guid.NewGuid(), 75m, "EUR")));
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var created = await response.Content.ReadFromJsonAsync<DepositResponse>();
        Assert.NotNull(created);

        // The row and its outbox message exist in the database.
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FundingDbContext>();
            var deposit = await db.Deposits.SingleAsync(d => d.Id == created.Id);
            Assert.Equal(DepositStatus.Requested, deposit.Status);
            Assert.Equal(75m, deposit.Amount.Amount);

            var outbox = await db.Outbox.SingleAsync(o => o.CorrelationId == created.Id.ToString());
            Assert.Equal("funding.deposit.requested.v1", outbox.Type);
        }

        // The relay delivers it. Other tests in this class publish to the same exchange, so
        // read until *our* message arrives rather than trusting the first one on the queue.
        BasicGetResult? delivered = null;
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (delivered is null && DateTime.UtcNow < deadline)
        {
            var next = await channel.BasicGetAsync(queue, autoAck: true);
            if (next is null)
            {
                await Task.Delay(100);
            }
            else if (next.BasicProperties.CorrelationId == created.Id.ToString())
            {
                delivered = next;
            }
        }

        Assert.NotNull(delivered);
        Assert.Equal("funding.deposit.requested.v1", delivered.BasicProperties.Type);
        Assert.Equal(created.Id.ToString(), delivered.BasicProperties.CorrelationId);
        Assert.Contains(created.Id.ToString(), Encoding.UTF8.GetString(delivered.Body.ToArray()));

        // And marks the row sent — after its own transaction commits, which may trail the
        // broker delivery by a few milliseconds, so poll rather than read once.
        OutboxRecord? sent = null;
        deadline = DateTime.UtcNow.AddSeconds(10);
        while (sent is null && DateTime.UtcNow < deadline)
        {
            using var scope = factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<FundingDbContext>();
            sent = await db.Outbox.AsNoTracking().SingleOrDefaultAsync(o => o.CorrelationId == created.Id.ToString() && o.SentAt != null);
            if (sent is null)
            {
                await Task.Delay(100);
            }
        }

        Assert.NotNull(sent);
        Assert.Equal(1, sent.Attempts);
        Assert.Null(sent.LastError);
    }

    [Fact]
    public async Task Idempotency_replay_survives_a_database_round_trip()
    {
        var client = factory.CreateClient();
        var key = Guid.NewGuid().ToString();
        var command = new RequestDepositCommand(Guid.NewGuid(), 10m, "USD");

        var first = await client.SendAsync(Post(key, command));
        var second = await client.SendAsync(Post(key, command));
        var changed = await client.SendAsync(Post(key, command with { Amount = 11m }));

        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, second.StatusCode);
        Assert.True(second.Headers.Contains("Idempotent-Replayed"));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, changed.StatusCode);

        var a = await first.Content.ReadFromJsonAsync<DepositResponse>();
        var b = await second.Content.ReadFromJsonAsync<DepositResponse>();
        Assert.Equal(a!.Id, b!.Id);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FundingDbContext>();
        Assert.Equal(1, await db.Deposits.CountAsync(d => d.Id == a.Id));
    }

    [Fact]
    public async Task Concurrent_claims_on_one_key_admit_exactly_one_request()
    {
        var client = factory.CreateClient();
        var key = Guid.NewGuid().ToString();
        var command = new RequestDepositCommand(Guid.NewGuid(), 5m, "GBP");

        var responses = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => client.SendAsync(Post(key, command))));
        var statuses = responses.Select(r => r.StatusCode).ToList();

        // Every response is either the one accepted request, a replay of it, or a 409 from the race.
        Assert.All(statuses, s => Assert.Contains(s, new[] { HttpStatusCode.Accepted, HttpStatusCode.Conflict }));
        Assert.Contains(HttpStatusCode.Accepted, statuses);

        var ids = new HashSet<Guid>();
        foreach (var r in responses.Where(r => r.StatusCode == HttpStatusCode.Accepted))
        {
            ids.Add((await r.Content.ReadFromJsonAsync<DepositResponse>())!.Id);
        }

        Assert.Single(ids); // one deposit, however many callers
    }
}

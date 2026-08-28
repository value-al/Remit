using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Remit.BuildingBlocks.Messaging;
using Remit.Ledger.Balances;
using Remit.Ledger.Consuming;
using Remit.Ledger.Persistence;

namespace Remit.Ledger.Tests;

public class SettlementHandlerTests(LedgerApiFactory factory) : IClassFixture<LedgerApiFactory>
{
    private SettlementHandler Handler => factory.Services.GetRequiredService<SettlementHandler>();

    private static IncomingMessage Settled(Guid account, decimal amount, string currency, Guid? messageId = null) =>
        new(
            messageId ?? Guid.NewGuid(),
            SettlementHandler.DepositSettled,
            JsonSerializer.Serialize(new { Id = Guid.NewGuid(), AccountId = account, Amount = amount, Currency = currency, Provider = "alpha" }),
            Guid.NewGuid().ToString(),
            DateTimeOffset.UtcNow);

    private static IncomingMessage Paid(Guid account, decimal amount, string currency) =>
        new(
            Guid.NewGuid(),
            SettlementHandler.WithdrawalPaid,
            JsonSerializer.Serialize(new { Id = Guid.NewGuid(), AccountId = account, Amount = amount, Currency = currency, Provider = "beta" }),
            Guid.NewGuid().ToString(),
            DateTimeOffset.UtcNow);

    [Fact]
    public async Task A_settled_deposit_credits_the_wallet_and_debits_the_provider_receivable()
    {
        var account = Guid.NewGuid();
        await Handler.HandleAsync(Settled(account, 120m, "EUR"), CancellationToken.None);

        var client = factory.CreateClient();
        var balance = await client.GetFromJsonAsync<BalanceResponse>($"/accounts/{account}/balance?currency=eur");
        Assert.Equal(120m, balance!.Balance);
        Assert.Equal("EUR", balance.Currency);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LedgerDbContext>();
        var receivable = await db.Postings.Where(p => p.Account == "psp:receivable:alpha" && p.Currency == "EUR").SumAsync(p => p.Side == Side.Debit ? p.Amount : -p.Amount);
        Assert.True(receivable >= 120m);
    }

    [Fact]
    public async Task The_same_message_delivered_twice_posts_once()
    {
        var account = Guid.NewGuid();
        var message = Settled(account, 50m, "USD");

        await Handler.HandleAsync(message, CancellationToken.None);
        await Handler.HandleAsync(message, CancellationToken.None);

        var balance = await factory.CreateClient().GetFromJsonAsync<BalanceResponse>($"/accounts/{account}/balance?currency=USD");
        Assert.Equal(50m, balance!.Balance);
        Assert.Equal(1, balance.Postings); // one entry, one wallet posting — not two
    }

    [Fact]
    public async Task Concurrent_deliveries_of_one_message_post_once()
    {
        var account = Guid.NewGuid();
        var message = Settled(account, 7m, "GBP");

        await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => Handler.HandleAsync(message, CancellationToken.None)));

        var balance = await factory.CreateClient().GetFromJsonAsync<BalanceResponse>($"/accounts/{account}/balance?currency=GBP");
        Assert.Equal(7m, balance!.Balance);
    }

    [Fact]
    public async Task A_paid_withdrawal_debits_the_wallet()
    {
        var account = Guid.NewGuid();
        await Handler.HandleAsync(Settled(account, 100m, "EUR"), CancellationToken.None);
        await Handler.HandleAsync(Paid(account, 40m, "EUR"), CancellationToken.None);

        var balance = await factory.CreateClient().GetFromJsonAsync<BalanceResponse>($"/accounts/{account}/balance?currency=EUR");
        Assert.Equal(60m, balance!.Balance);
    }

    [Fact]
    public async Task Balances_are_per_currency()
    {
        var account = Guid.NewGuid();
        await Handler.HandleAsync(Settled(account, 10m, "EUR"), CancellationToken.None);
        await Handler.HandleAsync(Settled(account, 20m, "USD"), CancellationToken.None);

        var client = factory.CreateClient();
        Assert.Equal(10m, (await client.GetFromJsonAsync<BalanceResponse>($"/accounts/{account}/balance?currency=EUR"))!.Balance);
        Assert.Equal(20m, (await client.GetFromJsonAsync<BalanceResponse>($"/accounts/{account}/balance?currency=USD"))!.Balance);
        Assert.Equal(0m, (await client.GetFromJsonAsync<BalanceResponse>($"/accounts/{account}/balance?currency=CHF"))!.Balance);
    }

    [Fact]
    public async Task Entries_are_queryable_by_correlation_id()
    {
        var account = Guid.NewGuid();
        var message = Settled(account, 3m, "EUR");
        await Handler.HandleAsync(message, CancellationToken.None);

        var entries = await factory.CreateClient().GetFromJsonAsync<List<EntryResponse>>($"/entries?correlationId={message.CorrelationId}");
        var entry = Assert.Single(entries!);
        Assert.Equal(2, entry.Postings.Count);
        Assert.Contains(entry.Postings, p => p.Account == $"client:wallet:{account}" && p.Side == "Credit" && p.Amount == 3m);
    }
}

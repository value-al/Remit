using Microsoft.Extensions.Logging.Abstractions;
using Remit.BuildingBlocks;
using Remit.Funding.Psp;

namespace Remit.Funding.Tests;

public class PspRouterTests
{
    private static PspChargeRequest Request(string currency) =>
        new(Guid.NewGuid(), Guid.NewGuid(), Money.Of(20m, currency), Guid.NewGuid().ToString());

    private static SimulatedProvider Accepting(string name, params string[] currencies) =>
        new(name, currencies, r => new PspChargeResult.Accepted($"{name}-{r.DepositId:N}"));

    private static SimulatedProvider Down(string name, params string[] currencies) =>
        new(name, currencies, _ => new PspChargeResult.Unavailable($"{name} down"));

    private static PspRouter Router(IProviderHealth health, params IPaymentProvider[] providers) =>
        new(providers, health, NullLogger<PspRouter>.Instance);

    [Fact]
    public async Task Only_providers_supporting_the_currency_are_considered()
    {
        var router = Router(new InMemoryProviderHealth(), Accepting("eur-only", "EUR"), Accepting("usd-only", "USD"));

        var outcome = await router.ChargeAsync(Request("USD"), CancellationToken.None);

        Assert.Equal("usd-only", outcome.Provider);
        Assert.Equal(["usd-only"], outcome.Attempted);
    }

    [Fact]
    public async Task No_provider_for_the_currency_is_a_rejection_not_an_outage()
    {
        var router = Router(new InMemoryProviderHealth(), Accepting("eur-only", "EUR"));

        var outcome = await router.ChargeAsync(Request("CHF"), CancellationToken.None);

        Assert.Null(outcome.Provider);
        Assert.IsType<PspChargeResult.Rejected>(outcome.Result);
        Assert.Empty(outcome.Attempted);
    }

    [Fact]
    public async Task Unavailable_falls_through_to_the_next_provider_and_records_the_failure()
    {
        var health = new InMemoryProviderHealth();
        var router = Router(health, Down("a", "EUR"), Accepting("b", "EUR"));

        var outcome = await router.ChargeAsync(Request("EUR"), CancellationToken.None);

        Assert.Equal("b", outcome.Provider);
        Assert.IsType<PspChargeResult.Accepted>(outcome.Result);
        Assert.Equal(["a", "b"], outcome.Attempted);
        Assert.Equal(0.0, health.SuccessRate("a"));
        Assert.Equal(1.0, health.SuccessRate("b"));
    }

    [Fact]
    public async Task Rejected_ends_the_chain_and_is_not_a_health_signal()
    {
        var health = new InMemoryProviderHealth();
        var rejecting = new SimulatedProvider("a", ["EUR"], _ => new PspChargeResult.Rejected("insufficient funds"));
        var router = Router(health, rejecting, Accepting("b", "EUR"));

        var outcome = await router.ChargeAsync(Request("EUR"), CancellationToken.None);

        Assert.Equal("a", outcome.Provider);
        Assert.IsType<PspChargeResult.Rejected>(outcome.Result);
        Assert.Equal(["a"], outcome.Attempted);
        Assert.Equal(1.0, health.SuccessRate("a"));
    }

    [Fact]
    public async Task A_degraded_provider_moves_to_the_back_of_the_chain()
    {
        var health = new InMemoryProviderHealth(windowSize: 10);
        for (var i = 0; i < 10; i++)
        {
            health.Record("a", success: i < 3); // 30% — below the 50% floor
        }

        var router = Router(health, Accepting("a", "EUR"), Accepting("b", "EUR"));

        Assert.Equal(["b", "a"], router.Chain("EUR").Select(p => p.Name));

        var outcome = await router.ChargeAsync(Request("EUR"), CancellationToken.None);
        Assert.Equal("b", outcome.Provider);
    }

    [Fact]
    public async Task A_throwing_adapter_is_treated_as_an_outage()
    {
        var throwing = new SimulatedProvider("a", ["EUR"], _ => throw new HttpRequestException("connection refused"));
        var router = Router(new InMemoryProviderHealth(), throwing, Accepting("b", "EUR"));

        var outcome = await router.ChargeAsync(Request("EUR"), CancellationToken.None);

        Assert.Equal("b", outcome.Provider);
        Assert.Equal(["a", "b"], outcome.Attempted);
    }

    [Fact]
    public async Task All_providers_down_is_an_unavailable_outcome_with_every_attempt_listed()
    {
        var router = Router(new InMemoryProviderHealth(), Down("a", "EUR"), Down("b", "EUR"));

        var outcome = await router.ChargeAsync(Request("EUR"), CancellationToken.None);

        Assert.Null(outcome.Provider);
        Assert.IsType<PspChargeResult.Unavailable>(outcome.Result);
        Assert.Equal(["a", "b"], outcome.Attempted);
    }

    [Fact]
    public void Health_window_is_sliding()
    {
        var health = new InMemoryProviderHealth(windowSize: 4);
        health.Record("a", false);
        health.Record("a", false);
        health.Record("a", false);
        health.Record("a", false);
        Assert.Equal(0.0, health.SuccessRate("a"));

        health.Record("a", true);
        health.Record("a", true);
        Assert.Equal(0.5, health.SuccessRate("a"));

        health.Record("a", true);
        health.Record("a", true);
        Assert.Equal(1.0, health.SuccessRate("a"));
    }
}

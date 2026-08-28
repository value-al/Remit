using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Remit.Funding.Tests;

/// <summary>
/// Hosts Funding with no database configured, so the in-memory stores are used. Fast, and
/// enough for behaviour that does not depend on persistence (the idempotency contract,
/// validation, routing, webhooks). Two simulated providers with known webhook secrets.
/// </summary>
public sealed class InMemoryApiFactory : WebApplicationFactory<FundingApp>
{
    public const string AlphaSecret = "whsec_alpha_test";
    public const string BetaSecret = "whsec_beta_test";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:Funding", string.Empty);

        builder.UseSetting("Psp:Providers:alpha:Currencies:0", "EUR");
        builder.UseSetting("Psp:Providers:alpha:Currencies:1", "USD");
        builder.UseSetting("Psp:Providers:alpha:Currencies:2", "GBP");
        builder.UseSetting("Psp:Providers:alpha:WebhookSecret", AlphaSecret);
        builder.UseSetting("Psp:Providers:beta:Currencies:0", "EUR");
        builder.UseSetting("Psp:Providers:beta:Currencies:1", "GBP");
        builder.UseSetting("Psp:Providers:beta:WebhookSecret", BetaSecret);
    }
}

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Remit.Funding.Tests;

/// <summary>
/// Hosts Funding with no database configured, so the in-memory stores are used. Fast, and
/// enough for behaviour that does not depend on persistence (the idempotency contract,
/// validation, routing).
/// </summary>
public sealed class InMemoryApiFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:Funding", string.Empty);
    }
}

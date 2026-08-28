namespace Remit.Funding.Psp;

public sealed class PspOptions
{
    public const string Section = "Psp";

    public Dictionary<string, ProviderOptions> Providers { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class ProviderOptions
{
    public string[] Currencies { get; set; } = [];

    /// <summary>The secret this provider signs its webhooks with. Distinct from any API secret.</summary>
    public string WebhookSecret { get; set; } = string.Empty;

    /// <summary>Simulator behaviour: <c>accept</c>, <c>reject</c> or <c>unavailable</c>.</summary>
    public string Behaviour { get; set; } = "accept";
}

/// <summary>
/// A provider that never talks to a network. Its behaviour is fixed by configuration (or by the
/// delegate in tests), which is exactly what the router's tests need: deterministic outages.
/// </summary>
public sealed class SimulatedProvider(string name, IEnumerable<string> currencies, Func<PspChargeRequest, PspChargeResult> behaviour) : IPaymentProvider
{
    public string Name { get; } = name;

    public IReadOnlySet<string> Currencies { get; } = currencies.Select(c => c.ToUpperInvariant()).ToHashSet(StringComparer.Ordinal);

    public Task<PspChargeResult> ChargeAsync(PspChargeRequest request, CancellationToken cancellationToken) =>
        Task.FromResult(behaviour(request));

    public static SimulatedProvider FromOptions(string name, ProviderOptions options) =>
        new(name, options.Currencies, options.Behaviour.ToLowerInvariant() switch
        {
            "reject" => _ => new PspChargeResult.Rejected($"{name}: declined by simulator configuration."),
            "unavailable" => _ => new PspChargeResult.Unavailable($"{name}: simulated outage."),
            _ => request => new PspChargeResult.Accepted($"{name}-{request.DepositId:N}"),
        });
}

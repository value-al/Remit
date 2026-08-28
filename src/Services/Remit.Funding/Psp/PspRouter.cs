namespace Remit.Funding.Psp;

public sealed record RoutingOutcome(string? Provider, PspChargeResult Result, IReadOnlyList<string> Attempted);

/// <summary>
/// Chooses a provider per request (ADR-0006): only providers that support the currency,
/// ranked by observed success rate, with providers below <see cref="MinimumSuccessRate"/>
/// demoted to the end of the chain rather than excluded — a degraded provider is still better
/// than no provider. An <see cref="PspChargeResult.Unavailable"/> falls through to the next;
/// an <see cref="PspChargeResult.Accepted"/> or <see cref="PspChargeResult.Rejected"/> ends it.
/// </summary>
public sealed class PspRouter(IEnumerable<IPaymentProvider> providers, IProviderHealth health, ILogger<PspRouter> logger)
{
    public const double MinimumSuccessRate = 0.5;

    private readonly IReadOnlyList<IPaymentProvider> _providers = [.. providers];

    public IReadOnlyList<IPaymentProvider> Chain(string currency) =>
        _providers
            .Where(p => p.Currencies.Contains(currency))
            .Select(p => (Provider: p, Rate: health.SuccessRate(p.Name)))
            .OrderBy(x => x.Rate < MinimumSuccessRate ? 1 : 0)
            .ThenByDescending(x => x.Rate)
            .ThenBy(x => x.Provider.Name, StringComparer.Ordinal)
            .Select(x => x.Provider)
            .ToList();

    public async Task<RoutingOutcome> ChargeAsync(PspChargeRequest request, CancellationToken cancellationToken)
    {
        var attempted = new List<string>();
        var chain = Chain(request.Amount.Currency);

        if (chain.Count == 0)
        {
            return new RoutingOutcome(null, new PspChargeResult.Rejected($"No provider supports {request.Amount.Currency}."), attempted);
        }

        PspChargeResult last = new PspChargeResult.Unavailable("No provider attempted.");
        foreach (var provider in chain)
        {
            attempted.Add(provider.Name);
            try
            {
                last = await provider.ChargeAsync(request, cancellationToken);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                // A throwing adapter is an outage, not a decision.
                last = new PspChargeResult.Unavailable(e.Message);
            }

            switch (last)
            {
                case PspChargeResult.Accepted:
                    health.Record(provider.Name, success: true);
                    return new RoutingOutcome(provider.Name, last, attempted);

                case PspChargeResult.Rejected:
                    // The provider answered; its answer stands. Not a health signal.
                    return new RoutingOutcome(provider.Name, last, attempted);

                case PspChargeResult.Unavailable unavailable:
                    health.Record(provider.Name, success: false);
                    logger.LogWarning("Provider {Provider} unavailable for deposit {DepositId}: {Error}. Falling through.", provider.Name, request.DepositId, unavailable.Error);
                    continue;
            }
        }

        return new RoutingOutcome(null, last, attempted);
    }
}

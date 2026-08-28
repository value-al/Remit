using Remit.BuildingBlocks;

namespace Remit.Funding.Psp;

/// <summary>What Funding asks a provider to do. The provider never sees card data — only references.</summary>
public sealed record PspChargeRequest(Guid DepositId, Guid AccountId, Money Amount, string IdempotencyKey);

/// <summary>
/// The three outcomes a provider call can have, kept distinct because they route differently:
/// a rejection is final for this provider, an outage is a reason to try the next one.
/// </summary>
public abstract record PspChargeResult
{
    private PspChargeResult()
    {
    }

    /// <summary>The provider accepted the charge and will confirm asynchronously.</summary>
    public sealed record Accepted(string Reference) : PspChargeResult;

    /// <summary>The provider declined this charge. Do not retry elsewhere without a human decision.</summary>
    public sealed record Rejected(string Reason) : PspChargeResult;

    /// <summary>The provider could not be reached or errored. Safe to try the next provider.</summary>
    public sealed record Unavailable(string Error) : PspChargeResult;
}

/// <summary>The boundary every payment provider is behind (ADR-0006).</summary>
public interface IPaymentProvider
{
    string Name { get; }

    IReadOnlySet<string> Currencies { get; }

    Task<PspChargeResult> ChargeAsync(PspChargeRequest request, CancellationToken cancellationToken);
}

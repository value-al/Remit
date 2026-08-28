using System.Collections.Concurrent;
using System.Net.Http.Json;
using Remit.BuildingBlocks;

namespace Remit.Funding.Withdrawals;

/// <summary>
/// What Funding asks the ledger before paying out. Advisory (ADR-0007): the ledger does not
/// place a hold, so two withdrawals racing on one balance can both pass the check. That is
/// accepted for now and is what reconciliation (week 8) exists to catch; a hold/reservation
/// entry is the planned fix.
/// </summary>
public interface ILedgerBalances
{
    Task<Money> AvailableAsync(Guid accountId, string currency, CancellationToken cancellationToken);
}

/// <summary>For the in-memory configuration and tests: balances set explicitly, default zero.</summary>
public sealed class InMemoryLedgerBalances : ILedgerBalances
{
    private readonly ConcurrentDictionary<(Guid, string), decimal> _balances = new();

    public void Set(Guid accountId, string currency, decimal amount) => _balances[(accountId, currency.ToUpperInvariant())] = amount;

    public Task<Money> AvailableAsync(Guid accountId, string currency, CancellationToken cancellationToken)
    {
        var upper = currency.ToUpperInvariant();
        return Task.FromResult(Money.Of(_balances.GetValueOrDefault((accountId, upper)), upper));
    }
}

public sealed class HttpLedgerBalances(HttpClient http) : ILedgerBalances
{
    private sealed record BalanceDto(Guid AccountId, string Currency, decimal Balance);

    public async Task<Money> AvailableAsync(Guid accountId, string currency, CancellationToken cancellationToken)
    {
        var dto = await http.GetFromJsonAsync<BalanceDto>($"/accounts/{accountId}/balance?currency={Uri.EscapeDataString(currency)}", cancellationToken)
            ?? throw new InvalidOperationException("Ledger returned no balance.");
        return Money.Of(dto.Balance, dto.Currency);
    }
}

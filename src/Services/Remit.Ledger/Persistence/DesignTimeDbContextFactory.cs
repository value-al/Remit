using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Remit.Ledger.Persistence;

/// <summary>Used only by <c>dotnet ef migrations add</c>; the connection is never opened at design time.</summary>
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<LedgerDbContext>
{
    public LedgerDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<LedgerDbContext>()
            .UseNpgsql("Host=localhost;Database=remit;Username=remit;Password=remit")
            .Options;
        return new LedgerDbContext(options);
    }
}

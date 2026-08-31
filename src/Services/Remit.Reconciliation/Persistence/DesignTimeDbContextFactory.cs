using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Remit.Reconciliation.Persistence;

/// <summary>Used only by <c>dotnet ef migrations add</c>; the connection is never opened at design time.</summary>
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<ReconciliationDbContext>
{
    public ReconciliationDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<ReconciliationDbContext>()
            .UseNpgsql("Host=localhost;Database=remit;Username=remit;Password=remit")
            .Options);
}

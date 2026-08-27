using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Remit.Funding.Persistence;

/// <summary>
/// Used only by <c>dotnet ef migrations add</c>. The connection string is never opened at
/// design time; Npgsql just needs one to build the model.
/// </summary>
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<FundingDbContext>
{
    public FundingDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<FundingDbContext>()
            .UseNpgsql("Host=localhost;Database=remit;Username=remit;Password=remit")
            .Options;
        return new FundingDbContext(options);
    }
}

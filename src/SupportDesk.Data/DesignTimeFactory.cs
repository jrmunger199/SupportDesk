using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

using SupportDesk.Domain;

namespace SupportDesk.Data;

/// <summary>Lets <c>dotnet ef</c> create the context without starting the web host.</summary>
internal sealed class DesignTimeFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlServer(DatabaseDefaults.LocalDbConnectionString("SupportDesk"))
            .Options;

        return new AppDbContext(options, new NoTenant(), TimeProvider.System);
    }

    private sealed class NoTenant : ITenantContext
    {
        public Guid? TenantId => null;
    }
}

public static class DatabaseDefaults
{
    /// <summary>Connection string for the SQL Server LocalDB instance that ships with Visual Studio.</summary>
    public static string LocalDbConnectionString(string database) =>
        $"Server=(localdb)\\MSSQLLocalDB;Database={database};Trusted_Connection=True;TrustServerCertificate=True;MultipleActiveResultSets=false";
}

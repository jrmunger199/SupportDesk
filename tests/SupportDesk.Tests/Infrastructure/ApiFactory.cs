using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.SqlClient;

using SupportDesk.Data;

namespace SupportDesk.Tests.Infrastructure;

/// <summary>
/// Boots the real API in-process against a throwaway SQL Server database. Locally that is LocalDB; in CI set
/// SUPPORTDESK_TEST_SQL to a server connection string (the database name is replaced with a unique one).
/// </summary>
public sealed class ApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly string _database = $"SupportDesk_Test_{Guid.NewGuid():N}";
    private readonly string _serverConnection =
        Environment.GetEnvironmentVariable("SUPPORTDESK_TEST_SQL")
        ?? DatabaseDefaults.LocalDbConnectionString("master");

    public string ConnectionString =>
        new SqlConnectionStringBuilder(_serverConnection) { InitialCatalog = _database }.ConnectionString;

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:Default", ConnectionString);
        builder.UseSetting("Jwt:Key", "integration-test-signing-key-0123456789-abcdef");
        builder.UseSetting("Database:MigrateOnStartup", "true");
        builder.UseSetting("Seed:DemoData", "false");
        builder.UseSetting("Throttling:AuthPermitLimit", "100000");
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        await DropDatabaseAsync();
        GC.SuppressFinalize(this);
    }

    private async Task DropDatabaseAsync()
    {
        await using var connection = new SqlConnection(new SqlConnectionStringBuilder(_serverConnection) { InitialCatalog = "master" }.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            $"IF DB_ID(N'{_database}') IS NOT NULL BEGIN ALTER DATABASE [{_database}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{_database}]; END";
        await command.ExecuteNonQueryAsync();
    }
}

[CollectionDefinition(Name)]
public sealed class ApiTestGroup : ICollectionFixture<ApiFactory>
{
    public const string Name = "api";
}

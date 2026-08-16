using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Configuration;
using WordOs.Infrastructure.Persistence;

namespace WordOs.Api;

/// <summary>
/// Used only by <c>dotnet ef</c> at design time.
/// </summary>
/// <remarks>
/// Migrations connect as <c>wordos_migrator</c>, which owns the schema. The
/// running application connects as <c>wordos_app</c>, which has no DDL rights
/// at all (docs/07-SECURITY.md §10) — so a SQL-injection bug in the API could
/// not drop a table even if one existed.
///
/// Both connection strings come from user-secrets or the environment; neither
/// is ever committed.
/// </remarks>
public sealed class WordOsDbContextFactory
    : IDesignTimeDbContextFactory<WordOsDbContext>
{
    public WordOsDbContext CreateDbContext(string[] args)
    {
        var configuration = new ConfigurationBuilder()
            .AddUserSecrets<WordOsDbContextFactory>(optional: true)
            .AddEnvironmentVariables()
            .Build();

        var connectionString =
            configuration.GetConnectionString("WordOsMigrations")
            ?? configuration.GetConnectionString("WordOs")
            ?? throw new InvalidOperationException(
                "ConnectionStrings:WordOsMigrations is not configured. " +
                "Migrations run as the schema owner; set it with " +
                "`dotnet user-secrets set`.");

        var options = new DbContextOptionsBuilder<WordOsDbContext>()
            .UseNpgsql(connectionString)
            .Options;

        return new WordOsDbContext(options);
    }
}

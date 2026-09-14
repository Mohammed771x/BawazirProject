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

        // Say which database is about to be changed, out loud.
        //
        // `WordOsMigrations` is checked before `WordOs` and user-secrets are
        // real configuration, so on a developer's machine this key is already
        // set — to their local database. Passing a different connection string
        // on the command line as `ConnectionStrings__WordOs` therefore changes
        // nothing at all: the local secret still wins, `dotnet ef` migrates
        // `wordos_dev`, and reports "Done."
        //
        // That happened, against production: the deployment guide named the
        // wrong variable, the command succeeded, said "No migrations were
        // applied. The database is already up to date" — which was true of the
        // local database — and the live one never got the table. The first
        // symptom was a 500 from an endpoint that had just been deployed.
        //
        // Nothing here can stop someone passing the wrong string. One line
        // naming the host makes it impossible not to notice. The password is
        // never part of it (docs/07-SECURITY.md §9).
        var target = new Npgsql.NpgsqlConnectionStringBuilder(connectionString);
        Console.WriteLine(
            $"Migrating {target.Host}/{target.Database} as {target.Username}");

        var options = new DbContextOptionsBuilder<WordOsDbContext>()
            .UseNpgsql(connectionString)
            .Options;

        return new WordOsDbContext(options);
    }
}

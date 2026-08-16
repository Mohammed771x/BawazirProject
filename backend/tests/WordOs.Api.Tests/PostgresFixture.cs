using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using WordOs.Infrastructure.Persistence;

namespace WordOs.Api.Tests;

/// <summary>
/// A real PostgreSQL database for integration tests.
/// </summary>
/// <remarks>
/// Deliberately <b>not</b> the EF in-memory provider: it does not enforce
/// unique indexes, check constraints, foreign keys or column lengths, so a test
/// suite built on it would pass while the production schema rejects the same
/// data. Everything worth testing here is exactly what the in-memory provider
/// does not model.
///
/// Each run creates a throwaway database from the migrations and drops it
/// afterwards, so tests never depend on — or corrupt — development data.
///
/// This needs <c>CREATEDB</c> on the migration role, which is granted on the
/// local development machine only. The production migration role applies
/// migrations to a database that already exists and must not have it.
///
/// The credentials come from user-secrets or the environment, never from
/// source. If they are absent the tests are skipped rather than silently
/// passing (see <see cref="IsAvailable"/>).
/// </remarks>
public sealed class PostgresFixture : IAsyncLifetime
{
    private readonly string _databaseName =
        $"wordos_test_{Guid.NewGuid():N}"[..24];

    private string? _adminConnectionString;

    public string ConnectionString { get; private set; } = string.Empty;

    /// <summary>False when no migration credentials are configured.</summary>
    public bool IsAvailable { get; private set; }

    public string? SkipReason { get; private set; }

    public async Task InitializeAsync()
    {
        var configuration = new ConfigurationBuilder()
            .AddUserSecrets<PostgresFixture>(optional: true)
            .AddEnvironmentVariables()
            .Build();

        _adminConnectionString =
            configuration.GetConnectionString("WordOsMigrations")
            ?? Environment.GetEnvironmentVariable("WORDOS_MIGRATIONS_CONNECTION");

        if (string.IsNullOrWhiteSpace(_adminConnectionString))
        {
            SkipReason =
                "No migration connection string configured. Set " +
                "ConnectionStrings:WordOsMigrations in user-secrets or the " +
                "WORDOS_MIGRATIONS_CONNECTION environment variable.";
            return;
        }

        var builder =
            new Npgsql.NpgsqlConnectionStringBuilder(_adminConnectionString);
        var maintenance =
            new Npgsql.NpgsqlConnectionStringBuilder(_adminConnectionString)
            {
                Database = "postgres",
            };

        await using (var connection =
                     new Npgsql.NpgsqlConnection(maintenance.ConnectionString))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            // The database name is generated here, never taken from input.
            command.CommandText = $"CREATE DATABASE \"{_databaseName}\";";
            await command.ExecuteNonQueryAsync();
        }

        builder.Database = _databaseName;
        ConnectionString = builder.ConnectionString;

        // The schema is built by the migrations — the same ones that run in
        // production. A schema created any other way would not be under test.
        await using var db = CreateContext();
        await db.Database.MigrateAsync();

        IsAvailable = true;
    }

    public WordOsDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<WordOsDbContext>()
            .UseNpgsql(ConnectionString)
            .Options;
        return new WordOsDbContext(options);
    }

    public async Task DisposeAsync()
    {
        if (!IsAvailable || _adminConnectionString is null) return;

        var maintenance =
            new Npgsql.NpgsqlConnectionStringBuilder(_adminConnectionString)
            {
                Database = "postgres",
            };

        Npgsql.NpgsqlConnection.ClearAllPools();

        await using var connection =
            new Npgsql.NpgsqlConnection(maintenance.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            $"DROP DATABASE IF EXISTS \"{_databaseName}\" WITH (FORCE);";
        await command.ExecuteNonQueryAsync();
    }
}

[CollectionDefinition(Name)]
public sealed class PostgresCollection : ICollectionFixture<PostgresFixture>
{
    public const string Name = "postgres";
}

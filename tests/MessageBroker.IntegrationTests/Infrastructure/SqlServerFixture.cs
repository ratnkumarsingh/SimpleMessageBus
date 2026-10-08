using MessageBroker.Infrastructure.Data;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging.Abstractions;
using Respawn;
using Samples.Shared;

namespace MessageBroker.IntegrationTests.Infrastructure;

/// <summary>
/// One throwaway database on a local SQL Server for the whole test run. The server comes from the
/// BROKER_TEST_SQL environment variable (default: the local default instance with Windows auth).
/// The schema is deployed once with the real DbUp deployer (plus the samples' schema); <see cref="ResetAsync"/> clears data
/// between tests with Respawn, and the database is dropped when the run ends.
/// </summary>
public sealed class SqlServerFixture : IAsyncLifetime
{
    private const string DefaultServer = "Server=localhost;Integrated Security=true;TrustServerCertificate=true";

    /// <summary>The test server, without a database.</summary>
    public string ServerConnectionString { get; } =
        Environment.GetEnvironmentVariable("BROKER_TEST_SQL") is { Length: > 0 } configured ? configured : DefaultServer;

    private readonly string _databaseName = $"BrokerDb_Test_{Guid.NewGuid():N}"[..28];

    private Respawner? _respawner;
    private ApiFactory? _api;

    public string ConnectionString { get; private set; } = "";

    /// <summary>The API host, started on first use and shared by every API test in the run.</summary>
    public ApiFactory Api => _api ??= new ApiFactory(ConnectionString);

    public async Task InitializeAsync()
    {
        ConnectionString = ConnectionStringFor(_databaseName);

        new SchemaDeployer(ConnectionString, NullLogger.Instance).Deploy(createDatabase: true);
        // The sample applications' tables live in the same database under the "sample" schema.
        await new SampleDatabase(ConnectionString).DeployAsync();

        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();
        _respawner = await Respawner.CreateAsync(connection, new RespawnerOptions
        {
            SchemasToInclude = ["broker", "sample"],
            TablesToIgnore = [new Respawn.Graph.Table("broker", "SchemaVersions")],
        });
    }

    public async Task ResetAsync()
    {
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();
        await _respawner!.ResetAsync(connection);
    }

    public async Task DisposeAsync()
    {
        if (_api is not null)
            await _api.DisposeAsync();
        await DropDatabaseAsync(_databaseName);
    }

    /// <summary>Connection string for <paramref name="databaseName"/> on the test server.</summary>
    public string ConnectionStringFor(string databaseName) =>
        new SqlConnectionStringBuilder(ServerConnectionString) { InitialCatalog = databaseName }.ConnectionString;

    public async Task DropDatabaseAsync(string databaseName)
    {
        SqlConnection.ClearAllPools();
        await using var connection = new SqlConnection(ConnectionStringFor("master"));
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            IF DB_ID('{databaseName}') IS NOT NULL
            BEGIN
                ALTER DATABASE [{databaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
                DROP DATABASE [{databaseName}];
            END
            """;
        await command.ExecuteNonQueryAsync();
    }
}

[CollectionDefinition(Name)]
public sealed class SqlCollection : ICollectionFixture<SqlServerFixture>
{
    public const string Name = "sql";
}

using System.Data;
using System.Text.RegularExpressions;
using MessageBroker.Infrastructure.Data;
using MessageBroker.IntegrationTests.Infrastructure;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging.Abstractions;

namespace MessageBroker.IntegrationTests.Database;

/// <summary>
/// D29 — The DBA release script (db/build-release-script.ps1) builds the same schema as the DbUp
/// deploy, can be applied repeatedly, and leaves a journal DbUp accepts. Each test uses its own
/// scratch database next to the shared one.
/// </summary>
[Collection(SqlCollection.Name)]
public sealed partial class ReleaseScriptTests(SqlServerFixture sql) : IAsyncLifetime
{
    private readonly string _databaseName = $"BrokerDb_Rel_{Guid.NewGuid():N}"[..27];
    private string ConnectionString => sql.ConnectionStringFor(_databaseName);

    public async Task InitializeAsync()
    {
        await SqlHelper.ExecuteNonQueryAsync(sql.ConnectionStringFor("master"), CommandType.Text,
            $"CREATE DATABASE [{_databaseName}]", CancellationToken.None);
    }

    public Task DisposeAsync() => sql.DropDatabaseAsync(_databaseName);

    [Fact(DisplayName = "D29a Release script on an empty database matches DbUp, and DbUp then runs no migration")]
    public async Task D29a_EmptyDatabase()
    {
        await ApplyReleaseScriptAsync();
        var journal = await JournalAsync();

        new SchemaDeployer(ConnectionString, NullLogger.Instance).Deploy();

        Assert.Equal(SchemaDeployer.ReadAllScripts().Count(s => SchemaDeployer.IsMigration(s.Name)), journal.Count);
        Assert.Equal(journal, await JournalAsync());
        Assert.Equal(await BrokerObjectsAsync(sql.ConnectionString), await BrokerObjectsAsync(ConnectionString));
    }

    [Fact(DisplayName = "D29b Release script applied twice, or over a DbUp deploy, changes nothing")]
    public async Task D29b_Reapply()
    {
        new SchemaDeployer(ConnectionString, NullLogger.Instance).Deploy();
        var journal = await JournalAsync();

        await ApplyReleaseScriptAsync();
        await ApplyReleaseScriptAsync();

        Assert.Equal(journal, await JournalAsync());
        Assert.Equal(await BrokerObjectsAsync(sql.ConnectionString), await BrokerObjectsAsync(ConnectionString));
    }

    [Fact(DisplayName = "D29c A failing migration leaves no journal row and no partial tables")]
    public async Task D29c_FailedMigrationRollsBack()
    {
        // Break the second migration so its batch fails after the first has been applied.
        var script = ReleaseScript.Build();
        var second = SchemaDeployer.ReadAllScripts().Where(s => SchemaDeployer.IsMigration(s.Name)).ElementAt(1).Name;
        var marker = $"BEGIN TRANSACTION;\r\nGO\r\n".ReplaceLineEndings();
        var at = script.IndexOf(marker, script.IndexOf($"-- ===== {second}", StringComparison.Ordinal), StringComparison.Ordinal);
        script = script.Insert(at + marker.Length, "CREATE TABLE broker.ReleaseProbe (Id int);\nGO\nSELECT 1/0;\nGO\n");

        await Assert.ThrowsAsync<SqlException>(() => ApplyAsync(script));

        var journal = await JournalAsync();
        Assert.DoesNotContain(second, journal);
        Assert.Single(journal);
        Assert.Null(await SqlHelper.ExecuteScalarAsync(ConnectionString, CommandType.Text,
            "SELECT OBJECT_ID(N'broker.ReleaseProbe')", CancellationToken.None));
    }

    private Task ApplyReleaseScriptAsync() => ApplyAsync(ReleaseScript.Build());

    /// <summary>Runs the script the way "sqlcmd -b" does: batch by batch on one session, stopping at the first error.</summary>
    private async Task ApplyAsync(string script)
    {
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();
        await SqlHelper.ExecuteNonQueryAsync(connection, CommandType.Text, "SET QUOTED_IDENTIFIER OFF;", CancellationToken.None); // sqlcmd's default
        foreach (var batch in GoSeparator().Split(script).Where(b => !string.IsNullOrWhiteSpace(b)))
            await SqlHelper.ExecuteNonQueryAsync(connection, CommandType.Text, batch, commandTimeout: 120, CancellationToken.None);
    }

    private Task<List<string>> JournalAsync() =>
        ColumnAsync(ConnectionString, "SELECT ScriptName FROM broker.SchemaVersions ORDER BY Id");

    private static async Task<List<string>> ColumnAsync(string connectionString, string sql)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var reader = await SqlHelper.ExecuteReaderAsync(connection, CommandType.Text, sql, CancellationToken.None);
        return [.. await SqlHelper.ReadAllAsync(reader, r => r.GetString(0), CancellationToken.None)];
    }

    /// <summary>Every object in the broker schema with its type, plus each column's definition.</summary>
    private static Task<List<string>> BrokerObjectsAsync(string connectionString) =>
        ColumnAsync(connectionString, """
            SELECT o.type COLLATE DATABASE_DEFAULT + ' ' + o.name COLLATE DATABASE_DEFAULT
            FROM sys.objects o
            WHERE o.schema_id = SCHEMA_ID(N'broker') AND o.is_ms_shipped = 0
            UNION ALL
            SELECT 'col ' + OBJECT_NAME(c.object_id) COLLATE DATABASE_DEFAULT + '.' + c.name COLLATE DATABASE_DEFAULT
                   + ' ' + TYPE_NAME(c.user_type_id) COLLATE DATABASE_DEFAULT
                   + ' ' + CAST(c.max_length AS varchar(10)) + ' ' + CAST(c.is_nullable AS varchar(1))
            FROM sys.columns c JOIN sys.tables t ON t.object_id = c.object_id
            WHERE t.schema_id = SCHEMA_ID(N'broker')
            UNION ALL
            SELECT 'ix ' + OBJECT_NAME(i.object_id) COLLATE DATABASE_DEFAULT + '.' + i.name COLLATE DATABASE_DEFAULT
                   + ' ' + ISNULL(i.filter_definition COLLATE DATABASE_DEFAULT, '')
            FROM sys.indexes i JOIN sys.tables t ON t.object_id = i.object_id
            WHERE t.schema_id = SCHEMA_ID(N'broker') AND i.name IS NOT NULL
            ORDER BY 1
            """);

    [GeneratedRegex(@"^\s*GO\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase)]
    private static partial Regex GoSeparator();
}

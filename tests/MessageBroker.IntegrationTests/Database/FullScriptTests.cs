using System.Data;
using System.Text.RegularExpressions;
using MessageBroker.Application.Security;
using MessageBroker.Infrastructure.Data;
using MessageBroker.IntegrationTests.Infrastructure;
using Microsoft.Data.SqlClient;

namespace MessageBroker.IntegrationTests.Database;

/// <summary>
/// D41 — The full install script (db/BrokerDb_Full.sql) creates the database, deploys the schema and adds
/// an Admin key the broker accepts and the webhook allowlist. Running it again adds nothing. Each test
/// lets the script create its own scratch database.
/// </summary>
[Collection(SqlCollection.Name)]
public sealed partial class FullScriptTests(SqlServerFixture sql) : IAsyncLifetime
{
    private readonly string _databaseName = $"BrokerDb_Full_{Guid.NewGuid():N}"[..28];
    private string ConnectionString => sql.ConnectionStringFor(_databaseName);

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => sql.DropDatabaseAsync(_databaseName);

    [Fact(DisplayName = "D41a Full script on a new server creates the database, schema, Admin key and allowlist")]
    public async Task D41a_NewDatabase()
    {
        var key = ApiKeys.Generate();

        await ApplyAsync(adminName: "", key.Key, " Hooks.Example.com, 10.0.0.5 ,");

        var admin = await RowsAsync("""
            SELECT a.Name COLLATE DATABASE_DEFAULT + '|' + CAST(a.IsAdmin AS varchar(1)) + '|' + k.Prefix + '|' + CONVERT(varchar(64), k.Hash, 2)
            FROM broker.ApiKeys k JOIN broker.Applications a ON a.AppId = k.AppId
            """);
        Assert.Equal([$"broker-admin|1|{key.Prefix}|{Convert.ToHexString(key.Hash)}"], admin);
        Assert.Equal(["10.0.0.5", "hooks.example.com"], await RowsAsync("SELECT Host FROM broker.WebhookAllowedHosts ORDER BY Host"));
        Assert.Equal(SchemaDeployer.ReadAllScripts().Count(s => SchemaDeployer.IsMigration(s.Name)),
            (await RowsAsync("SELECT ScriptName FROM broker.SchemaVersions")).Count);
    }

    [Fact(DisplayName = "D41b Full script applied again, or with no key and no hosts, adds nothing")]
    public async Task D41b_Reapply()
    {
        var key = ApiKeys.Generate();
        await ApplyAsync("ops-admin", key.Key, "hooks.example.com");

        await ApplyAsync("ops-admin", key.Key, "hooks.example.com");
        await ApplyAsync("ops-admin", adminApiKey: "", webhookAllowedHosts: "");

        Assert.Equal(["ops-admin"], await RowsAsync("SELECT Name FROM broker.Applications"));
        Assert.Single(await RowsAsync("SELECT Prefix FROM broker.ApiKeys"));
        Assert.Equal(["hooks.example.com"], await RowsAsync("SELECT Host FROM broker.WebhookAllowedHosts"));
    }

    [Theory(DisplayName = "D41c Full script rejects a malformed key or host and adds no data")]
    [InlineData("notakey", "")]
    [InlineData("", "https://hooks.example.com/in")]
    public async Task D41c_RejectsBadInput(string adminApiKey, string webhookAllowedHosts)
    {
        await Assert.ThrowsAsync<SqlException>(() => ApplyAsync("", adminApiKey, webhookAllowedHosts));

        Assert.Empty(await RowsAsync("SELECT Name FROM broker.Applications"));
        Assert.Empty(await RowsAsync("SELECT Host FROM broker.WebhookAllowedHosts"));
    }

    /// <summary>
    /// Runs the script the way "sqlcmd -b -v ..." does: variables substituted, batch by batch on one
    /// session that starts in master, stopping at the first error.
    /// </summary>
    private async Task ApplyAsync(string adminName, string adminApiKey, string webhookAllowedHosts)
    {
        var values = new Dictionary<string, string>
        {
            ["DatabaseName"] = _databaseName,
            ["AdminName"] = adminName,
            ["AdminApiKey"] = adminApiKey,
            ["WebhookAllowedHosts"] = webhookAllowedHosts,
        };
        Assert.Equal(ReleaseScript.FullScriptVariables.Order(), values.Keys.Order());
        var script = Variable().Replace(ReleaseScript.BuildFull(), m => values[m.Groups[1].Value]);

        await using var connection = new SqlConnection(sql.ConnectionStringFor("master"));
        await connection.OpenAsync();
        await SqlHelper.ExecuteNonQueryAsync(connection, CommandType.Text, "SET QUOTED_IDENTIFIER OFF;", CancellationToken.None); // sqlcmd's default
        foreach (var batch in GoSeparator().Split(script).Where(b => !string.IsNullOrWhiteSpace(b)))
            await SqlHelper.ExecuteNonQueryAsync(connection, CommandType.Text, batch, commandTimeout: 120, CancellationToken.None);
    }

    private async Task<List<string>> RowsAsync(string query)
    {
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var reader = await SqlHelper.ExecuteReaderAsync(connection, CommandType.Text, query, CancellationToken.None);
        return [.. await SqlHelper.ReadAllAsync(reader, r => r.GetString(0), CancellationToken.None)];
    }

    [GeneratedRegex(@"\$\((\w+)\)")]
    private static partial Regex Variable();

    [GeneratedRegex(@"^\s*GO\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase)]
    private static partial Regex GoSeparator();
}

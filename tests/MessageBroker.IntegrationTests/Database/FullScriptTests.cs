using System.Data;
using System.Text.RegularExpressions;
using MessageBroker.Application;
using MessageBroker.Application.Security;
using MessageBroker.Infrastructure.Data;
using MessageBroker.IntegrationTests.Infrastructure;
using Microsoft.Data.SqlClient;

namespace MessageBroker.IntegrationTests.Database;

/// <summary>
/// D41 — The full install script (db/install/BrokerDb_Full.sql) creates the database, deploys the schema and
/// adds an Admin key the broker accepts and the webhook allowlist. Running it again adds nothing.
/// D42 — The onboarding script (db/install/OnboardApplication.sql) adds an application with its key, Publish
/// permissions, topics and Pull/SignalR subscriptions, and changes nothing when run again.
/// Each test lets the full script create its own scratch database.
/// </summary>
[Collection(SqlCollection.Name)]
public sealed partial class FullScriptTests(SqlServerFixture sql) : IAsyncLifetime
{
    private readonly string _databaseName = $"BrokerDb_Full_{Guid.NewGuid():N}"[..28];
    private string ConnectionString => sql.ConnectionStringFor(_databaseName);

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => sql.DropDatabaseAsync(_databaseName);

    [Fact(DisplayName = "D41a Full script on a new server creates the database and schema, and generates an Admin key it shows once")]
    public async Task D41a_NewDatabase()
    {
        var result = await InstallAsync(webhookAllowedHosts: " Hooks.Example.com, 10.0.0.5 ,");

        var newKey = Assert.Single(result)["NewApiKey"];
        Assert.True(ApiKeys.TryGetPrefix(newKey, out var prefix));
        Assert.Equal(ApiKeys.Generate().Key.Length, newKey!.Length);
        var admin = Assert.Single(await RowsAsync(
            "SELECT a.Name, a.IsAdmin, k.Prefix, k.Hash FROM broker.ApiKeys k JOIN broker.Applications a ON a.AppId = k.AppId"));
        Assert.Equal("broker-admin", admin[0]);
        Assert.Equal(true, admin[1]);
        Assert.Equal(prefix, admin[2]);
        Assert.True(ApiKeys.Verify(newKey, (byte[])admin[3]));

        Assert.Equal(["10.0.0.5", "hooks.example.com"], await ColumnAsync("SELECT Host FROM broker.WebhookAllowedHosts ORDER BY Host"));
        Assert.Equal(SchemaDeployer.ReadAllScripts().Count(s => SchemaDeployer.IsMigration(s.Name)),
            (await ColumnAsync("SELECT ScriptName FROM broker.SchemaVersions")).Count);
    }

    [Fact(DisplayName = "D41b Full script with a given key stores its hash; running it again adds no key, Admin or host")]
    public async Task D41b_GivenKeyAndReapply()
    {
        var key = ApiKeys.Generate();

        var first = await InstallAsync(adminName: "ops-admin", adminApiKey: key.Key, webhookAllowedHosts: "hooks.example.com");
        await InstallAsync(adminName: "ops-admin", adminApiKey: key.Key, webhookAllowedHosts: "hooks.example.com");
        var third = await InstallAsync(adminName: "ops-admin");

        Assert.Null(Assert.Single(first)["NewApiKey"]);
        Assert.Null(Assert.Single(third)["NewApiKey"]);
        var admin = Assert.Single(await RowsAsync(
            "SELECT a.Name, k.Prefix, k.Hash FROM broker.ApiKeys k JOIN broker.Applications a ON a.AppId = k.AppId"));
        Assert.Equal("ops-admin", admin[0]);
        Assert.Equal(key.Prefix, admin[1]);
        Assert.Equal(key.Hash, (byte[])admin[2]);
        Assert.Equal(["hooks.example.com"], await ColumnAsync("SELECT Host FROM broker.WebhookAllowedHosts"));
    }

    [Theory(DisplayName = "D41c Full script rejects a malformed setting and adds no data")]
    [InlineData("broker-admin", "notakey", "")]
    [InlineData("broker-admin", "", "https://hooks.example.com/in")]
    [InlineData("broker admin", "", "")]
    public async Task D41c_RejectsBadSettings(string adminName, string adminApiKey, string webhookAllowedHosts)
    {
        await Assert.ThrowsAsync<SqlException>(() => InstallAsync(adminName, adminApiKey, webhookAllowedHosts));

        Assert.Empty(await ColumnAsync("SELECT Name FROM broker.Applications"));
        Assert.Empty(await ColumnAsync("SELECT Host FROM broker.WebhookAllowedHosts"));
    }

    [Fact(DisplayName = "D41d usp_ApiKey_Generate makes distinct keys in the broker's format whose hash the broker verifies")]
    public async Task D41d_GeneratedKeys()
    {
        await InstallAsync();
        var keys = new HashSet<string>();
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();
        for (var i = 0; i < 50; i++)
        {
            await using var command = new SqlCommand("broker.usp_ApiKey_Generate", connection) { CommandType = CommandType.StoredProcedure };
            var key = command.Parameters.Add("@Key", SqlDbType.VarChar, 100);
            var prefix = command.Parameters.Add("@Prefix", SqlDbType.VarChar, 32);
            var hash = command.Parameters.Add("@Hash", SqlDbType.VarBinary, 32);
            key.Direction = prefix.Direction = hash.Direction = ParameterDirection.Output;
            await command.ExecuteNonQueryAsync();

            var text = (string)key.Value;
            Assert.Matches("^mbk_[a-km-np-z2-9]{12}_[A-Za-z0-9_-]{43}$", text);
            Assert.True(ApiKeys.TryGetPrefix(text, out var expectedPrefix));
            Assert.Equal(expectedPrefix, (string)prefix.Value);
            Assert.True(ApiKeys.Verify(text, (byte[])hash.Value));
            Assert.True(keys.Add(text));
        }
    }

    [Fact(DisplayName = "D42a Onboarding adds the application, its key, Publish on topics and subscriptions with the broker's defaults")]
    public async Task D42a_Onboard()
    {
        await InstallAsync();
        await ExecuteAsync("INSERT broker.Topics (TopicId, Name) VALUES (NEWID(), N'payments');");

        var app = Assert.Single(await OnboardAsync("orders-service", publishTo: ["orders", "payments"],
            subscriptions: [("payments", "orders-payments", "Pull"), ("refunds", "orders-refunds", "SignalR")]));

        Assert.Equal("False", app["IsAdmin"]);
        var key = app["NewApiKey"];
        var stored = Assert.Single(await RowsAsync(
            $"SELECT Prefix, Hash FROM broker.ApiKeys WHERE AppId = '{app["AppId"]}' AND IsActive = 1"));
        Assert.True(ApiKeys.TryGetPrefix(key, out var prefix));
        Assert.Equal(prefix, (string)stored[0]);
        Assert.True(ApiKeys.Verify(key!, (byte[])stored[1]));

        Assert.Equal(["orders", "payments", "refunds"], await ColumnAsync("SELECT Name FROM broker.Topics ORDER BY Name"));
        Assert.Equal(["Publish orders", "Publish payments", "Receive orders-payments", "Receive orders-refunds"], await ColumnAsync($"""
            SELECT p.Permission + ' ' + COALESCE(t.Name, s.Name) COLLATE DATABASE_DEFAULT
            FROM broker.AppPermissions p
            LEFT JOIN broker.Topics t ON t.TopicId = p.ResourceId
            LEFT JOIN broker.Subscriptions s ON s.SubscriptionId = p.ResourceId
            WHERE p.AppId = '{app["AppId"]}' ORDER BY 1
            """));

        var defaults = new SubscriptionDefaults();
        var expected = $"{defaults.MaxAttempts}|{defaults.LockDurationSeconds}|{defaults.RetryBaseDelaySeconds}|" +
                       $"{defaults.RetryMaxDelaySeconds}|{defaults.MaxConcurrentDeliveries}|{new WebhookOptions().TimeoutSeconds}";
        Assert.Equal([$"orders-payments Pull Active {expected}", $"orders-refunds SignalR Active {expected}"], await ColumnAsync($"""
            SELECT Name COLLATE DATABASE_DEFAULT + ' ' + DeliveryMode + ' ' + Status + ' ' + CONCAT_WS('|', MaxAttempts, LockDurationSeconds,
                   RetryBaseDelaySeconds, RetryMaxDelaySeconds, MaxConcurrentDeliveries, WebhookTimeoutSeconds)
            FROM broker.Subscriptions WHERE OwnerAppId = '{app["AppId"]}' ORDER BY Name
            """));
    }

    [Fact(DisplayName = "D42b Onboarding run again with the same values issues no key and changes nothing")]
    public async Task D42b_OnboardAgain()
    {
        await InstallAsync();
        var first = Assert.Single(await OnboardAsync("orders-service", ["orders"], [("payments", "orders-payments", "Pull")]));
        var snapshot = await SnapshotAsync();

        var second = Assert.Single(await OnboardAsync("orders-service", ["orders"], [("payments", "orders-payments", "Pull")]));

        Assert.NotNull(first["NewApiKey"]);
        Assert.Null(second["NewApiKey"]);
        Assert.Equal((first["AppId"], first["KeyPrefix"]), (second["AppId"], second["KeyPrefix"]));
        Assert.Equal(snapshot, await SnapshotAsync());
    }

    [Theory(DisplayName = "D42c Onboarding rejects a webhook subscription, a bad name or a taken subscription, and adds nothing")]
    [InlineData("orders-service", "Webhook", "orders-payments")]
    [InlineData("orders service", "Pull", "orders-payments")]
    [InlineData("orders-service", "Pull", "taken")]
    public async Task D42c_OnboardRejects(string appName, string deliveryMode, string subscriptionName)
    {
        await InstallAsync();
        // "taken" exists on payments already, owned by another application.
        Assert.Single(await OnboardAsync("billing", [], [("payments", "taken", "Pull")]));
        var snapshot = await SnapshotAsync();

        await Assert.ThrowsAsync<SqlException>(() =>
            OnboardAsync(appName, ["orders"], [("payments", subscriptionName, deliveryMode)]));

        Assert.Equal(snapshot, await SnapshotAsync());
    }

    // ---- helpers ----

    /// <summary>Runs db/install/BrokerDb_Full.sql against the scratch database with the given settings.</summary>
    private Task<List<Dictionary<string, string?>>> InstallAsync(
        string adminName = "broker-admin", string adminApiKey = "", string webhookAllowedHosts = "")
    {
        var script = ReleaseScript.BuildFull().Replace(ReleaseScript.FullScriptDatabase, _databaseName, StringComparison.Ordinal);
        var values = new Dictionary<string, string>
        {
            ["AdminName"] = adminName,
            ["AdminApiKey"] = adminApiKey,
            ["WebhookAllowedHosts"] = webhookAllowedHosts,
        };
        Assert.Equal(ReleaseScript.FullScriptSettings.Keys.Order(), values.Keys.Order());
        foreach (var (name, value) in values)
            script = ReplaceOnce(script,
                $"N'broker.{name}', N'{ReleaseScript.FullScriptSettings[name]}';", $"N'broker.{name}', N'{value.Replace("'", "''")}';");
        return RunAsync(script);
    }

    /// <summary>Runs db/install/OnboardApplication.sql against the scratch database with the given settings.</summary>
    private Task<List<Dictionary<string, string?>>> OnboardAsync(
        string appName, string[] publishTo, (string Topic, string Name, string Mode)[] subscriptions)
    {
        var script = File.ReadAllText(Path.Combine(RepositoryRoot(), "db", "install", "OnboardApplication.sql"));
        script = ReplaceOnce(script, "USE [BrokerDb];", $"USE [{_databaseName}];");
        script = ReplaceOnce(script, "@AppName nvarchar(100) = N'orders-service';", $"@AppName nvarchar(100) = N'{appName}';");
        script = SettingsInsert().Replace(script, m => m.Groups["table"].Value switch
        {
            "PublishTo" => publishTo.Length == 0 ? "" : "INSERT @PublishTo (Topic) VALUES " + string.Join(", ", publishTo.Select(t => $"(N'{t}')")) + ";",
            _ => subscriptions.Length == 0 ? "" : "INSERT @Subscriptions (Topic, Name, DeliveryMode) VALUES " +
                 string.Join(", ", subscriptions.Select(s => $"(N'{s.Topic}', N'{s.Name}', '{s.Mode}')")) + ";",
        }, 2);
        return RunAsync(script);
    }

    /// <summary>
    /// Runs a script the way "sqlcmd -b" does: batch by batch on one session that starts in master,
    /// stopping at the first error. Returns the first result set of the last batch that returned one.
    /// </summary>
    private async Task<List<Dictionary<string, string?>>> RunAsync(string script)
    {
        var rows = new List<Dictionary<string, string?>>();
        await using var connection = new SqlConnection(sql.ConnectionStringFor("master"));
        await connection.OpenAsync();
        await using (var quotedOff = new SqlCommand("SET QUOTED_IDENTIFIER OFF;", connection)) // sqlcmd's default
            await quotedOff.ExecuteNonQueryAsync();
        foreach (var batch in GoSeparator().Split(script).Where(b => !string.IsNullOrWhiteSpace(b)))
        {
            await using var command = new SqlCommand(batch, connection) { CommandTimeout = 120 };
            await using var reader = await command.ExecuteReaderAsync();
            var first = true;
            do
            {
                if (reader.FieldCount == 0)
                    continue;
                if (!first)
                {
                    while (await reader.ReadAsync()) { }
                    continue;
                }
                first = false;
                rows = [];
                while (await reader.ReadAsync())
                    rows.Add(Enumerable.Range(0, reader.FieldCount).ToDictionary(
                        reader.GetName, i => reader.IsDBNull(i) ? null : Convert.ToString(reader.GetValue(i))));
            } while (await reader.NextResultAsync());
        }
        return rows;
    }

    private async Task ExecuteAsync(string query)
    {
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(query, connection);
        await command.ExecuteNonQueryAsync();
    }

    private async Task<List<object[]>> RowsAsync(string query)
    {
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(query, connection);
        await using var reader = await command.ExecuteReaderAsync();
        var rows = new List<object[]>();
        while (await reader.ReadAsync())
        {
            var row = new object[reader.FieldCount];
            reader.GetValues(row);
            rows.Add(row);
        }
        return rows;
    }

    private async Task<List<string>> ColumnAsync(string query) => [.. (await RowsAsync(query)).Select(r => (string)r[0])];

    /// <summary>Every application, key, topic, permission and subscription, as text.</summary>
    private Task<List<string>> SnapshotAsync() => ColumnAsync("""
        SELECT 'app ' + CAST(AppId AS varchar(36)) + ' ' + Name COLLATE DATABASE_DEFAULT FROM broker.Applications
        UNION ALL SELECT 'key ' + Prefix FROM broker.ApiKeys
        UNION ALL SELECT 'topic ' + CAST(TopicId AS varchar(36)) + ' ' + Name COLLATE DATABASE_DEFAULT FROM broker.Topics
        UNION ALL SELECT 'perm ' + CAST(AppId AS varchar(36)) + ' ' + Permission + ' ' + CAST(ResourceId AS varchar(36)) FROM broker.AppPermissions
        UNION ALL SELECT 'sub ' + CAST(SubscriptionId AS varchar(36)) + ' ' + Name COLLATE DATABASE_DEFAULT FROM broker.Subscriptions
        ORDER BY 1
        """);

    private static string ReplaceOnce(string text, string oldValue, string newValue)
    {
        Assert.Contains(oldValue, text);
        return text.Replace(oldValue, newValue, StringComparison.Ordinal);
    }

    private static string RepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "db", "install")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("db/install was not found above the test output.");
    }

    /// <summary>The example INSERT under "Topics the application publishes to" or "Subscriptions the application receives from".</summary>
    [GeneratedRegex(@"INSERT @(?<table>PublishTo|Subscriptions) \([^)]*\) VALUES\s*\r?\n[^;]*;")]
    private static partial Regex SettingsInsert();

    [GeneratedRegex(@"^\s*GO\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase)]
    private static partial Regex GoSeparator();
}

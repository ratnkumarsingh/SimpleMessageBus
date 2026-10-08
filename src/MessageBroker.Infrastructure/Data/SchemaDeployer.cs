using System.Reflection;
using DbUp;
using DbUp.Engine;
using DbUp.Helpers;
using DbUp.Support;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace MessageBroker.Infrastructure.Data;

/// <summary>
/// Deploys the broker schema with DbUp. db/Migrations scripts run once and are journaled in
/// broker.SchemaVersions; db/Programmability scripts (CREATE OR ALTER) run on every deploy in the
/// order functions, views, procedures.
/// </summary>
public sealed class SchemaDeployer(string connectionString, ILogger logger)
{
    private static readonly Assembly ScriptAssembly = typeof(SchemaDeployer).Assembly;

    private static readonly (string Folder, int Order)[] ProgrammabilityGroups =
    [
        (".db.Programmability.Functions.", 1),
        (".db.Programmability.Views.", 2),
        (".db.Programmability.Procedures.", 3),
    ];

    /// <param name="createDatabase">Create the database if it is missing (development and tests).</param>
    public void Deploy(bool createDatabase = false)
    {
        if (createDatabase)
            EnsureDatabase.For.SqlDatabase(connectionString);

        // DbUp creates its journal table (broker.SchemaVersions) before running any script, so the
        // schema must exist first. 0001_Schema.sql repeats this check for the DBA release script.
        EnsureBrokerSchema();

        var migrations = DeployChanges.To
            .SqlDatabase(connectionString)
            .WithScriptsEmbeddedInAssembly(ScriptAssembly, IsMigration)
            .JournalToSqlTable("broker", "SchemaVersions")
            .WithTransactionPerScript()
            .LogTo(new DbUpLogger(logger))
            .Build();
        Check(migrations.PerformUpgrade(), "migrations");

        var builder = DeployChanges.To
            .SqlDatabase(connectionString)
            .JournalTo(new NullJournal())
            .LogTo(new DbUpLogger(logger));
        foreach (var (folder, order) in ProgrammabilityGroups)
            builder = builder.WithScriptsEmbeddedInAssembly(ScriptAssembly, name => name.Contains(folder),
                new SqlScriptOptions { RunGroupOrder = order, ScriptType = ScriptType.RunAlways });
        Check(builder.Build().PerformUpgrade(), "programmability");
    }

    /// <summary>Every script in deployment order, for the DBA release script.</summary>
    public static IEnumerable<(string Name, string Sql)> ReadAllScripts()
    {
        var names = ScriptAssembly.GetManifestResourceNames();
        var ordered = names.Where(IsMigration).Order()
            .Concat(ProgrammabilityGroups.SelectMany(g => names.Where(n => n.Contains(g.Folder)).Order()));
        foreach (var name in ordered)
        {
            using var reader = new StreamReader(ScriptAssembly.GetManifestResourceStream(name)!);
            yield return (name, reader.ReadToEnd());
        }
    }

    /// <summary>True for a run-once migration; the resource name is also its journal ScriptName.</summary>
    public static bool IsMigration(string scriptName) => scriptName.Contains(".db.Migrations.");

    private void EnsureBrokerSchema()
    {
        using var connection = new SqlConnection(connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "IF SCHEMA_ID(N'broker') IS NULL EXEC(N'CREATE SCHEMA broker AUTHORIZATION dbo');";
        command.ExecuteNonQuery();
    }

    private static void Check(DatabaseUpgradeResult result, string stage)
    {
        if (!result.Successful)
            throw new InvalidOperationException(
                $"Schema deployment failed during {stage} at script '{result.ErrorScript?.Name}'.", result.Error);
    }

    private sealed class DbUpLogger(ILogger logger) : DbUp.Engine.Output.IUpgradeLog
    {
        public void LogTrace(string format, params object[] args) => logger.LogTrace(format, args);
        public void LogDebug(string format, params object[] args) => logger.LogDebug(format, args);
        public void LogInformation(string format, params object[] args) => logger.LogDebug(format, args);
        public void LogWarning(string format, params object[] args) => logger.LogWarning(format, args);
        public void LogError(string format, params object[] args) => logger.LogError(format, args);
        public void LogError(Exception ex, string format, params object[] args) => logger.LogError(ex, format, args);
    }
}

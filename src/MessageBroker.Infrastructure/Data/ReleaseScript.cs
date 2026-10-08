using System.Text;

namespace MessageBroker.Infrastructure.Data;

/// <summary>
/// Builds one SQL script that DBAs can review and apply in place of <see cref="SchemaDeployer"/>, for
/// hosts that run with Broker:Database:DeploySchemaOnStartup off. It brings a database at any version
/// up to date: each migration runs only when broker.SchemaVersions has no row for it and then writes
/// that row under the name DbUp uses, so a later DbUp deploy skips it. Programmability scripts are
/// CREATE OR ALTER and always run. Apply it with "sqlcmd -b" so the run stops at the first error.
/// </summary>
public static class ReleaseScript
{
    public static string Build()
    {
        var sql = new StringBuilder();
        sql.AppendLine("-- Internal Message Broker schema release script. Generated; do not edit.")
           .AppendLine("-- Apply with: sqlcmd -b -S <server> -d <database> -i <this file>")
           // sqlcmd starts with QUOTED_IDENTIFIER OFF, which filtered indexes reject; procedures also
           // keep the QUOTED_IDENTIFIER and ANSI_NULLS settings they were created with.
           .AppendLine("SET QUOTED_IDENTIFIER ON;")
           .AppendLine("SET ANSI_NULLS ON;")
           .AppendLine("SET ANSI_PADDING ON;")
           .AppendLine("SET ANSI_WARNINGS ON;")
           .AppendLine("SET ARITHABORT ON;")
           .AppendLine("SET CONCAT_NULL_YIELDS_NULL ON;")
           .AppendLine("SET NUMERIC_ROUNDABORT OFF;")
           .AppendLine("SET XACT_ABORT ON;")
           .AppendLine("SET NOEXEC OFF;")
           .AppendLine("GO")
           .AppendLine("IF SCHEMA_ID(N'broker') IS NULL EXEC(N'CREATE SCHEMA broker AUTHORIZATION dbo');")
           .AppendLine("GO")
           // The same table DbUp creates for its journal.
           .AppendLine("IF OBJECT_ID(N'broker.SchemaVersions', N'U') IS NULL")
           .AppendLine("    CREATE TABLE broker.SchemaVersions (")
           .AppendLine("        Id int IDENTITY(1,1) NOT NULL CONSTRAINT PK_SchemaVersions_Id PRIMARY KEY,")
           .AppendLine("        ScriptName nvarchar(255) NOT NULL,")
           .AppendLine("        Applied datetime NOT NULL);")
           .AppendLine("GO");

        foreach (var (name, script) in SchemaDeployer.ReadAllScripts())
        {
            sql.AppendLine().AppendLine($"-- ===== {name}");
            if (SchemaDeployer.IsMigration(name))
            {
                // NOEXEC spans batches, so it skips every GO-separated batch of an applied migration.
                var literal = name.Replace("'", "''", StringComparison.Ordinal);
                sql.AppendLine($"IF EXISTS (SELECT 1 FROM broker.SchemaVersions WHERE ScriptName = N'{literal}') SET NOEXEC ON;")
                   .AppendLine("GO")
                   .AppendLine("BEGIN TRANSACTION;")
                   .AppendLine("GO")
                   .AppendLine(script.TrimEnd())
                   .AppendLine("GO")
                   .AppendLine($"INSERT broker.SchemaVersions (ScriptName, Applied) VALUES (N'{literal}', GETDATE());")
                   .AppendLine("COMMIT TRANSACTION;")
                   .AppendLine("GO")
                   .AppendLine("SET NOEXEC OFF;")
                   .AppendLine("GO");
            }
            else
            {
                sql.AppendLine(script.TrimEnd()).AppendLine("GO");
            }
        }
        return sql.ToString();
    }
}

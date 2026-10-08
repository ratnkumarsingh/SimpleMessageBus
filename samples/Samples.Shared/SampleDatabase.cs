using System.Data;
using System.Text.RegularExpressions;
using Dapper;
using Microsoft.Data.SqlClient;

namespace Samples.Shared;

/// <summary>
/// Deploys the <c>sample</c> schema (tables and procedures in sample.sql) and calls its procedures.
/// Like the broker, the samples keep their SQL in stored procedures and use no ORM.
/// </summary>
public sealed partial class SampleDatabase(string connectionString)
{
    public string ConnectionString { get; } = connectionString;

    /// <summary>Creates the database if it is missing, then applies sample.sql (safe to repeat).</summary>
    public async Task DeployAsync(CancellationToken ct = default)
    {
        var builder = new SqlConnectionStringBuilder(ConnectionString);
        if (!string.IsNullOrEmpty(builder.InitialCatalog))
        {
            var database = builder.InitialCatalog;
            builder.InitialCatalog = "master";
            await using var master = new SqlConnection(builder.ConnectionString);
            await master.ExecuteAsync(new CommandDefinition(
                """
                IF DB_ID(@database) IS NULL
                BEGIN
                    DECLARE @sql nvarchar(300) = N'CREATE DATABASE ' + QUOTENAME(@database);
                    EXEC (@sql);
                END
                """, new { database }, cancellationToken: ct));
        }

        await using var stream = typeof(SampleDatabase).Assembly.GetManifestResourceStream("sample.sql")!;
        var script = await new StreamReader(stream).ReadToEndAsync(ct);
        await using var connection = await OpenAsync(ct);
        foreach (var batch in GoSeparator().Split(script).Where(b => !string.IsNullOrWhiteSpace(b)))
            await connection.ExecuteAsync(new CommandDefinition(batch, cancellationToken: ct));
    }

    internal async Task<SqlConnection> OpenAsync(CancellationToken ct)
    {
        var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync(ct);
        return connection;
    }

    internal async Task<T> QuerySingleAsync<T>(string procedure, object args, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        return await connection.QuerySingleAsync<T>(new CommandDefinition(procedure, args, commandType: CommandType.StoredProcedure, cancellationToken: ct));
    }

    internal async Task<IReadOnlyList<T>> QueryAsync<T>(string procedure, object args, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        return [.. await connection.QueryAsync<T>(new CommandDefinition(procedure, args, commandType: CommandType.StoredProcedure, cancellationToken: ct))];
    }

    internal async Task ExecuteAsync(string procedure, object args, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct);
        await connection.ExecuteAsync(new CommandDefinition(procedure, args, commandType: CommandType.StoredProcedure, cancellationToken: ct));
    }

    [GeneratedRegex(@"^\s*GO\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase)]
    private static partial Regex GoSeparator();
}

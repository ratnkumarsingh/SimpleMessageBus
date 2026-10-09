using System.Data;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;

namespace Samples.Shared;

/// <summary>
/// Deploys the <c>sample</c> schema (tables and procedures in sample.sql) and calls its procedures.
/// Like the broker, the samples keep their SQL in stored procedures and use plain ADO.NET: explicit
/// parameters, and rows read by hand.
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
            await master.OpenAsync(ct);
            await using var create = new SqlCommand(
                """
                IF DB_ID(@database) IS NULL
                BEGIN
                    DECLARE @sql nvarchar(300) = N'CREATE DATABASE ' + QUOTENAME(@database);
                    EXEC (@sql);
                END
                """, master);
            create.Parameters.Add(Param("database", database));
            await create.ExecuteNonQueryAsync(ct);
        }

        await using var stream = typeof(SampleDatabase).Assembly.GetManifestResourceStream("sample.sql")!;
        var script = await new StreamReader(stream).ReadToEndAsync(ct);
        await using var connection = await OpenAsync(ct);
        foreach (var batch in GoSeparator().Split(script).Where(b => !string.IsNullOrWhiteSpace(b)))
        {
            await using var command = new SqlCommand(batch, connection);
            await command.ExecuteNonQueryAsync(ct);
        }
    }

    /// <summary>An input parameter; null is sent as DBNull.</summary>
    public static SqlParameter Param(string name, object? value) => new("@" + name, value ?? DBNull.Value);

    internal async Task<SqlConnection> OpenAsync(CancellationToken ct)
    {
        var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync(ct);
        return connection;
    }

    /// <summary>The first row of the procedure's result, read with <paramref name="map"/>; no row throws.</summary>
    internal async Task<T> QuerySingleAsync<T>(string procedure, Func<SqlDataReader, T> map, CancellationToken ct, params SqlParameter[] parameters)
    {
        await using var connection = await OpenAsync(ct);
        await using var command = Procedure(procedure, connection, parameters);
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            throw new InvalidOperationException($"{procedure} returned no row.");
        return map(reader);
    }

    internal async Task<IReadOnlyList<T>> QueryAsync<T>(string procedure, Func<SqlDataReader, T> map, CancellationToken ct, params SqlParameter[] parameters)
    {
        await using var connection = await OpenAsync(ct);
        await using var command = Procedure(procedure, connection, parameters);
        await using var reader = await command.ExecuteReaderAsync(ct);
        var rows = new List<T>();
        while (await reader.ReadAsync(ct))
            rows.Add(map(reader));
        return rows;
    }

    internal async Task ExecuteAsync(string procedure, CancellationToken ct, params SqlParameter[] parameters)
    {
        await using var connection = await OpenAsync(ct);
        await using var command = Procedure(procedure, connection, parameters);
        await command.ExecuteNonQueryAsync(ct);
    }

    private static SqlCommand Procedure(string procedure, SqlConnection connection, SqlParameter[] parameters)
    {
        var command = new SqlCommand(procedure, connection) { CommandType = CommandType.StoredProcedure };
        command.Parameters.AddRange(parameters);
        return command;
    }

    [GeneratedRegex(@"^\s*GO\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase)]
    private static partial Regex GoSeparator();
}

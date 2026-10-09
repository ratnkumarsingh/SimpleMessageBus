using System.Data;
using Microsoft.Data.SqlClient;

namespace MessageBroker.Infrastructure.Data;

public sealed class BrokerDbOptions
{
    public string ConnectionString { get; set; } = "";
}

/// <summary>
/// Runs the broker's stored procedures through <see cref="SqlHelper"/>. Every call opens a pooled connection,
/// uses CommandType.StoredProcedure, maps rows with an explicit mapper, and maps SQL errors with
/// <see cref="SqlErrorMapper"/> (also errors raised while rows are read, which stays inside the call).
/// </summary>
public sealed class Db(BrokerDbOptions options)
{
    public SqlConnection CreateConnection() => new(options.ConnectionString);

    public async Task<T> RunAsync<T>(Func<SqlConnection, Task<T>> work, CancellationToken ct)
    {
        try
        {
            await using var connection = CreateConnection();
            await connection.OpenAsync(ct);
            return await work(connection);
        }
        catch (SqlException ex) when (SqlErrorMapper.Map(ex) is { } mapped)
        {
            throw mapped;
        }
    }

    private static string Proc(string name) => $"broker.{name}";

    private Task<T> ReadAsync<T>(string proc, Func<SqlDataReader, Task<T>> read, CancellationToken ct, SqlParameter[] parameters) =>
        RunAsync(async c =>
        {
            await using var reader = await SqlHelper.ExecuteReaderAsync(c, CommandType.StoredProcedure, Proc(proc), ct, parameters);
            return await read(reader);
        }, ct);

    /// <summary>Every row of the first result set.</summary>
    public Task<IReadOnlyList<T>> QueryAsync<T>(string proc, Func<SqlDataReader, T> map, CancellationToken ct, params SqlParameter[] parameters) =>
        ReadAsync(proc, r => SqlHelper.ReadAllAsync(r, map, ct), ct, parameters);

    /// <summary>Exactly one row.</summary>
    public Task<T> QuerySingleAsync<T>(string proc, Func<SqlDataReader, T> map, CancellationToken ct, params SqlParameter[] parameters) =>
        ReadAsync(proc, r => SqlHelper.ReadSingleAsync(r, map, ct), ct, parameters);

    /// <summary>Zero or one row; null when there is none.</summary>
    public Task<T?> QuerySingleOrDefaultAsync<T>(string proc, Func<SqlDataReader, T> map, CancellationToken ct, params SqlParameter[] parameters)
        where T : class =>
        ReadAsync(proc, async r => (await SqlHelper.ReadSingleOrDefaultAsync(r, map, ct)) is (true, var value) ? value : null, ct, parameters);

    /// <summary>The first column of exactly one row (a Guid, bool, int or DateTime the procedure SELECTs).</summary>
    public Task<T> ScalarAsync<T>(string proc, CancellationToken ct, params SqlParameter[] parameters) =>
        QuerySingleAsync(proc, r => SqlHelper.ConvertScalar<T>(r.GetValue(0)), ct, parameters);

    public Task ExecuteAsync(string proc, CancellationToken ct, params SqlParameter[] parameters) =>
        RunAsync(c => SqlHelper.ExecuteNonQueryAsync(c, CommandType.StoredProcedure, Proc(proc), ct, parameters), ct);

    /// <summary>Several result sets; <paramref name="read"/> moves between them with NextResultAsync.</summary>
    public Task<T> ReadMultipleAsync<T>(string proc, Func<SqlDataReader, Task<T>> read, CancellationToken ct, params SqlParameter[] parameters) =>
        ReadAsync(proc, read, ct, parameters);
}

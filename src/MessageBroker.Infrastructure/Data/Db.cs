using System.Data;
using Dapper;
using Microsoft.Data.SqlClient;

namespace MessageBroker.Infrastructure.Data;

public sealed class BrokerDbOptions
{
    public string ConnectionString { get; set; } = "";
}

/// <summary>
/// Runs stored procedures through Dapper. Every call opens a pooled connection, uses
/// CommandType.StoredProcedure, and maps SQL errors with <see cref="SqlErrorMapper"/>.
/// </summary>
public sealed class Db(BrokerDbOptions options)
{
    static Db() => DapperSetup.Register();

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

    public Task RunAsync(Func<SqlConnection, Task> work, CancellationToken ct) =>
        RunAsync(async c => { await work(c); return 0; }, ct);

    private static CommandDefinition Proc(string name, object? args, CancellationToken ct) =>
        new($"broker.{name}", args, commandType: CommandType.StoredProcedure, cancellationToken: ct);

    public Task<IReadOnlyList<T>> QueryAsync<T>(string proc, object? args, CancellationToken ct) =>
        RunAsync<IReadOnlyList<T>>(async c => (await c.QueryAsync<T>(Proc(proc, args, ct))).AsList(), ct);

    public Task<T?> QuerySingleOrDefaultAsync<T>(string proc, object? args, CancellationToken ct) =>
        RunAsync(c => c.QuerySingleOrDefaultAsync<T>(Proc(proc, args, ct)), ct);

    public async Task<T> QuerySingleAsync<T>(string proc, object? args, CancellationToken ct) =>
        await RunAsync(c => c.QuerySingleAsync<T>(Proc(proc, args, ct)), ct);

    public Task ExecuteAsync(string proc, object? args, CancellationToken ct) =>
        RunAsync(c => c.ExecuteAsync(Proc(proc, args, ct)), ct);

    public Task<T> QueryMultipleAsync<T>(string proc, object? args, Func<SqlMapper.GridReader, Task<T>> read, CancellationToken ct) =>
        RunAsync(async c =>
        {
            await using var grid = await c.QueryMultipleAsync(Proc(proc, args, ct));
            return await read(grid);
        }, ct);
}

internal static class DapperSetup
{
    private static int _registered;

    public static void Register()
    {
        if (Interlocked.Exchange(ref _registered, 1) == 1)
            return;
        SqlMapper.AddTypeHandler(new UtcDateTimeHandler());
    }

    /// <summary>Every datetime2 column holds UTC (SYSUTCDATETIME), so stamp the Kind on read.</summary>
    private sealed class UtcDateTimeHandler : SqlMapper.TypeHandler<DateTime>
    {
        public override void SetValue(IDbDataParameter parameter, DateTime value)
        {
            parameter.DbType = DbType.DateTime2;
            parameter.Value = value.Kind == DateTimeKind.Local ? value.ToUniversalTime() : value;
        }

        public override DateTime Parse(object value) => DateTime.SpecifyKind((DateTime)value, DateTimeKind.Utc);
    }
}

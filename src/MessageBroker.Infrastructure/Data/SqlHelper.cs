using System.Data;
using System.Globalization;
using Microsoft.Data.SqlClient;

namespace MessageBroker.Infrastructure.Data;

/// <summary>
/// Plain ADO.NET helpers in the shape of Microsoft's Data Access Application Block SqlHelper
/// (ExecuteNonQuery, ExecuteScalar, ExecuteReader), made async and built on Microsoft.Data.SqlClient.
/// Commands and parameters are explicit; results are read with hand-written mappers.
/// </summary>
public static class SqlHelper
{
    /// <summary>Strings up to this length are sent as nvarchar(4000), longer ones as nvarchar(max), so plans stay stable.</summary>
    private const int NVarCharLength = 4000;
    private const int VarBinaryLength = 8000;

    public static async Task<int> ExecuteNonQueryAsync(
        SqlConnection connection, CommandType commandType, string commandText, CancellationToken ct,
        params SqlParameter[] parameters)
    {
        await using var command = CreateCommand(connection, commandType, commandText, parameters);
        return await command.ExecuteNonQueryAsync(ct);
    }

    /// <summary>As above, with a command timeout in seconds other than the default 30 (for long scripts).</summary>
    public static async Task<int> ExecuteNonQueryAsync(
        SqlConnection connection, CommandType commandType, string commandText, int commandTimeout, CancellationToken ct,
        params SqlParameter[] parameters)
    {
        await using var command = CreateCommand(connection, commandType, commandText, parameters);
        command.CommandTimeout = commandTimeout;
        return await command.ExecuteNonQueryAsync(ct);
    }

    public static async Task<int> ExecuteNonQueryAsync(
        string connectionString, CommandType commandType, string commandText, CancellationToken ct,
        params SqlParameter[] parameters)
    {
        await using var connection = await OpenAsync(connectionString, ct);
        return await ExecuteNonQueryAsync(connection, commandType, commandText, ct, parameters);
    }

    /// <summary>The first column of the first row, or null when there is no row or the value is NULL.</summary>
    public static async Task<object?> ExecuteScalarAsync(
        SqlConnection connection, CommandType commandType, string commandText, CancellationToken ct,
        params SqlParameter[] parameters)
    {
        await using var command = CreateCommand(connection, commandType, commandText, parameters);
        var value = await command.ExecuteScalarAsync(ct);
        return value is DBNull ? null : value;
    }

    public static async Task<object?> ExecuteScalarAsync(
        string connectionString, CommandType commandType, string commandText, CancellationToken ct,
        params SqlParameter[] parameters)
    {
        await using var connection = await OpenAsync(connectionString, ct);
        return await ExecuteScalarAsync(connection, commandType, commandText, ct, parameters);
    }

    /// <summary>The caller disposes the reader; the connection stays open until then.</summary>
    public static async Task<SqlDataReader> ExecuteReaderAsync(
        SqlConnection connection, CommandType commandType, string commandText, CancellationToken ct,
        params SqlParameter[] parameters)
    {
        await using var command = CreateCommand(connection, commandType, commandText, parameters);
        return await command.ExecuteReaderAsync(ct);
    }

    /// <summary>Reads every row of the first result set with <paramref name="map"/>.</summary>
    public static async Task<IReadOnlyList<T>> ReadAllAsync<T>(SqlDataReader reader, Func<SqlDataReader, T> map, CancellationToken ct)
    {
        var rows = new List<T>();
        while (await reader.ReadAsync(ct))
            rows.Add(map(reader));
        return rows;
    }

    /// <summary>Exactly one row; zero or several rows throw.</summary>
    public static async Task<T> ReadSingleAsync<T>(SqlDataReader reader, Func<SqlDataReader, T> map, CancellationToken ct)
    {
        var (found, value) = await ReadSingleOrDefaultAsync(reader, map, ct);
        return found ? value : throw new InvalidOperationException("Sequence contains no elements");
    }

    /// <summary>Zero or one row; several rows throw.</summary>
    public static async Task<(bool Found, T Value)> ReadSingleOrDefaultAsync<T>(SqlDataReader reader, Func<SqlDataReader, T> map, CancellationToken ct)
    {
        if (!await reader.ReadAsync(ct))
            return (false, default!);
        var value = map(reader);
        if (await reader.ReadAsync(ct))
            throw new InvalidOperationException("Sequence contains more than one element");
        return (true, value);
    }

    /// <summary>
    /// A typed input parameter. Null becomes DBNull; DateTime is sent as datetime2 (a Local value as UTC,
    /// since every column holds UTC); strings and byte arrays get fixed sizes so query plans are reused.
    /// </summary>
    public static SqlParameter Param(string name, object? value)
    {
        var parameter = new SqlParameter(name.StartsWith('@') ? name : "@" + name, value ?? DBNull.Value);
        switch (value)
        {
            case DateTime dateTime:
                parameter.SqlDbType = SqlDbType.DateTime2;
                parameter.Value = dateTime.Kind == DateTimeKind.Local ? dateTime.ToUniversalTime() : dateTime;
                break;
            case string text:
                parameter.SqlDbType = SqlDbType.NVarChar;
                parameter.Size = text.Length > NVarCharLength ? -1 : NVarCharLength;
                break;
            case byte[] bytes:
                parameter.SqlDbType = SqlDbType.VarBinary;
                parameter.Size = bytes.Length > VarBinaryLength ? -1 : VarBinaryLength;
                break;
        }
        return parameter;
    }

    /// <summary>A table-valued parameter of the user-defined table type <paramref name="typeName"/>.</summary>
    public static SqlParameter Table(string name, string typeName, DataTable rows) =>
        new(name.StartsWith('@') ? name : "@" + name, SqlDbType.Structured) { TypeName = typeName, Value = rows };

    /// <summary>Converts a scalar result: numeric widening and narrowing, Guid from text, UTC DateTimes, NULL as default.</summary>
    public static T ConvertScalar<T>(object? value)
    {
        if (value is null or DBNull)
            return default!;
        var target = Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T);
        if (value is DateTime dateTime && target == typeof(DateTime))
            return (T)(object)DateTime.SpecifyKind(dateTime, DateTimeKind.Utc);
        if (target.IsInstanceOfType(value))
            return (T)value;
        if (target == typeof(Guid))
            return (T)(object)(value is string s ? Guid.Parse(s) : (Guid)value);
        return (T)Convert.ChangeType(value, target, CultureInfo.InvariantCulture);
    }

    private static SqlCommand CreateCommand(SqlConnection connection, CommandType commandType, string commandText, SqlParameter[] parameters)
    {
        var command = new SqlCommand(commandText, connection) { CommandType = commandType };
        command.Parameters.AddRange(parameters);
        return command;
    }

    private static async Task<SqlConnection> OpenAsync(string connectionString, CancellationToken ct)
    {
        var connection = new SqlConnection(connectionString);
        try
        {
            await connection.OpenAsync(ct);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }
}

/// <summary>
/// Reads columns by name for the hand-written mappers. A missing column throws, so a mapper that does not
/// match its procedure fails loudly. Numbers convert between integer widths (bit, int, bigint), and every
/// datetime2 value is stamped DateTimeKind.Utc (the database stores UTC only).
/// </summary>
public static class SqlDataReaderExtensions
{
    public static bool IsNull(this SqlDataReader reader, string column) => reader.IsDBNull(reader.GetOrdinal(column));

    public static Guid GetGuid(this SqlDataReader reader, string column) => reader.GetGuid(reader.GetOrdinal(column));

    public static Guid? GetNullableGuid(this SqlDataReader reader, string column) =>
        reader.GetOrdinal(column) is var i && reader.IsDBNull(i) ? null : reader.GetGuid(i);

    public static string GetString(this SqlDataReader reader, string column) => reader.GetString(reader.GetOrdinal(column));

    public static string? GetNullableString(this SqlDataReader reader, string column) =>
        reader.GetOrdinal(column) is var i && reader.IsDBNull(i) ? null : reader.GetString(i);

    public static bool GetBoolean(this SqlDataReader reader, string column) =>
        Convert.ToBoolean(reader.GetValue(reader.GetOrdinal(column)), CultureInfo.InvariantCulture);

    public static byte GetByte(this SqlDataReader reader, string column) =>
        Convert.ToByte(reader.GetValue(reader.GetOrdinal(column)), CultureInfo.InvariantCulture);

    public static int GetInt32(this SqlDataReader reader, string column) =>
        Convert.ToInt32(reader.GetValue(reader.GetOrdinal(column)), CultureInfo.InvariantCulture);

    public static int? GetNullableInt32(this SqlDataReader reader, string column) =>
        reader.GetOrdinal(column) is var i && reader.IsDBNull(i) ? null : Convert.ToInt32(reader.GetValue(i), CultureInfo.InvariantCulture);

    public static long GetInt64(this SqlDataReader reader, string column) =>
        Convert.ToInt64(reader.GetValue(reader.GetOrdinal(column)), CultureInfo.InvariantCulture);

    public static long? GetNullableInt64(this SqlDataReader reader, string column) =>
        reader.GetOrdinal(column) is var i && reader.IsDBNull(i) ? null : Convert.ToInt64(reader.GetValue(i), CultureInfo.InvariantCulture);

    public static byte[] GetBytes(this SqlDataReader reader, string column) => (byte[])reader.GetValue(reader.GetOrdinal(column));

    public static DateTime GetUtcDateTime(this SqlDataReader reader, string column) =>
        DateTime.SpecifyKind(reader.GetDateTime(reader.GetOrdinal(column)), DateTimeKind.Utc);

    public static DateTime? GetNullableUtcDateTime(this SqlDataReader reader, string column) =>
        reader.GetOrdinal(column) is var i && reader.IsDBNull(i) ? null : DateTime.SpecifyKind(reader.GetDateTime(i), DateTimeKind.Utc);
}

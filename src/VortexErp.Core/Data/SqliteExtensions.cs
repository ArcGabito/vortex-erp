using System.Globalization;
using Microsoft.Data.Sqlite;

namespace VortexErp.Core.Data;

/// <summary>Ayudas mínimas sobre Microsoft.Data.Sqlite (sin reflexión, apto para AOT en iOS/Android).</summary>
internal static class SqliteExtensions
{
    private const string TimeFormat = "yyyy-MM-dd'T'HH:mm:ss.fff'Z'";

    /// <summary>Fechas en UTC con formato fijo: se ordenan bien como texto.</summary>
    public static string Time(DateTimeOffset value) => value.UtcDateTime.ToString(TimeFormat, CultureInfo.InvariantCulture);

    public static SqliteCommand Command(this SqliteConnection connection, string sql, SqliteTransaction? tx,
        params (string Name, object? Value)[] args)
    {
        var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = tx;
        foreach (var (name, value) in args)
            command.Parameters.AddWithValue(name, ToDb(value));
        return command;
    }

    public static async Task<int> ExecAsync(this SqliteConnection connection, string sql, SqliteTransaction? tx = null,
        params (string Name, object? Value)[] args)
    {
        await using var command = connection.Command(sql, tx, args);
        return await command.ExecuteNonQueryAsync();
    }

    public static async Task<T> ScalarAsync<T>(this SqliteConnection connection, string sql, SqliteTransaction? tx = null,
        params (string Name, object? Value)[] args)
    {
        await using var command = connection.Command(sql, tx, args);
        var value = await command.ExecuteScalarAsync();
        if (value is null or DBNull)
            return default!;
        var target = Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T);
        if (target == typeof(decimal))
            return (T)(object)ToDecimal(value);
        return (T)Convert.ChangeType(value, target, CultureInfo.InvariantCulture);
    }

    public static async Task<List<T>> QueryAsync<T>(this SqliteConnection connection, string sql, Func<SqliteDataReader, T> map,
        SqliteTransaction? tx = null, params (string Name, object? Value)[] args)
    {
        await using var command = connection.Command(sql, tx, args);
        await using var reader = await command.ExecuteReaderAsync();
        var list = new List<T>();
        while (await reader.ReadAsync())
            list.Add(map(reader));
        return list;
    }

    public static async Task<T?> FirstOrDefaultAsync<T>(this SqliteConnection connection, string sql, Func<SqliteDataReader, T> map,
        SqliteTransaction? tx = null, params (string Name, object? Value)[] args) where T : class
    {
        await using var command = connection.Command(sql, tx, args);
        await using var reader = await command.ExecuteReaderAsync();
        return await reader.ReadAsync() ? map(reader) : null;
    }

    // ---------- Lectura de columnas por nombre ----------

    public static Guid Guid(this SqliteDataReader r, string column) => System.Guid.Parse(r.GetString(r.GetOrdinal(column)));

    public static Guid? GuidOrNull(this SqliteDataReader r, string column) =>
        r.IsDBNull(r.GetOrdinal(column)) ? null : System.Guid.Parse(r.GetString(r.GetOrdinal(column)));

    public static string Str(this SqliteDataReader r, string column) => r.GetString(r.GetOrdinal(column));

    public static string? StrOrNull(this SqliteDataReader r, string column) =>
        r.IsDBNull(r.GetOrdinal(column)) ? null : r.GetString(r.GetOrdinal(column));

    public static decimal Dec(this SqliteDataReader r, string column) =>
        r.IsDBNull(r.GetOrdinal(column)) ? 0 : ToDecimal(r.GetValue(r.GetOrdinal(column)));

    public static decimal? DecOrNull(this SqliteDataReader r, string column) =>
        r.IsDBNull(r.GetOrdinal(column)) ? null : ToDecimal(r.GetValue(r.GetOrdinal(column)));

    public static int Int(this SqliteDataReader r, string column) => r.GetInt32(r.GetOrdinal(column));

    public static long Long(this SqliteDataReader r, string column) => r.GetInt64(r.GetOrdinal(column));

    public static bool Bool(this SqliteDataReader r, string column) => r.GetInt64(r.GetOrdinal(column)) != 0;

    public static DateTimeOffset Time(this SqliteDataReader r, string column) =>
        DateTimeOffset.Parse(r.GetString(r.GetOrdinal(column)), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);

    public static DateTimeOffset? TimeOrNull(this SqliteDataReader r, string column) =>
        r.IsDBNull(r.GetOrdinal(column)) ? null : r.Time(column);

    /// <summary>SQLite guarda REAL: se redondea a 15 dígitos significativos para quitar el ruido binario.</summary>
    private static decimal ToDecimal(object value) => value switch
    {
        double d => Convert.ToDecimal(d),
        long l => l,
        string s => decimal.Parse(s, CultureInfo.InvariantCulture),
        _ => Convert.ToDecimal(value, CultureInfo.InvariantCulture),
    };

    private static object ToDb(object? value) => value switch
    {
        null => DBNull.Value,
        System.Guid g => g.ToString(),
        DateTimeOffset d => Time(d),
        bool b => b ? 1 : 0,
        decimal m => (double)m,
        Enum e => e.ToString(),
        _ => value,
    };
}

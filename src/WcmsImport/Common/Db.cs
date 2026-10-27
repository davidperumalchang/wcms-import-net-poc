using System.Text.Json;
using Npgsql;

namespace WcmsImport.Common;

/// <summary>
/// Small helpers over Npgsql so the SQL reads like the SQL. Every value goes in as a positional
/// parameter ($1, $2, ...); nothing is ever concatenated into the SQL text.
/// </summary>
public static class Db
{
    public static NpgsqlCommand Command(this NpgsqlDataSource db, string sql, params object?[] args) =>
        WithArgs(db.CreateCommand(sql), args);

    public static NpgsqlCommand Command(this NpgsqlConnection conn, string sql, params object?[] args) =>
        WithArgs(new NpgsqlCommand(sql, conn), args);

    public static async Task<int> RunAsync(this NpgsqlCommand cmd)
    {
        await using (cmd) return await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>First row mapped with <paramref name="map"/>, or default when there is none.</summary>
    public static async Task<T?> FirstOrDefaultAsync<T>(this NpgsqlCommand cmd, Func<NpgsqlDataReader, T> map)
    {
        await using (cmd)
        {
            await using var reader = await cmd.ExecuteReaderAsync();
            return await reader.ReadAsync() ? map(reader) : default;
        }
    }

    /// <summary>
    /// Rows as column-name dictionaries, so API responses keep the database column names
    /// (job_id, last_error, ...). jsonb columns are returned as JSON, not as strings.
    /// </summary>
    public static async Task<List<Dictionary<string, object?>>> RowsAsync(this NpgsqlCommand cmd)
    {
        await using (cmd)
        {
            await using var reader = await cmd.ExecuteReaderAsync();
            var rows = new List<Dictionary<string, object?>>();
            while (await reader.ReadAsync())
            {
                var row = new Dictionary<string, object?>(reader.FieldCount);
                for (var i = 0; i < reader.FieldCount; i++)
                {
                    row[reader.GetName(i)] = reader.IsDBNull(i) ? null
                        : reader.GetDataTypeName(i) == "jsonb" ? JsonDocument.Parse(reader.GetString(i)).RootElement.Clone()
                        : reader.GetValue(i);
                }
                rows.Add(row);
            }
            return rows;
        }
    }

    public static T? Get<T>(this NpgsqlDataReader reader, string column)
    {
        var i = reader.GetOrdinal(column);
        return reader.IsDBNull(i) ? default : reader.GetFieldValue<T>(i);
    }

    private static NpgsqlCommand WithArgs(NpgsqlCommand cmd, object?[] args)
    {
        foreach (var arg in args) cmd.Parameters.Add(new NpgsqlParameter { Value = arg ?? DBNull.Value });
        return cmd;
    }
}

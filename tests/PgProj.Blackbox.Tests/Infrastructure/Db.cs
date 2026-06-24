using Npgsql;

namespace PgProj.Blackbox.Tests.Infrastructure;

/// <summary>
/// Thin Npgsql helpers used ONLY to arrange database state and to assert observable outcomes — never
/// to drive the tool. A <see cref="Db"/> wraps one connection string (one database).
/// </summary>
public sealed class Db(string connectionString)
{
    public string ConnectionString { get; } = connectionString;

    public async Task ExecAsync(string sql)
    {
        await using var c = new NpgsqlConnection(ConnectionString);
        await c.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, c);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<T?> ScalarAsync<T>(string sql)
    {
        await using var c = new NpgsqlConnection(ConnectionString);
        await c.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, c);
        var v = await cmd.ExecuteScalarAsync();
        return v is null or DBNull ? default : (T)Convert.ChangeType(v, typeof(T));
    }

    /// <summary>True when a relation (table/view/matview/sequence) exists in the given schema.</summary>
    public Task<bool> RelationExistsAsync(string schema, string name) =>
        ScalarAsync<bool>(
            $"SELECT EXISTS (SELECT 1 FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace " +
            $"WHERE n.nspname='{schema}' AND c.relname='{name}')");

    public Task<bool> ColumnExistsAsync(string schema, string table, string column) =>
        ScalarAsync<bool>(
            $"SELECT EXISTS (SELECT 1 FROM information_schema.columns " +
            $"WHERE table_schema='{schema}' AND table_name='{table}' AND column_name='{column}')");

    public Task<bool> SchemaExistsAsync(string schema) =>
        ScalarAsync<bool>($"SELECT EXISTS (SELECT 1 FROM pg_namespace WHERE nspname='{schema}')");
}

using System.Threading;
using System.Threading.Tasks;
using Npgsql;

namespace PgProj.Core.Introspection;

/// <summary>Executes a generated deployment script against a live Postgres server.</summary>
public sealed class DatabaseDeployer
{
    public async Task ExecuteAsync(string connectionString, string script, CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);

        // Npgsql accepts multiple semicolon-separated statements in one command. The script's own
        // BEGIN/COMMIT (when enabled) makes the whole batch atomic.
        await using var cmd = new NpgsqlCommand(script, conn);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}

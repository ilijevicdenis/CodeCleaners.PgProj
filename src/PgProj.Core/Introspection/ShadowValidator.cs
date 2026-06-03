using System;
using System.Threading;
using System.Threading.Tasks;
using Npgsql;

namespace PgProj.Core.Introspection;

/// <summary>The outcome of a shadow validation: did the project's SQL apply against a real Postgres?</summary>
public sealed record ValidationOutcome(bool Ok, string? Error = null, string? SqlState = null, int Position = 0)
{
    public static readonly ValidationOutcome Success = new(true);
}

/// <summary>
/// Validates a project's SQL the way PostgreSQL itself would — by actually running it. It creates a
/// throwaway temporary database on the target server, applies the deploy script inside a transaction,
/// rolls the transaction back, and then drops the temporary database. Nothing persists: the rollback
/// discards every change and the DROP removes the scratch database. This catches the runtime/semantic
/// errors a static analyzer cannot (and must not guess at): missing objects, type mismatches, invalid
/// function/view bodies, bad generated/CHECK expressions, etc. Raw ADO.NET (Npgsql), no ORM.
/// </summary>
public sealed class ShadowValidator
{
    /// <param name="adminConnectionString">A connection to the server (its database must allow CREATE DATABASE).</param>
    /// <param name="script">The deploy script — generate it WITHOUT its own BEGIN/COMMIT; this wraps it.</param>
    public async Task<ValidationOutcome> ValidateAsync(string adminConnectionString, string script, CancellationToken ct = default)
    {
        var temp = "pgproj_validate_" + Guid.NewGuid().ToString("N")[..16];
        var tempConn = new NpgsqlConnectionStringBuilder(adminConnectionString) { Database = temp }.ConnectionString;

        // CREATE DATABASE cannot run inside a transaction — issue it on the admin connection first.
        await using (var admin = new NpgsqlConnection(adminConnectionString))
        {
            await admin.OpenAsync(ct);
            await using var create = new NpgsqlCommand($"CREATE DATABASE \"{temp}\"", admin);
            await create.ExecuteNonQueryAsync(ct);
        }

        try
        {
            await using var conn = new NpgsqlConnection(tempConn);
            await conn.OpenAsync(ct);
            await using var tx = await conn.BeginTransactionAsync(ct);
            try
            {
                await using var cmd = new NpgsqlCommand(script, conn, tx);
                await cmd.ExecuteNonQueryAsync(ct);
                await tx.RollbackAsync(ct);                 // discard everything — this was only a test
                return ValidationOutcome.Success;
            }
            catch (PostgresException ex)
            {
                await tx.RollbackAsync(CancellationToken.None);
                return new ValidationOutcome(false, ex.MessageText, ex.SqlState, ex.Position);
            }
        }
        finally
        {
            // Throw the scratch database away (FORCE terminates any lingering backend; PG13+).
            await using var admin = new NpgsqlConnection(adminConnectionString);
            await admin.OpenAsync(CancellationToken.None);
            await using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{temp}\" WITH (FORCE)", admin);
            await drop.ExecuteNonQueryAsync(CancellationToken.None);
        }
    }
}

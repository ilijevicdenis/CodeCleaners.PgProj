using System;
using System.Threading;
using System.Threading.Tasks;
using Npgsql;
using PgProj.Core.Introspection;
using PgProj.Core.Versioning;

namespace PgProj.Core.Snapshot;

/// <summary>
/// Captures a <see cref="SchemaSnapshot"/> from a live PostgreSQL database: introspects the canonical
/// model once via <see cref="LiveDatabaseReader"/> and stamps the manifest with the source server's
/// version (read from <c>server_version_num</c> / <c>version()</c>). The volatile stamp fields
/// (<c>toolVersion</c>, <c>createdUtc</c>) are injected by the caller, never read from the clock here —
/// the same determinism contract the package builder follows.
/// </summary>
/// <remarks>
/// The introspection version profile is selected from the live server's own major version, so the
/// catalog SQL issued matches the server being captured (rather than always assuming the latest).
/// </remarks>
public sealed class SchemaSnapshotReader
{
    /// <summary>
    /// Introspects <paramref name="connectionString"/> and assembles a snapshot. <paramref name="toolVersion"/>
    /// and <paramref name="createdUtc"/> are caller-injected stamps.
    /// </summary>
    public async Task<SchemaSnapshot> CaptureAsync(
        string connectionString,
        string toolVersion,
        string createdUtc,
        CancellationToken ct = default)
    {
        var (versionText, major) = await ReadServerVersionAsync(connectionString, ct);

        // Introspect with the profile matching the live server, so the catalog SQL fits the source.
        var profile = PostgresVersionProfile.ForMajor(major);
        var model = await new LiveDatabaseReader(profile).ReadAsync(connectionString, ct);

        var sourceName = TryDatabaseName(connectionString);
        return SchemaSnapshot.Create(model, versionText, major, toolVersion, createdUtc, sourceName);
    }

    /// <summary>Reads the source server's version: <c>(version(), major)</c> where major comes from <c>server_version_num</c>.</summary>
    private static async Task<(string VersionText, int Major)> ReadServerVersionAsync(string connectionString, CancellationToken ct)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(
            "SELECT version(), current_setting('server_version_num')::int", conn);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct))
            throw new InvalidOperationException("Could not read the source server version.");
        var versionText = r.GetString(0);
        var num = r.GetInt32(1);              // e.g. 180000 → 18, 130012 → 13
        return (versionText, num / 10000);
    }

    private static string? TryDatabaseName(string connectionString)
    {
        try { return new NpgsqlConnectionStringBuilder(connectionString).Database; }
        catch { return null; }
    }
}

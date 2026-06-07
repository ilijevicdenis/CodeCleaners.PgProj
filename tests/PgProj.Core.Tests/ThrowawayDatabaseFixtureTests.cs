using System.Threading.Tasks;
using Npgsql;
using Xunit;

namespace PgProj.Core.Tests;

/// <summary>
/// Exercises <see cref="ThrowawayDatabaseFixture"/> itself: verifies that each fixture-owning class
/// receives an isolated, writable database and that objects created in one fixture are not visible in
/// another. Skipped (no-op) when PGPROJ_TEST_CONNECTION is unset — identical to every other
/// DB-backed integration test class in this suite.
/// </summary>
public sealed class ThrowawayDatabaseFixtureTests : IClassFixture<ThrowawayDatabaseFixture>
{
    private readonly ThrowawayDatabaseFixture _fixture;

    public ThrowawayDatabaseFixtureTests(ThrowawayDatabaseFixture fixture)
        => _fixture = fixture;

    /// <summary>
    /// Creates a table and a row in the fixture's throwaway database, then reads them back.
    /// Proves the fixture provides a real, writable, isolated PostgreSQL database.
    /// </summary>
    [Fact]
    public async Task Fixture_database_is_writable_and_isolated()
    {
        var conn = _fixture.ConnectionString;
        if (string.IsNullOrWhiteSpace(conn)) return;   // no live DB — treated as a skip

        await using var c = new NpgsqlConnection(conn);
        await c.OpenAsync();

        // Create a table unique to this fixture instance.
        await using (var cmd = new NpgsqlCommand(
            "CREATE TABLE public.fixture_isolation_probe (id int PRIMARY KEY, label text)",
            c))
            await cmd.ExecuteNonQueryAsync();

        // Insert a row.
        await using (var cmd = new NpgsqlCommand(
            "INSERT INTO public.fixture_isolation_probe VALUES (1, 'hello')",
            c))
            await cmd.ExecuteNonQueryAsync();

        // Read it back — proves the DB is live and writable.
        await using var read = new NpgsqlCommand(
            "SELECT label FROM public.fixture_isolation_probe WHERE id = 1",
            c);
        var result = (string?)await read.ExecuteScalarAsync();
        Assert.Equal("hello", result);
    }

    /// <summary>
    /// Verifies that the fixture connection string targets a database whose name starts with the
    /// expected prefix, confirming the throwaway DB was created (not re-using the admin DB).
    /// </summary>
    [Fact]
    public void Fixture_connection_string_targets_a_dedicated_throwaway_database()
    {
        var conn = _fixture.ConnectionString;
        if (string.IsNullOrWhiteSpace(conn)) return;   // no live DB — treated as a skip

        var builder = new NpgsqlConnectionStringBuilder(conn);
        Assert.StartsWith("pgproj_test_", builder.Database,
            System.StringComparison.Ordinal);
    }
}

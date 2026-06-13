using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Npgsql;

namespace PgProj.Core.Testing;

/// <summary>The outcome of one unit test.</summary>
public enum TestStatus { Passed, Failed, Inconclusive }

/// <summary>One test to run: its display name, SQL body, and (for a negative test) the SQLSTATE it must raise.</summary>
public sealed record TestCase(string Name, string Sql, string? ExpectedSqlState = null);

/// <summary>The result of running one test.</summary>
public sealed record TestResult(string Name, TestStatus Status, string? Message = null, string? SqlState = null);

/// <summary>The aggregate run result with bucket counts and an all-passed gate.</summary>
public sealed record TestRunResult(IReadOnlyList<TestResult> Results)
{
    public int Passed => Results.Count(r => r.Status == TestStatus.Passed);
    public int Failed => Results.Count(r => r.Status == TestStatus.Failed);
    public int Inconclusive => Results.Count(r => r.Status == TestStatus.Inconclusive);
    public bool AllPassed => Failed == 0;
}

/// <summary>
/// A PL/pgSQL database unit-test runner (#139 — the SQL Server unit-testing analogue). Each test runs inside
/// its own <c>BEGIN … ROLLBACK</c> against the target, so the database is left untouched (single-transaction
/// scope). A built-in assertion prelude provides the predefined conditions (row count, scalar value,
/// empty / not-empty result set, expected-schema column check, data checksum, and an explicit inconclusive
/// signal); a failed assertion raises, which the runner records as a failure. A test may also declare an
/// expected SQLSTATE (a negative / expected-exception test) and passes only when exactly that error is raised.
/// </summary>
public static class PgUnitRunner
{
    /// <summary>The inconclusive sentinel SQLSTATE raised by <c>pgproj_inconclusive()</c>.</summary>
    public const string InconclusiveSqlState = "P0002";

    /// <summary>
    /// The assertion helpers, prepended (inside the test transaction) to every test so the predefined
    /// conditions are always available and always rolled back. <c>CREATE OR REPLACE</c> so re-installing
    /// per test is free.
    /// </summary>
    public const string Prelude = """
        CREATE OR REPLACE FUNCTION pgproj_assert(cond boolean, msg text DEFAULT 'condition was false') RETURNS void
          LANGUAGE plpgsql AS $pp$ BEGIN IF cond IS NOT TRUE THEN RAISE EXCEPTION 'assertion failed: %', msg; END IF; END $pp$;
        CREATE OR REPLACE FUNCTION pgproj_assert_rowcount(q text, expected bigint) RETURNS void
          LANGUAGE plpgsql AS $pp$ DECLARE n bigint; BEGIN EXECUTE 'SELECT count(*) FROM ('||q||') _pp' INTO n;
            IF n <> expected THEN RAISE EXCEPTION 'row count: expected %, got %', expected, n; END IF; END $pp$;
        CREATE OR REPLACE FUNCTION pgproj_assert_scalar(q text, expected text) RETURNS void
          LANGUAGE plpgsql AS $pp$ DECLARE v text; BEGIN EXECUTE 'SELECT ('||q||')::text' INTO v;
            IF v IS DISTINCT FROM expected THEN RAISE EXCEPTION 'scalar: expected %, got %', expected, v; END IF; END $pp$;
        CREATE OR REPLACE FUNCTION pgproj_assert_empty(q text) RETURNS void
          LANGUAGE plpgsql AS $pp$ DECLARE n bigint; BEGIN EXECUTE 'SELECT count(*) FROM ('||q||') _pp' INTO n;
            IF n <> 0 THEN RAISE EXCEPTION 'expected an empty result set, got % row(s)', n; END IF; END $pp$;
        CREATE OR REPLACE FUNCTION pgproj_assert_not_empty(q text) RETURNS void
          LANGUAGE plpgsql AS $pp$ DECLARE n bigint; BEGIN EXECUTE 'SELECT count(*) FROM ('||q||') _pp' INTO n;
            IF n = 0 THEN RAISE EXCEPTION 'expected a non-empty result set'; END IF; END $pp$;
        CREATE OR REPLACE FUNCTION pgproj_assert_column_type(rel regclass, col text, expected_type text) RETURNS void
          LANGUAGE plpgsql AS $pp$ DECLARE t text; BEGIN
            SELECT format_type(a.atttypid, a.atttypmod) INTO t FROM pg_attribute a
              WHERE a.attrelid = rel AND a.attname = col AND a.attnum > 0 AND NOT a.attisdropped;
            IF t IS NULL THEN RAISE EXCEPTION 'expected column %.% to exist', rel, col; END IF;
            IF t <> expected_type THEN RAISE EXCEPTION 'column %.%: expected type %, got %', rel, col, expected_type, t; END IF; END $pp$;
        CREATE OR REPLACE FUNCTION pgproj_assert_checksum(q text, expected text) RETURNS void
          LANGUAGE plpgsql AS $pp$ DECLARE h text; BEGIN
            EXECUTE 'SELECT md5(string_agg(_pp::text, '','' ORDER BY _pp::text)) FROM ('||q||') _pp' INTO h;
            IF h IS DISTINCT FROM expected THEN RAISE EXCEPTION 'checksum: expected %, got %', expected, h; END IF; END $pp$;
        CREATE OR REPLACE FUNCTION pgproj_inconclusive(msg text DEFAULT 'inconclusive') RETURNS void
          LANGUAGE plpgsql AS $pp$ BEGIN RAISE EXCEPTION 'inconclusive: %', msg USING ERRCODE = 'P0002'; END $pp$;
        """;

    public static async Task<TestRunResult> RunAsync(string connectionString, IReadOnlyList<TestCase> tests, CancellationToken ct = default)
    {
        var results = new List<TestResult>(tests.Count);
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);

        foreach (var test in tests)
            results.Add(await RunOneAsync(conn, test, ct));

        return new TestRunResult(results);
    }

    private static async Task<TestResult> RunOneAsync(NpgsqlConnection conn, TestCase test, CancellationToken ct)
    {
        await using var tx = await conn.BeginTransactionAsync(ct);
        try
        {
            await using (var cmd = new NpgsqlCommand(Prelude + "\n" + test.Sql, conn, tx))
                await cmd.ExecuteNonQueryAsync(ct);

            await tx.RollbackAsync(ct);   // single-transaction scope: leave the DB untouched

            // No error raised. A negative test that expected one therefore FAILS.
            return test.ExpectedSqlState is { } expected
                ? new TestResult(test.Name, TestStatus.Failed, $"expected SQLSTATE {expected} but no error was raised")
                : new TestResult(test.Name, TestStatus.Passed);
        }
        catch (PostgresException ex)
        {
            await SafeRollback(tx);
            if (test.ExpectedSqlState is { } expected)
                return string.Equals(ex.SqlState, expected, StringComparison.OrdinalIgnoreCase)
                    ? new TestResult(test.Name, TestStatus.Passed)
                    : new TestResult(test.Name, TestStatus.Failed, $"expected SQLSTATE {expected}, got {ex.SqlState}: {ex.MessageText}", ex.SqlState);

            if (string.Equals(ex.SqlState, InconclusiveSqlState, StringComparison.Ordinal))
                return new TestResult(test.Name, TestStatus.Inconclusive, ex.MessageText, ex.SqlState);

            return new TestResult(test.Name, TestStatus.Failed, ex.MessageText, ex.SqlState);
        }
    }

    private static async Task SafeRollback(NpgsqlTransaction tx)
    {
        try { await tx.RollbackAsync(CancellationToken.None); } catch { /* connection already gone */ }
    }

    /// <summary>
    /// Parses a test file body for a leading <c>-- @expect-sqlstate: XXXXX</c> directive (a negative test).
    /// Returns the SQLSTATE when present, else null. Scans only the leading comment lines.
    /// </summary>
    public static string? ParseExpectedSqlState(string sql)
    {
        foreach (var raw in sql.Split('\n').Take(10))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            if (!line.StartsWith("--", StringComparison.Ordinal)) break;   // past the leading comment block
            var i = line.IndexOf("@expect-sqlstate:", StringComparison.OrdinalIgnoreCase);
            if (i >= 0) return line[(i + "@expect-sqlstate:".Length)..].Trim();
        }
        return null;
    }
}

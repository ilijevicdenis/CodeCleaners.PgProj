using System;
using Xunit;

namespace PgProj.Core.Tests;

/// <summary>
/// A <see cref="FactAttribute"/> that runs only when a live database is configured via
/// PGPROJ_TEST_CONNECTION; otherwise it is skipped (xUnit v2 has no runtime skip, so the decision is
/// made here at discovery time). Used by the generated corpus tests for cases the static engine can't
/// decide without false positives — they are verified by executing them against real PostgreSQL.
/// </summary>
public sealed class DbFactAttribute : FactAttribute
{
    public DbFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("PGPROJ_TEST_CONNECTION")))
            Skip = "requires PGPROJ_TEST_CONNECTION (verified by executing against PostgreSQL)";
    }
}

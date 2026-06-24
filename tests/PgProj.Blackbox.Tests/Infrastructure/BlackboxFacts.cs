namespace PgProj.Blackbox.Tests.Infrastructure;

/// <summary>
/// A test that needs only the built <c>pgproj</c> binary (no database). Skipped at discovery time when
/// the CLI hasn't been built — xUnit v2 has no runtime skip, so the decision is made in the ctor.
/// </summary>
public sealed class CliFactAttribute : FactAttribute
{
    public CliFactAttribute()
    {
        if (!PgProjCli.Available)
            Skip = "pgproj CLI not built (expected src/PgProj.Cli/bin/<cfg>/net10.0/PgProj.Cli.dll).";
    }
}

/// <summary>
/// A test that needs the full Docker harness: the built CLI plus the SOURCE and TARGET servers
/// (PGPROJ_SOURCE_CONNECTION / PGPROJ_TARGET_CONNECTION). Skipped when any is missing, so the suite
/// stays green in `dotnet test PgProj.slnx` without containers and runs in full after
/// tests/blackbox-db/blackbox-db.ps1 -Export.
/// </summary>
public sealed class LiveFactAttribute : FactAttribute
{
    public LiveFactAttribute()
    {
        if (!PgProjCli.Available)
            Skip = "pgproj CLI not built.";
        else if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("PGPROJ_SOURCE_CONNECTION")) ||
                 string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("PGPROJ_TARGET_CONNECTION")))
            Skip = "requires PGPROJ_SOURCE_CONNECTION + PGPROJ_TARGET_CONNECTION (run tests/blackbox-db/blackbox-db.ps1 -Export).";
    }
}

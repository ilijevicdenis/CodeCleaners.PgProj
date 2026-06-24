namespace PgProj.Blackbox.Tests.Infrastructure;

/// <summary>
/// Base for every blackbox test class: holds the shared fixture and the CLI-running helpers. Skipping
/// when the Docker harness or the CLI is unavailable is handled at discovery time by the
/// <see cref="CliFactAttribute"/> / <see cref="LiveFactAttribute"/> the test methods carry — so the
/// project stays green in `dotnet test PgProj.slnx` on a machine with no containers, yet runs in full
/// once tests/blackbox-db/blackbox-db.ps1 -Export has been run.
/// </summary>
[Collection("blackbox")]
public abstract class BlackboxTestBase(BlackboxFixture fx)
{
    protected readonly BlackboxFixture Fx = fx;

    protected CliResult Run(string args, string? workingDirectory = null,
        IReadOnlyDictionary<string, string>? env = null) =>
        PgProjCli.Run(args, workingDirectory, env);

    /// <summary>Quote a path/connection string for the single-line CLI arg string.</summary>
    protected static string Q(string s) => $"\"{s}\"";
}

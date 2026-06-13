namespace PgProj.Core.Cli;

/// <summary>
/// Stable, classified process exit codes for the <c>pgproj</c> CLI. This is the contract a CI
/// pipeline (EP-CICD) gates on, so a failure <em>class</em> is machine-distinguishable
/// (bad-invocation vs build vs analysis vs reference vs drift vs deploy vs validation) without
/// scraping stderr.
/// </summary>
/// <remarks>
/// Codes are <b>append-only</b>: never renumber an existing class — only add new ones — mirroring the
/// parser-corpus discipline, so a pipeline pinned to "5 == reference error" never silently shifts.
/// Deploy/drift gating hooks are reserved here for EP-CICD to wire onto the publish/drift verbs.
/// </remarks>
public static class ExitCode
{
    /// <summary>The operation completed successfully.</summary>
    public const int Success = 0;

    /// <summary>An unclassified error: an unexpected exception reached <c>main</c>.</summary>
    public const int Error = 1;

    /// <summary>Bad invocation: unknown command, missing required argument, or a malformed option value.</summary>
    public const int Usage = 2;

    /// <summary>The project failed to build/parse (SQL syntax errors, duplicate object definitions, …).</summary>
    public const int BuildError = 3;

    /// <summary>The static-analysis gate blocked the operation (errors, or warnings under <c>--strict</c>).</summary>
    public const int AnalysisBlocked = 4;

    /// <summary>A project / artifact / package reference was unresolved or invalid (EP-REF).</summary>
    public const int ReferenceError = 5;

    /// <summary>Drift was detected when a verb was asked to gate on it (reserved for EP-CICD drift gating).</summary>
    public const int Drift = 6;

    /// <summary>A publish/deploy against the live database failed (DDL error, transaction rollback, connection).</summary>
    public const int DeployError = 7;

    /// <summary>Shadow-database validation found the project does not apply cleanly (EP-VALIDATE).</summary>
    public const int ValidationFailed = 8;

    /// <summary>The publish was blocked by the possible-data-loss gate (<c>BlockOnPossibleDataLoss</c>, #140).</summary>
    public const int DataLossBlocked = 9;

    /// <summary>One or more database unit tests failed (<c>pgproj test</c>, #139).</summary>
    public const int TestFailed = 10;
}

// EP-VS #25 Route B (modern). The VisualStudio.Extensibility extension entry point.
using Microsoft.VisualStudio.Extensibility;

namespace PgProj.VisualStudio;

/// <summary>
/// The PgProj extension for Visual Studio 2026+. Out-of-process (VisualStudio.Extensibility) on
/// .NET 10. It contributes Publish and Schema Compare commands, a Schema Compare tool window, and a
/// <c>.sql</c> language-server provider — all of which delegate to the <c>pgproj</c> engine so there
/// is one code path shared with the CLI, the MSBuild SDK, and the VS Code extension.
/// </summary>
[VisualStudioContribution]
internal sealed class PgProjExtension : Extension
{
    /// <inheritdoc/>
    public override ExtensionConfiguration ExtensionConfiguration => new()
    {
        Metadata = new(
            id: "PgProj.VisualStudio.b0000000-0000-0000-0000-000000000025",
            version: this.ExtensionAssemblyVersion,
            publisherName: "CodeCleaners",
            displayName: "PgProj for PostgreSQL",
            description: "PostgreSQL database projects (.pgproj) in Visual Studio: Publish, Schema Compare, and .sql IntelliSense — powered by the pgproj engine.")
        {
            // Declare the .NET runtime(s) this OOP extension can run on. VS 2026 ships .NET 10 (LTS),
            // which is also the workspace standard, so we target net10.0.
            //
            // KNOWN ISSUE — VSExtensibility #544 (opened 2025-12-19, VS 2026 Community): early VS 2026
            // builds failed to *select* the net10.0 runtime via DotnetTarget.Custom, so commands silently
            // did not run. If that reproduces in your build: use the Debug-menu ".NET runtime" picker to
            // force net10.0 when F5-debugging, confirm the fix shipped, and only as a last resort drop the
            // csproj TargetFramework + this line to net8.0 (the documented fallback baseline).
            DotnetTargetVersions = [DotnetTarget.Custom("net10.0")],
        },
    };
}

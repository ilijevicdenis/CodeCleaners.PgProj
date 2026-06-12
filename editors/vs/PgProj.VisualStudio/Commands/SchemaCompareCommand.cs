// EP-VS #25 Route B (modern) + #116. "Schema Compare" command — seeds the interactive Schema Compare
// tool window (source = the selected .pgproj, target = PGPROJ_CONNECTION when set) and runs the first
// compare IN-PROCESS. Source/target can then be re-picked inside the window itself.
using Microsoft.VisualStudio.Extensibility;
using Microsoft.VisualStudio.Extensibility.Commands;
using Microsoft.VisualStudio.Extensibility.Shell;
using PgProj.VisualStudio.Engine;
using PgProj.VisualStudio.ToolWindows;

namespace PgProj.VisualStudio.Commands;

/// <summary>
/// Opens the <see cref="SchemaCompareToolWindow"/> for the selected <c>.pgproj</c>. The window is an
/// interactive session over the engine's selectable change set: source/target pickers (project /
/// .pgpkg / .schema.snapshot / live connection), a checkable diff, and Generate Script / Apply. When
/// <c>PGPROJ_CONNECTION</c> is set the first compare runs immediately; otherwise the window opens with
/// the source prefilled and waits for a target.
/// </summary>
[VisualStudioContribution]
internal sealed class SchemaCompareCommand : Command
{
    public SchemaCompareCommand(VisualStudioExtensibility extensibility)
        : base(extensibility)
    {
    }

    /// <inheritdoc/>
    public override CommandConfiguration CommandConfiguration => new("%PgProj.SchemaCompare.DisplayName%")
    {
        // Same placement/visibility policy as PublishCommand: .pgproj project context menu only
        // (the classic extension's VSCT group, values inlined for the compile-time evaluator),
        // no Extensions-menu entry.
        Placements = [CommandPlacement.VsctParent(new Guid("b0000000-0000-0000-0000-0000000000a2"), 0x1020, priority: 0x0101)],
        Icon = new(ImageMoniker.KnownValues.CompareFiles, IconSettings.IconAndText),
        VisibleWhen = ActivationConstraint.ClientContext(ClientContextKey.Shell.ActiveSelectionFileName, @"\.pgproj$"),
        EnabledWhen = ActivationConstraint.ClientContext(ClientContextKey.Shell.ActiveSelectionFileName, @"\.pgproj$"),
    };

    /// <inheritdoc/>
    public override async Task ExecuteCommandAsync(IClientContext context, CancellationToken cancellationToken)
    {
        var selectedUri = await context.GetSelectedPathAsync(cancellationToken);
        var project = PgProjContext.FindNearestProject(selectedUri?.LocalPath);
        if (project is null)
        {
            await this.Extensibility.Shell().ShowPromptAsync(
                "No .pgproj found for the current selection.", PromptOptions.OK, cancellationToken);
            return;
        }

        var session = SchemaCompareState.GetOrCreate(this.Extensibility);
        session.SourceSpec = project;
        if (session.TargetSpec.Trim().Length == 0 &&
            (ConnectionStore.TryGet(project) ?? PgProjContext.ResolveConnection()) is { } connection)
        {
            session.TargetSpec = connection;
        }

        await this.Extensibility.Shell().ShowToolWindowAsync<SchemaCompareToolWindow>(activate: true, cancellationToken);

        if (session.TargetSpec.Trim().Length > 0)
            await session.CompareAsync(cancellationToken);
        else
            session.Summary = $"Source: {Path.GetFileName(project)}. Enter a target (connection string, .pgproj, .pgpkg, or .schema.snapshot) and Compare.";
    }
}

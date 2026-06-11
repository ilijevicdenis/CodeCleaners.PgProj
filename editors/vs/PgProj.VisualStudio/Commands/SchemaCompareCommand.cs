// EP-VS #25 Route B (modern). "Schema Compare" command — runs the engine's comparer IN-PROCESS and
// opens the Schema Compare tool window over the resulting change set (no subprocess, no JSON round-trip).
using Microsoft.VisualStudio.Extensibility;
using Microsoft.VisualStudio.Extensibility.Commands;
using Microsoft.VisualStudio.Extensibility.Shell;
using PgProj.VisualStudio.Engine;
using PgProj.VisualStudio.ToolWindows;

namespace PgProj.VisualStudio.Commands;

/// <summary>
/// Compares the selected <c>.pgproj</c> against the configured PostgreSQL target and shows the result in
/// the <see cref="SchemaCompareToolWindow"/>. The comparison is the engine's two-way comparer
/// (<see cref="PgProjEngine.CompareAsync"/>); the window only renders the resulting change set.
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

        var target = PgProjContext.ResolveConnection();
        if (target is null)
        {
            await this.Extensibility.Shell().ShowPromptAsync(
                $"No compare target is set. Define {PgProjContext.ConnectionEnvVar} with a PostgreSQL connection " +
                "string (the live database to compare against), then try again.",
                PromptOptions.OK,
                cancellationToken);
            return;
        }

        try
        {
            var result = await PgProjEngine.CompareAsync(project, target, cancellationToken);
            SchemaCompareState.SetLatest(SchemaCompareViewModelFactory.From(Path.GetFileName(project), result));
            await this.Extensibility.Shell().ShowToolWindowAsync<SchemaCompareToolWindow>(activate: true, cancellationToken);
        }
        catch (Exception ex)
        {
            await this.Extensibility.Shell().ShowPromptAsync(
                $"Schema compare failed: {ex.Message}", PromptOptions.OK, cancellationToken);
        }
    }
}

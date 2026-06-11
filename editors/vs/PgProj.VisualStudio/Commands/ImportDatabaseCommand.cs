// EP-VS "Import Database…" — the SSDT "Import Database" analogue for a .pgproj: a modal dialog
// (connection + Test Connection + checkable object list via the engine's extract units), then the
// checked objects are written into the project as .sql files. The SDK's **/*.sql auto-glob makes
// them Build items immediately — no per-file project edits needed.
using Microsoft.VisualStudio.Extensibility;
using Microsoft.VisualStudio.Extensibility.Commands;
using Microsoft.VisualStudio.Extensibility.Documents;
using Microsoft.VisualStudio.Extensibility.Shell;
using Microsoft.VisualStudio.RpcContracts.Notifications;
using PgProj.VisualStudio.Engine;
using PgProj.VisualStudio.ImportDialog;

namespace PgProj.VisualStudio.Commands;

/// <summary>
/// Imports objects from a live PostgreSQL database into the selected <c>.pgproj</c>. The dialog
/// connects through the in-process engine (no DDEX dependency — there is no maintained PostgreSQL
/// DDEX provider for the built-in VS data dialog), lists every object the introspection reads
/// (the same per-object file units <c>pgproj extract</c> writes), and OK imports the checked ones
/// into the project's extract-layout folders. Existing files are skipped unless overwrite is checked.
/// </summary>
[VisualStudioContribution]
internal sealed class ImportDatabaseCommand : Command
{
    private OutputChannel? outputChannel;

    public ImportDatabaseCommand(VisualStudioExtensibility extensibility)
        : base(extensibility)
    {
    }

    /// <inheritdoc/>
    public override CommandConfiguration CommandConfiguration => new("%PgProj.ImportDatabase.DisplayName%")
    {
        // Same placement/visibility policy as Publish/Schema Compare: .pgproj project context menu
        // only (the classic extension's VSCT group, values inlined for the compile-time evaluator).
        Placements = [CommandPlacement.VsctParent(new Guid("b0000000-0000-0000-0000-0000000000a2"), 0x1020, priority: 0x0102)],
        Icon = new(ImageMoniker.KnownValues.Extension, IconSettings.IconAndText),
        VisibleWhen = ActivationConstraint.ClientContext(ClientContextKey.Shell.ActiveSelectionFileName, @"\.pgproj$"),
        EnabledWhen = ActivationConstraint.ClientContext(ClientContextKey.Shell.ActiveSelectionFileName, @"\.pgproj$"),
    };

    /// <inheritdoc/>
    public override async Task InitializeAsync(CancellationToken cancellationToken)
        => this.outputChannel = await this.Extensibility.Views().Output.CreateOutputChannelAsync("PgProj Import", cancellationToken);

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

        var dialogModel = new ImportDatabaseDialogViewModel
        {
            ConnectionString = PgProjContext.ResolveConnection() ?? string.Empty,
        };
        using var dialogControl = new ImportDatabaseDialogControl(dialogModel);
        var confirmed = await this.Extensibility.Shell().ShowDialogAsync(
            dialogControl, $"Import Database into '{Path.GetFileName(project)}'", DialogOption.OKCancel, cancellationToken);
        if (confirmed != DialogResult.OK)
            return;

        var selected = dialogModel.Objects.Where(o => o.IsIncluded).Select(o => o.RelativePath).ToList();
        if (selected.Count == 0 || dialogModel.LoadedUnits.Count == 0)
        {
            await this.Extensibility.Shell().ShowPromptAsync(
                "Nothing to import — load the objects and check at least one before OK.", PromptOptions.OK, cancellationToken);
            return;
        }

        try
        {
            var projectDir = Path.GetDirectoryName(project)!;
            int written = 0, skipped = 0;
            foreach (var relativePath in selected)
            {
                if (!dialogModel.LoadedUnits.TryGetValue(relativePath, out var sql))
                    continue;

                var fullPath = Path.Combine(projectDir, relativePath);
                if (File.Exists(fullPath) && !dialogModel.OverwriteExisting)
                {
                    skipped++;
                    await WriteLineAsync($"skipped (exists): {relativePath}", cancellationToken);
                    continue;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
                await File.WriteAllTextAsync(fullPath, sql, cancellationToken);
                written++;
                await WriteLineAsync($"imported: {relativePath}", cancellationToken);
            }

            var summary = $"Imported {written} object file(s) into {Path.GetFileName(project)}" +
                          (skipped > 0 ? $" ({skipped} skipped — already exist; check 'Overwrite existing files' to replace)." : ".");
            await WriteLineAsync(summary, cancellationToken);
            await this.Extensibility.Shell().ShowPromptAsync(summary, PromptOptions.OK, cancellationToken);
        }
        catch (Exception ex)
        {
            await WriteLineAsync($"Import failed: {ex.Message}", cancellationToken);
            await this.Extensibility.Shell().ShowPromptAsync(
                $"Import failed: {ex.Message} See the 'PgProj Import' Output window.", PromptOptions.OK, cancellationToken);
        }
    }

    private Task WriteLineAsync(string text, CancellationToken cancellationToken)
        => this.outputChannel is { } channel ? channel.WriteLineAsync(text) : Task.CompletedTask;
}

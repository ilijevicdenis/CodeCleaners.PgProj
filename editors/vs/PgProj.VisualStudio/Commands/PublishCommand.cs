// EP-VS #25 Route B (modern) + #115. "Publish" command — a modal publish dialog (connection,
// profile, SQLCMD variables, options, generate-script), then the engine IN-PROCESS via the shared
// PublishService, streaming progress to an Output channel.
using Microsoft.VisualStudio.Extensibility;
using Microsoft.VisualStudio.Extensibility.Commands;
using Microsoft.VisualStudio.Extensibility.Documents;
using Microsoft.VisualStudio.Extensibility.Shell;
using Microsoft.VisualStudio.RpcContracts.Notifications;
using PgProj.Core.Deployment;
using PgProj.Core.Publishing;
using PgProj.VisualStudio.Engine;
using PgProj.VisualStudio.PublishDialog;

namespace PgProj.VisualStudio.Commands;

/// <summary>
/// Publishes the selected <c>.pgproj</c> to a live PostgreSQL server. A modal dialog collects the
/// connection, an optional <c>.pgpublish.json</c> profile, SQLCMD variable overrides, and options
/// (allow-drops / no-transaction / generate-script-only) — mirroring the SQL Server publish flow.
/// All comparison + deploy logic is the engine's (<see cref="PgProjEngine"/> →
/// <c>PgProj.Core.Publishing.PublishService</c>, the same single code path the CLI uses), so the VS
/// publish produces the identical deploy script. The connection string is never stored.
/// </summary>
[VisualStudioContribution]
internal sealed class PublishCommand : Command
{
    private OutputChannel? outputChannel;

    public PublishCommand(VisualStudioExtensibility extensibility)
        : base(extensibility)
    {
    }

    /// <inheritdoc/>
    public override CommandConfiguration CommandConfiguration => new("%PgProj.Publish.DisplayName%")
    {
        // Lives on the .pgproj project-node context menu, NOT on the Extensions menu: database
        // controls appear only when a PostgreSQL database project is selected. The VSCT parent is
        // the group owned by the classic project-system extension (PgProj.VisualStudio.ProjectSystem:
        // PgProjGuids.CommandSetGuidString + ProjectContextGroupId — keep in sync). Values must be
        // inlined: the Extensibility compile-time evaluator (CEE0018) rejects user-defined helpers.
        Placements = [CommandPlacement.VsctParent(new Guid("b0000000-0000-0000-0000-0000000000a2"), 0x1020, priority: 0x0100)],
        Icon = new(ImageMoniker.KnownValues.Extension, IconSettings.IconAndText),
        VisibleWhen = ActivationConstraint.ClientContext(ClientContextKey.Shell.ActiveSelectionFileName, @"\.pgproj$"),
        EnabledWhen = ActivationConstraint.ClientContext(ClientContextKey.Shell.ActiveSelectionFileName, @"\.pgproj$"),
    };

    /// <inheritdoc/>
    public override async Task InitializeAsync(CancellationToken cancellationToken)
        => this.outputChannel = await this.Extensibility.Views().Output.CreateOutputChannelAsync("PgProj Publish", cancellationToken);

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

        // The dialog: connection (prefilled from PGPROJ_CONNECTION), profile (prefilled when one sits
        // next to the project), variables, options. Cancel = no work.
        var dialogModel = new PublishDialogViewModel
        {
            ProjectName = $"Publish '{Path.GetFileName(project)}'",
            ConnectionString = PgProjContext.ResolveConnection() ?? string.Empty,
            ProfilePath = PgProjContext.FindDefaultProfile(project) ?? string.Empty,
        };
        using var dialogControl = new PublishDialogControl(dialogModel);
        var confirmed = await this.Extensibility.Shell().ShowDialogAsync(
            dialogControl, "Publish PgProj Database", DialogOption.OKCancel, cancellationToken);
        if (confirmed != DialogResult.OK)
            return;

        var connection = dialogModel.ConnectionString.Trim();
        if (connection.Length == 0)
        {
            await this.Extensibility.Shell().ShowPromptAsync(
                "A target connection string is required (the plan is a diff against the live target). " +
                "For an offline create-script preview use `pgproj script` or a dry-run Publish via the SDK.",
                PromptOptions.OK,
                cancellationToken);
            return;
        }

        try
        {
            // Profile (options + variables). Dialog values beat the profile, the profile beats the
            // project defaults — the same precedence the CLI applies to its flags.
            PublishProfile? profile = null;
            var profilePath = dialogModel.ProfilePath.Trim();
            if (profilePath.Length > 0)
            {
                if (!File.Exists(profilePath))
                {
                    await this.Extensibility.Shell().ShowPromptAsync(
                        $"Publish profile not found: {profilePath}", PromptOptions.OK, cancellationToken);
                    return;
                }
                profile = PublishProfile.Load(profilePath);
                await WriteLineAsync($"Using publish profile {profilePath}.", cancellationToken);
            }

            var options = new PublishPlanOptions
            {
                AllowDrops = dialogModel.AllowDrops || profile?.Options.AllowDrops == true,
                WrapInTransaction = !dialogModel.NoTransaction && profile?.Options.WrapInTransaction != false,
                ProfileVariables = profile?.Variables,
                VariableOverrides = PgProjEngine.ParseVariableOverrides(dialogModel.Variables),
            };

            await WriteLineAsync($"Building '{Path.GetFileName(project)}'…", cancellationToken);
            var (databaseProject, model) = await PgProjEngine.LoadProjectAsync(project, cancellationToken);

            // Same gates the CLI publish runs before touching the database.
            var gate = PgProjEngine.RunGates(databaseProject);
            if (gate.Blocked)
            {
                foreach (var message in gate.Messages)
                    await WriteLineAsync(message, cancellationToken);
                await this.Extensibility.Shell().ShowPromptAsync(
                    "Publish blocked by a gate. See the 'PgProj Publish' Output window.", PromptOptions.OK, cancellationToken);
                return;
            }

            await WriteLineAsync("Comparing against the target…", cancellationToken);
            var plan = await PgProjEngine.PlanAsync(databaseProject, model, connection, options, cancellationToken);

            if (plan.NothingToDo)
            {
                await WriteLineAsync("Nothing to publish — target already matches the project.", cancellationToken);
                await this.Extensibility.Shell().ShowPromptAsync(
                    "Nothing to publish — the target already matches the project.", PromptOptions.OK, cancellationToken);
                return;
            }

            if (dialogModel.GenerateScriptOnly)
            {
                // The dry-run shape: write the deploy script (leading '_' keeps the CLI globber from
                // re-parsing it as a schema source) and open it in the editor instead of executing.
                var scriptPath = Path.Combine(
                    Path.GetDirectoryName(project)!, "bin",
                    "_" + Path.GetFileNameWithoutExtension(project) + ".deploy.sql");
                Directory.CreateDirectory(Path.GetDirectoryName(scriptPath)!);
                await File.WriteAllTextAsync(scriptPath, plan.Script, cancellationToken);
                await WriteLineAsync($"Deploy script written: {scriptPath}", cancellationToken);
                await this.Extensibility.Documents().OpenTextDocumentAsync(new Uri(scriptPath), cancellationToken);
                return;
            }

            var confirm = await this.Extensibility.Shell().ShowPromptAsync(
                $"Publish '{Path.GetFileName(project)}': {plan.ChangeCount} change(s)" +
                (plan.DestructiveCount > 0 ? $", including {plan.DestructiveCount} DESTRUCTIVE. Continue?" : ". Continue?"),
                PromptOptions.OKCancel,
                cancellationToken);
            if (!confirm)
            {
                await WriteLineAsync("Publish cancelled.", cancellationToken);
                return;
            }

            await WriteLineAsync($"Applying {plan.ChangeCount} change(s)…", cancellationToken);
            await PgProjEngine.ApplyAsync(plan, connection, cancellationToken);

            await WriteLineAsync("Publish completed.", cancellationToken);
            await this.Extensibility.Shell().ShowPromptAsync("Publish completed.", PromptOptions.OK, cancellationToken);
        }
        catch (Exception ex)
        {
            await WriteLineAsync($"Publish failed: {ex.Message}", cancellationToken);
            await this.Extensibility.Shell().ShowPromptAsync(
                $"Publish failed: {ex.Message} See the 'PgProj Publish' Output window.", PromptOptions.OK, cancellationToken);
        }
    }

    private Task WriteLineAsync(string text, CancellationToken cancellationToken)
        => this.outputChannel is { } channel ? channel.WriteLineAsync(text) : Task.CompletedTask;
}

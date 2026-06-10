// EP-VS #25 Route B (modern). "Publish" command — compares the selected .pgproj to the target and
// applies the deploy script IN-PROCESS via the engine (PgProj.Core), streaming progress to an Output channel.
using Microsoft.VisualStudio.Extensibility;
using Microsoft.VisualStudio.Extensibility.Commands;
using Microsoft.VisualStudio.Extensibility.Documents;
using Microsoft.VisualStudio.Extensibility.Shell;
using PgProj.VisualStudio.Engine;

namespace PgProj.VisualStudio.Commands;

/// <summary>
/// Publishes the selected <c>.pgproj</c> to a live PostgreSQL server. It compares the project to the
/// target (read-only) to show the change/destructive counts, confirms, then applies the engine's deploy
/// script. All comparison + deploy logic is the engine's (<see cref="PgProjEngine"/>); the connection
/// comes from <c>PGPROJ_CONNECTION</c> (never stored in the project).
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
        Placements = [CommandPlacement.KnownPlacements.ExtensionsMenu],
        Icon = new(ImageMoniker.KnownValues.Extension, IconSettings.IconAndText),
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

        var connection = PgProjContext.ResolveConnection();
        if (connection is null)
        {
            await this.Extensibility.Shell().ShowPromptAsync(
                $"No publish connection is set. Define the {PgProjContext.ConnectionEnvVar} environment variable " +
                "with your target PostgreSQL connection string, then try again.",
                PromptOptions.OK,
                cancellationToken);
            return;
        }

        try
        {
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
            var plan = await PgProjEngine.PlanAsync(databaseProject, model, connection, cancellationToken);

            if (plan.NothingToDo)
            {
                await WriteLineAsync("Nothing to publish — target already matches the project.", cancellationToken);
                await this.Extensibility.Shell().ShowPromptAsync(
                    "Nothing to publish — the target already matches the project.", PromptOptions.OK, cancellationToken);
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

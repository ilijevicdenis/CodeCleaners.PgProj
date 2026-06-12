// EP-VS #25 Route B (modern) + #116. Remote UI view models for the Schema Compare tool window —
// an interactive session: source/target pickers, a checkable diff, and Script/Apply actions over
// the engine's selectable change set (SchemaChangeSet/SelectableChange — selection and scripting
// are engine features, the window only renders them).
using System.Runtime.Serialization;
using Microsoft.VisualStudio.Extensibility;
using Microsoft.VisualStudio.Extensibility.Documents;
using Microsoft.VisualStudio.Extensibility.Shell;
using Microsoft.VisualStudio.Extensibility.UI;
using PgProj.Core.Cli;
using PgProj.Core.Comparison;
using PgProj.VisualStudio.Engine;

namespace PgProj.VisualStudio.ToolWindows;

/// <summary>
/// Data context for <see cref="SchemaCompareControl"/> (a Remote UI control). One long-lived session:
/// the command seeds the source/target and runs the first compare; afterwards every action (compare,
/// include/exclude, generate script, apply) runs from the window itself.
/// </summary>
[DataContract]
internal sealed class SchemaCompareViewModel : NotifyPropertyChangedObject
{
    private readonly VisualStudioExtensibility extensibility;
    private SchemaCompareResult? lastResult;

    private string sourceSpec = string.Empty;
    private string targetSpec = string.Empty;
    private string summary = "Pick a source and a target, then Compare.";
    private bool isBusy;

    public SchemaCompareViewModel(VisualStudioExtensibility extensibility)
    {
        this.extensibility = extensibility;
        this.CompareCommand = new AsyncCommand((_, ct) => this.CompareAsync(ct));
        this.IncludeAllCommand = new AsyncCommand((_, _) => this.SetAllIncludedAsync(true));
        this.ExcludeAllCommand = new AsyncCommand((_, _) => this.SetAllIncludedAsync(false));
        this.GenerateScriptCommand = new AsyncCommand((_, ct) => this.GenerateScriptAsync(ct));
        this.ApplyCommand = new AsyncCommand((_, ct) => this.ApplyAsync(ct));
    }

    /// <summary>The left/source endpoint: a .pgproj, .pgpkg, .schema.snapshot, or a connection string.</summary>
    [DataMember]
    public string SourceSpec
    {
        get => this.sourceSpec;
        set => this.SetProperty(ref this.sourceSpec, value);
    }

    /// <summary>The right/target endpoint: a .pgproj, .pgpkg, .schema.snapshot, or a connection string.</summary>
    [DataMember]
    public string TargetSpec
    {
        get => this.targetSpec;
        set => this.SetProperty(ref this.targetSpec, value);
    }

    /// <summary>The headline/status line (counts, progress, results of the last action).</summary>
    [DataMember]
    public string Summary
    {
        get => this.summary;
        set => this.SetProperty(ref this.summary, value);
    }

    /// <summary>True while an engine operation runs (disables the action buttons).</summary>
    [DataMember]
    public bool IsBusy
    {
        get => this.isBusy;
        set => this.SetProperty(ref this.isBusy, value);
    }

    [DataMember]
    public ObservableList<SchemaChangeViewModel> Changes { get; } = new();

    [DataMember]
    public AsyncCommand CompareCommand { get; }

    [DataMember]
    public AsyncCommand IncludeAllCommand { get; }

    [DataMember]
    public AsyncCommand ExcludeAllCommand { get; }

    [DataMember]
    public AsyncCommand GenerateScriptCommand { get; }

    [DataMember]
    public AsyncCommand ApplyCommand { get; }

    /// <summary>Runs the engine's two-way compare over the current source/target specs.</summary>
    public async Task CompareAsync(CancellationToken cancellationToken)
    {
        var source = this.SourceSpec.Trim();
        var target = this.TargetSpec.Trim();
        if (source.Length == 0 || target.Length == 0)
        {
            this.Summary = "Both a source and a target are required — each a .pgproj, .pgpkg, .schema.snapshot, or a connection string.";
            return;
        }

        this.IsBusy = true;
        this.Summary = "Comparing…";
        try
        {
            var result = await PgProjEngine.CompareAsync(source, target, cancellationToken);
            this.lastResult = result;

            this.Changes.Clear();
            foreach (var change in result.ChangeSet.Changes)
            {
                this.Changes.Add(new SchemaChangeViewModel
                {
                    Id = change.Id,
                    Marker = change.IsDestructive ? "!" : "+",
                    ObjectType = change.ObjectType,
                    Risk = change.RiskLevel.ToString(),
                    Description = change.Description,
                    IsIncluded = change.Included,
                });
            }

            this.Summary = result.ChangeSet.InSync
                ? $"{result.Source.DisplayName} → {result.Target.DisplayName}: in sync."
                : $"{result.Source.DisplayName} → {result.Target.DisplayName}: {result.ChangeSet.Count} change(s), {result.ChangeSet.DestructiveCount} destructive.";

            // A live target that just compared successfully is worth remembering for the source
            // project, so every PgProj dialog prefills it next time (enter the connection once).
            if (result.Target.Kind == EndpointKind.LiveDatabase &&
                source.EndsWith(".pgproj", StringComparison.OrdinalIgnoreCase) && File.Exists(source))
            {
                ConnectionStore.Save(source, target);
            }
        }
        catch (Exception ex)
        {
            this.lastResult = null;
            this.Changes.Clear();
            this.Summary = $"Compare failed: {ex.Message}";
        }
        finally
        {
            this.IsBusy = false;
        }
    }

    private Task SetAllIncludedAsync(bool included)
    {
        foreach (var change in this.Changes)
            change.IsIncluded = included;
        return Task.CompletedTask;
    }

    /// <summary>
    /// Scripts the checked subset (the engine's <c>ScriptIncluded</c>) into
    /// <c>bin/_&lt;source&gt;.compare.sql</c> next to the source project (temp dir otherwise) and opens it.
    /// </summary>
    private async Task GenerateScriptAsync(CancellationToken cancellationToken)
    {
        var changeSet = this.SyncSelection();
        if (changeSet is null)
            return;

        this.IsBusy = true;
        try
        {
            var script = PgProjEngine.ScriptIncluded(changeSet);
            var scriptPath = this.ScriptOutputPath();
            Directory.CreateDirectory(Path.GetDirectoryName(scriptPath)!);
            await File.WriteAllTextAsync(scriptPath, script, cancellationToken);
            this.Summary = $"Script for {changeSet.IncludedCount} included change(s): {scriptPath}";
            await this.extensibility.Documents().OpenTextDocumentAsync(new Uri(scriptPath), cancellationToken);
        }
        catch (Exception ex)
        {
            this.Summary = $"Generate script failed: {ex.Message}";
        }
        finally
        {
            this.IsBusy = false;
        }
    }

    /// <summary>Applies the checked subset to the live target database (confirms first).</summary>
    private async Task ApplyAsync(CancellationToken cancellationToken)
    {
        var changeSet = this.SyncSelection();
        if (changeSet is null)
            return;

        if (this.lastResult is not { Target.Kind: EndpointKind.LiveDatabase })
        {
            this.Summary = "Apply needs a live database target — the target endpoint is a file. (Generate Script works for any target.)";
            return;
        }

        if (changeSet.IncludedCount == 0)
        {
            this.Summary = "Nothing is included — check the changes to apply.";
            return;
        }

        var destructive = changeSet.Included.Count(c => c.IsDestructive);
        var confirm = await this.extensibility.Shell().ShowPromptAsync(
            $"Apply {changeSet.IncludedCount} included change(s) to the target database" +
            (destructive > 0 ? $", including {destructive} DESTRUCTIVE?" : "?"),
            PromptOptions.OKCancel,
            cancellationToken);
        if (!confirm)
            return;

        this.IsBusy = true;
        this.Summary = $"Applying {changeSet.IncludedCount} change(s)…";
        try
        {
            await PgProjEngine.ApplyIncludedAsync(changeSet, this.TargetSpec.Trim(), cancellationToken);
            this.Summary = $"Applied {changeSet.IncludedCount} change(s). Re-comparing…";
            await this.CompareAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            this.Summary = $"Apply failed: {ex.Message}";
        }
        finally
        {
            this.IsBusy = false;
        }
    }

    /// <summary>Pushes the window's checkboxes back into the engine change set (selection by stable id).</summary>
    private SchemaChangeSet? SyncSelection()
    {
        if (this.lastResult is null)
        {
            this.Summary = "Run a compare first.";
            return null;
        }

        var includedIds = this.Changes.Where(c => c.IsIncluded).Select(c => c.Id);
        return this.lastResult.ChangeSet.ApplySelection(includedIds);
    }

    private string ScriptOutputPath()
    {
        var source = this.SourceSpec.Trim();
        if (source.EndsWith(".pgproj", StringComparison.OrdinalIgnoreCase) && File.Exists(source))
        {
            // Leading '_' keeps the CLI's source globber from re-parsing the script as schema source.
            return Path.Combine(
                Path.GetDirectoryName(source)!, "bin",
                "_" + Path.GetFileNameWithoutExtension(source) + ".compare.sql");
        }
        return Path.Combine(Path.GetTempPath(), "pgproj_compare_" + Guid.NewGuid().ToString("N") + ".sql");
    }
}

/// <summary>One row in the change list: include checkbox, destructive marker, kind, risk, description.</summary>
[DataContract]
internal sealed class SchemaChangeViewModel : NotifyPropertyChangedObject
{
    private bool included;

    /// <summary>The engine's stable change id (selection round-trips by id).</summary>
    [DataMember]
    public string Id { get; init; } = string.Empty;

    /// <summary><c>!</c> for destructive (drop/recreate) changes, <c>+</c> otherwise.</summary>
    [DataMember]
    public string Marker { get; init; } = string.Empty;

    /// <summary>The object type (e.g. <c>table</c>, <c>index</c>).</summary>
    [DataMember]
    public string ObjectType { get; init; } = string.Empty;

    /// <summary>The engine's risk verdict for the change (Safe … Blocking).</summary>
    [DataMember]
    public string Risk { get; init; } = string.Empty;

    [DataMember]
    public string Description { get; init; } = string.Empty;

    /// <summary>Whether the change is part of Script/Apply (bound to the row checkbox).</summary>
    [DataMember]
    public bool IsIncluded
    {
        get => this.included;
        set => this.SetProperty(ref this.included, value);
    }
}

// EP-VS #25 Route B (modern). Remote UI view models for the Schema Compare tool window.
using System.Runtime.Serialization;
using Microsoft.VisualStudio.Extensibility.UI;
using PgProj.Core.Comparison;

namespace PgProj.VisualStudio.ToolWindows;

/// <summary>Builds the tool-window view model directly from the engine's compare result (no JSON).</summary>
internal static class SchemaCompareViewModelFactory
{
    public static SchemaCompareViewModel From(string projectName, SchemaCompareResult result)
    {
        var changeSet = result.ChangeSet;
        var viewModel = new SchemaCompareViewModel
        {
            Summary = changeSet.InSync
                ? $"{projectName}: in sync — the target already matches the project."
                : $"{projectName}: {changeSet.Count} change(s) to apply, {changeSet.DestructiveCount} destructive.",
        };

        foreach (var change in changeSet.Changes)
        {
            viewModel.Changes.Add(new SchemaChangeViewModel
            {
                Marker = change.IsDestructive ? "!" : "+",
                ObjectType = change.ObjectType,
                Description = change.Description,
            });
        }

        return viewModel;
    }
}

/// <summary>Data context for <see cref="SchemaCompareControl"/> (a Remote UI control).</summary>
[DataContract]
internal sealed class SchemaCompareViewModel : NotifyPropertyChangedObject
{
    [DataMember]
    public string Summary { get; init; } = string.Empty;

    [DataMember]
    public ObservableList<SchemaChangeViewModel> Changes { get; init; } = new();
}

/// <summary>One row in the change list: a destructive marker, the change kind, and a description.</summary>
[DataContract]
internal sealed class SchemaChangeViewModel : NotifyPropertyChangedObject
{
    /// <summary><c>!</c> for destructive (drop/recreate) changes, <c>+</c> otherwise.</summary>
    [DataMember]
    public string Marker { get; init; } = string.Empty;

    /// <summary>The change kind discriminator (e.g. <c>CreateTableChange</c>).</summary>
    [DataMember]
    public string ObjectType { get; init; } = string.Empty;

    [DataMember]
    public string Description { get; init; } = string.Empty;
}

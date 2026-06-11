// EP-VS #25 Route B (modern) + #116. The interactive Schema Compare tool window.
using Microsoft.VisualStudio.Extensibility;
using Microsoft.VisualStudio.Extensibility.ToolWindows;
using Microsoft.VisualStudio.RpcContracts.RemoteUI;

namespace PgProj.VisualStudio.ToolWindows;

/// <summary>
/// Hosts the Schema Compare session UI (pickers + checkable diff + Script/Apply). All engine work
/// runs from the session view model (<see cref="SchemaCompareViewModel"/>) held by
/// <see cref="SchemaCompareState"/>; <see cref="Commands.SchemaCompareCommand"/> seeds it. The
/// framework owns the lifetime of the returned <see cref="IRemoteUserControl"/>, so no manual
/// disposal is needed.
/// </summary>
[VisualStudioContribution]
internal sealed class SchemaCompareToolWindow : ToolWindow
{
    public SchemaCompareToolWindow()
    {
        this.Title = "PgProj Schema Compare";
    }

    /// <inheritdoc/>
    public override ToolWindowConfiguration ToolWindowConfiguration => new()
    {
        Placement = ToolWindowPlacement.DocumentWell,
    };

    /// <inheritdoc/>
    public override Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc/>
    public override Task<IRemoteUserControl> GetContentAsync(CancellationToken cancellationToken)
        => Task.FromResult<IRemoteUserControl>(new SchemaCompareControl(SchemaCompareState.GetOrCreate(this.Extensibility)));
}

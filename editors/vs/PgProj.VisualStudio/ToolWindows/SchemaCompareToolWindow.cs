// EP-VS #25 Route B (modern). The Schema Compare tool window (renders the engine's diff).
using Microsoft.VisualStudio.Extensibility;
using Microsoft.VisualStudio.Extensibility.ToolWindows;
using Microsoft.VisualStudio.RpcContracts.RemoteUI;

namespace PgProj.VisualStudio.ToolWindows;

/// <summary>
/// Hosts the Schema Compare UI. Pure presentation over the engine's change set: the source/target compare
/// runs in the engine via <see cref="Commands.SchemaCompareCommand"/>; this window renders the latest
/// result held by <see cref="SchemaCompareState"/>. The framework owns the lifetime of the returned
/// <see cref="IRemoteUserControl"/>, so no manual disposal is needed.
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
        => Task.FromResult<IRemoteUserControl>(new SchemaCompareControl(SchemaCompareState.Latest));
}

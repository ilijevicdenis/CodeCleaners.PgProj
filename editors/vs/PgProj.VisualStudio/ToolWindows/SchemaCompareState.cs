// EP-VS #25 Route B (modern) + #116. The shared Schema Compare session between the command and the
// tool window.
using Microsoft.VisualStudio.Extensibility;

namespace PgProj.VisualStudio.ToolWindows;

/// <summary>
/// Holds the single Schema Compare session (<see cref="SchemaCompareViewModel"/>) so the command can
/// seed it (source/target + first compare) and the tool window renders the same live instance when it
/// opens. (A shared singleton is the simplest seam between a command and a tool window — they are
/// separate contributions.)
/// </summary>
internal static class SchemaCompareState
{
    private static readonly Lock Gate = new();
    private static SchemaCompareViewModel? session;

    /// <summary>The session view model, created on first use.</summary>
    public static SchemaCompareViewModel GetOrCreate(VisualStudioExtensibility extensibility)
    {
        lock (Gate)
        {
            return session ??= new SchemaCompareViewModel(extensibility);
        }
    }
}

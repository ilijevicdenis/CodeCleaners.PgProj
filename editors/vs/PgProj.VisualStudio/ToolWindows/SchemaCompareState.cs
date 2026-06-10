// EP-VS #25 Route B (modern). The hand-off between the Schema Compare command and its tool window.
namespace PgProj.VisualStudio.ToolWindows;

/// <summary>
/// Holds the most recent Schema Compare view model so the tool window can render it when it opens. The
/// command builds the view model (from the engine's change set) and stores it here, then calls
/// <c>ShowToolWindowAsync</c>; the window reads <see cref="Latest"/> on <c>GetContentAsync</c>. (A shared
/// singleton is the simplest seam between a command and a tool window — they are separate contributions.)
/// </summary>
internal static class SchemaCompareState
{
    private static readonly Lock Gate = new();

    private static SchemaCompareViewModel latest = new()
    {
        Summary = "No schema comparison has been run yet.",
    };

    public static void SetLatest(SchemaCompareViewModel viewModel)
    {
        lock (Gate)
        {
            latest = viewModel;
        }
    }

    public static SchemaCompareViewModel Latest
    {
        get
        {
            lock (Gate)
            {
                return latest;
            }
        }
    }
}

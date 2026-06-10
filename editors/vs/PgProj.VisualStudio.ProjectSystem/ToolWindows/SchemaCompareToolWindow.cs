// EP-VS #25 Route B — Schema Compare tool window. SCAFFOLD (requires the VS SDK to build).
using System.Runtime.InteropServices;
using Microsoft.VisualStudio.Shell;

namespace PgProj.VisualStudio.ToolWindows
{
    /// <summary>
    /// Hosts the Schema Compare UI: source/target pickers, the change list (add/drop/alter, with
    /// destructive items flagged), and Apply. Backed entirely by the engine's structured diff JSON
    /// (SchemaCompareReport from `pgproj compare … --format json`). This window is pure presentation.
    /// </summary>
    [Guid(PgProjGuids.SchemaCompareWindowGuidString)]
    public sealed class SchemaCompareToolWindow : ToolWindowPane
    {
        public SchemaCompareToolWindow() : base(null)
        {
            Caption = "PgProj Schema Compare";
            // SCAFFOLD: Content = new SchemaCompareControl();  (a WPF view over the diff JSON)
        }
    }
}

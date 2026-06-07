// EP-VS #25 Route B — "SQLCMD variables" property page. SCAFFOLD (requires the VS SDK to build).
using System.Runtime.InteropServices;
using Microsoft.VisualStudio.Shell;

namespace PgProj.VisualStudio.Properties
{
    /// <summary>
    /// Grid of SQLCMD variables and their default values, persisted as
    /// &lt;SqlCmdVariable Include="Name"&gt;&lt;DefaultValue&gt;…&lt;/DefaultValue&gt;&lt;/SqlCmdVariable&gt;
    /// items in the .pgproj — the exact shape the engine's variable resolver reads (EP-VARS). The
    /// Publish command forwards overrides via `--var Name=Value`.
    /// </summary>
    [Guid("b0000000-0000-0000-0000-0000000000b3")]
    public sealed class PgProjSqlCmdVariablesPage : DialogPage
    {
        // SCAFFOLD: a DataGridView-backed page editing <SqlCmdVariable> items.
    }
}

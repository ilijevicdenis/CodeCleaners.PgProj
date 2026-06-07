// EP-VS #25 Route B — "Target platform" property page. SCAFFOLD (requires the VS SDK to build).
using System.Runtime.InteropServices;
using Microsoft.VisualStudio.Shell;

namespace PgProj.VisualStudio.Properties
{
    /// <summary>
    /// Target PostgreSQL major version (TargetPostgresVersion). Drives the engine's target-platform
    /// gate (EP-TARGET): the build/publish refuses syntax newer than the selected version. A dropdown
    /// of supported majors (e.g. 14–18) persisted to the .pgproj &lt;PropertyGroup&gt;.
    /// </summary>
    [Guid("b0000000-0000-0000-0000-0000000000b4")]
    public sealed class PgProjTargetPlatformPage : DialogPage
    {
        // SCAFFOLD:
        // public string TargetPostgresVersion { get; set; }   // "14".."18"
    }
}

// EP-VS #25 Route B — "Build" property page. SCAFFOLD (requires the VS SDK to build).
using System.Runtime.InteropServices;
using Microsoft.VisualStudio.Shell;

namespace PgProj.VisualStudio.Properties
{
    /// <summary>
    /// Build output settings. Edits and persists MSBuild properties in the .pgproj that the Route-A
    /// SDK already understands: Name, the model/.pgpkg output paths (PgProjOutput / PgProjPackage),
    /// and EnableDefaultSqlItems. Persistence = write the &lt;PropertyGroup&gt; values back to the
    /// project file (the same XML the engine and SDK read).
    /// </summary>
    [Guid("b0000000-0000-0000-0000-0000000000b1")]
    public sealed class PgProjBuildPropertiesPage : DialogPage
    {
        // SCAFFOLD: real fields bind to project properties via the project's IVsBuildPropertyStorage.
        // public string Name { get; set; }
        // public string OutputModelPath { get; set; }      // PgProjOutput
        // public string OutputPackagePath { get; set; }    // PgProjPackage
        // public bool EnableDefaultSqlItems { get; set; }
    }
}

// EP-VS #25 Route B — "Database settings" property page. SCAFFOLD (requires the VS SDK to build).
using System.Runtime.InteropServices;
using Microsoft.VisualStudio.Shell;

namespace PgProj.VisualStudio.Properties
{
    /// <summary>
    /// Database / publish settings: DefaultSchema, the publish connection (PgProjPublishConnection —
    /// stored as a non-committed user setting / .pgpublish.json reference, never persisted in the
    /// committed .pgproj), the publish profile path (PgProjPublishProfile), and AllowDrops. These map
    /// to the Route-A SDK Publish properties so the Publish command and this page share one contract.
    /// </summary>
    [Guid("b0000000-0000-0000-0000-0000000000b2")]
    public sealed class PgProjDatabasePropertiesPage : DialogPage
    {
        // SCAFFOLD:
        // public string DefaultSchema { get; set; }
        // public string PublishConnection { get; set; }   // PgProjPublishConnection (user-scoped)
        // public string PublishProfile { get; set; }       // PgProjPublishProfile (.pgpublish.json)
        // public bool AllowDrops { get; set; }              // PgProjPublishAllowDrops
    }
}

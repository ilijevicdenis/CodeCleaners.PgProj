// EP-VS #25 Route B — the flavored .pgproj instance + Solution Explorer object tree. SCAFFOLD.
using System;
using System.Collections.Generic;
using Microsoft.VisualStudio.Shell.Flavor;

namespace PgProj.VisualStudio.ProjectSystem
{
    /// <summary>
    /// A single open .pgproj. Owns the Solution Explorer presentation (the database object tree) and
    /// the property-page registration. The object tree is built from the project MODEL produced by
    /// the engine — the same JSON `pgproj model-tree --format json` (or the LSP model tree) the VS
    /// Code projects panel uses (#24 / EP-RPC). No re-parsing here: the tree is a view over that JSON.
    /// </summary>
    public sealed class PgProjUnconfiguredProject : FlavoredProjectBase
    {
        private readonly PgProjPackage _package;

        public PgProjUnconfiguredProject(PgProjPackage package) => _package = package;

        /// <summary>
        /// Property pages shown on the project's Properties dialog. Mirrors the SSDT property-page set
        /// the task calls for: build output, database settings, SQLCMD variables, target platform.
        /// </summary>
        public IReadOnlyList<Guid> GetPropertyPageGuids() => new[]
        {
            typeof(Properties.PgProjBuildPropertiesPage).GUID,
            typeof(Properties.PgProjDatabasePropertiesPage).GUID,
            typeof(Properties.PgProjSqlCmdVariablesPage).GUID,
            typeof(Properties.PgProjTargetPlatformPage).GUID,
        };

        // TODO (scaffold): override the hierarchy to project the model tree (schemas → tables/views/…)
        // into Solution Explorer, sourced from `pgproj model-tree --format json`.
    }
}

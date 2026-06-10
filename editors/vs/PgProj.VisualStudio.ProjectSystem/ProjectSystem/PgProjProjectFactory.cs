// EP-VS #25 Route B — the .pgproj project factory. SCAFFOLD (requires the VS SDK to build).
using System;
using System.Runtime.InteropServices;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Flavor;

namespace PgProj.VisualStudio.ProjectSystem
{
    /// <summary>
    /// Project factory for *.pgproj. Registered by <see cref="PgProjPackage"/> so VS shows a
    /// PostgreSQL Database Project node in Solution Explorer with the standard verbs (Build, Rebuild,
    /// Clean, Publish). Because the MSBuild SDK (PgProj.Sdk, Route A) already provides those targets,
    /// this factory's job is the UI/flavor: the object tree, properties pages, and the Publish/Schema
    /// Compare context-menu commands — it delegates Build/Clean to MSBuild.
    ///
    /// The modern way is a CPS (Common Project System) project type; this scaffold sketches the
    /// flavored-project (FlavoredProjectBase) shape since it has fewer moving parts to illustrate.
    /// </summary>
    [Guid(PgProjGuids.ProjectTypeGuidString)]
    public sealed class PgProjProjectFactory : FlavoredProjectFactoryBase
    {
        private readonly PgProjPackage _package;

        public PgProjProjectFactory(PgProjPackage package) => _package = package;

        protected override object PreCreateForOuter(IntPtr outerProjectIUnknown)
            => new PgProjUnconfiguredProject(_package);
    }
}

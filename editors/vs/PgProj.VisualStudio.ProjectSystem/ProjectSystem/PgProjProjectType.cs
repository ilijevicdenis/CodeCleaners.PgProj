// EP-VS #25 Route B — the .pgproj CPS project type (Stage 1 of the SSDT-parity roadmap).
using System.ComponentModel.Composition;
using Microsoft.VisualStudio.ProjectSystem;
using Microsoft.VisualStudio.ProjectSystem.VS;

namespace PgProj.VisualStudio.ProjectSystem
{
    /// <summary>
    /// Declares <c>.pgproj</c> as a real Visual Studio project type via CPS (the Common Project
    /// System). There is no hand-written factory or hierarchy: the registration below points the
    /// project-type GUID at CPS, which loads the MSBuild project (PgProj.Sdk evaluates it), shows
    /// the Solution Explorer tree from the project's items (Build = *.sql, declared by the SDK's
    /// ProjectItemsSchema.xaml rule), and routes Build/Rebuild/Clean/Publish to the SDK's targets.
    /// Database objects are grouped by schema via the folder-per-schema convention the templates
    /// establish (public/, app/, … with files named schema.object.sql).
    /// </summary>
    [Export]
    [AppliesTo(ProjectCapability)]
    [ProjectTypeRegistration(
        projectTypeGuid: PgProjGuids.ProjectTypeGuidString,
        displayName: "PostgreSQL Database Project",
        displayProjectFileExtensions: "PostgreSQL Database Projects (*.pgproj);*.pgproj",
        defaultProjectExtension: ProjectExtension,
        language: Language,
        resourcePackageGuid: PgProjGuids.PackageGuidString,
        PossibleProjectExtensions = ProjectExtension,
        Capabilities = ProjectCapability)]
    internal sealed class PgProjUnconfiguredProject
    {
        /// <summary>The .pgproj file extension (no dot, as the registration expects).</summary>
        internal const string ProjectExtension = "pgproj";

        /// <summary>
        /// The template language: item/project .vstemplate files declare
        /// <c>&lt;ProjectType&gt;PgProj&lt;/ProjectType&gt;</c> to attach to this project type.
        /// </summary>
        internal const string Language = "PgProj";

        /// <summary>
        /// The capability all .pgproj projects carry. Declared both here (registration-applied) and
        /// by PgProj.Sdk's Sdk.props (&lt;ProjectCapability Include="PgProj"/&gt;), so AppliesTo
        /// exports and the SolutionHasProjectCapability UIContext rule key on it.
        /// </summary>
        internal const string ProjectCapability = "PgProj";

        [ImportingConstructor]
        public PgProjUnconfiguredProject(UnconfiguredProject unconfiguredProject)
        {
            UnconfiguredProject = unconfiguredProject;
        }

        /// <summary>The CPS unconfigured project this instance is scoped to.</summary>
        internal UnconfiguredProject UnconfiguredProject { get; }
    }

    /// <summary>Per-configuration MEF scope anchor (canonical CPS project-type boilerplate).</summary>
    [Export]
    [AppliesTo(PgProjUnconfiguredProject.ProjectCapability)]
    internal sealed class PgProjConfiguredProject
    {
        [Import]
        internal ConfiguredProject ConfiguredProject { get; private set; }
    }
}

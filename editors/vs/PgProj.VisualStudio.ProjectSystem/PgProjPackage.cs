// EP-VS #25 Route B — the VS AsyncPackage entry point (classic in-proc, net472).
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.VisualStudio.Shell;
using PgProj.VisualStudio.ProjectSystem.Commands;
using Task = System.Threading.Tasks.Task;

namespace PgProj.VisualStudio
{
    /// <summary>
    /// The project-system package. It is intentionally thin: the .pgproj project type itself is a
    /// CPS contribution (see <c>ProjectSystem/PgProjProjectType.cs</c> — registration attribute +
    /// MEF exports, no factory code in this package). What this package carries:
    ///   * the pkgdef registrations (project type, templates, UIContext rule) harvested from this
    ///     assembly's attributes at build time;
    ///   * the .vsct command table that defines the .pgproj project context-menu group (the OOP
    ///     extension places Publish/Schema Compare into it when present);
    ///   * the IN-PROC "Import Database…" command — in-proc because a local OOP extension cannot be
    ///     installed into the main VS 2026 instance (only F5/Marketplace run its finalizer);
    ///   * the "PgProj project present" UIContext — activated by VS itself (no package load needed)
    ///     when the solution contains a project with the PgProj capability, so database controls
    ///     show only when a PostgreSQL database project is actually open.
    /// </summary>
    [PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
    [InstalledProductRegistration("PgProj — PostgreSQL Database Projects", "PostgreSQL database projects (.pgproj) in Visual Studio: project type, templates, build and publish.", "0.1.0")]
    [Guid(PgProjGuids.PackageGuidString)]
    [ProvideMenuResource("Menus.ctmenu", 1)]
    // Term/expression rule: the context turns on exactly while some loaded project declares the
    // PgProj capability (declared by PgProj.Sdk's Sdk.props and the project-type registration).
    [ProvideUIContextRule(PgProjGuids.PgProjLoadedUIContextGuidString,
        name: "PgProj project present",
        expression: "PgProj",
        termNames: new[] { "PgProj" },
        termValues: new[] { "SolutionHasProjectCapability:PgProj" })]
    public sealed class PgProjPackage : AsyncPackage
    {
        /// <summary>Wires the in-proc menu commands (VS loads the package on first invoke).</summary>
        protected override async Task InitializeAsync(CancellationToken cancellationToken, System.IProgress<ServiceProgressData> progress)
        {
            await base.InitializeAsync(cancellationToken, progress);
            await ImportDatabaseCommand.InitializeAsync(this);
        }
    }
}

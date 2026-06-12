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
    // RegisterUsing=CodeBase is REQUIRED for a VSIX-deployed package: the default (Assembly) emits
    // "Assembly"="<name>, Version=..." into the pkgdef and the CLR then resolves by name via the
    // GAC/probing paths, which do NOT include the extension folder — package creation dies with
    // FileNotFoundException ("The 'PgProjPackage' package did not load correctly"). CodeBase emits
    // "CodeBase"="$PackageFolder$\PgProj.VisualStudio.ProjectSystem.dll" instead.
    [PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true, RegisterUsing = RegistrationMethod.CodeBase)]
    [InstalledProductRegistration("PgProj — PostgreSQL Database Projects", "PostgreSQL database projects (.pgproj) in Visual Studio: project type, templates, build and publish.", "0.1.0")]
    [Guid(PgProjGuids.PackageGuidString)]
    [ProvideMenuResource("Menus.ctmenu", 1)]
    // Autoload when any solution opens: the Import Database button is DefaultInvisible, and while
    // this package is NOT loaded its visibility depends entirely on the VSCT VisibilityConstraints
    // (the a4 SolutionHasProjectCapability UIContext) — a chain with several silent failure points
    // (capability not surfacing, stale cto cache). Once the package IS loaded, the command's own
    // BeforeQueryStatus governs (selected node's file extension via DTE — no capability machinery),
    // which is the behavior we actually want. The package is thin, so the load is cheap.
    [ProvideAutoLoad(Microsoft.VisualStudio.VSConstants.UICONTEXT.SolutionExists_string, PackageAutoLoadFlags.BackgroundLoad)]
    // Term/expression rule: the context turns on exactly while some loaded project declares the
    // PgProj capability (declared by PgProj.Sdk's Sdk.props and the project-type registration).
    [ProvideUIContextRule(PgProjGuids.PgProjLoadedUIContextGuidString,
        name: "PgProj project present",
        expression: "PgProj",
        termNames: new[] { "PgProj" },
        termValues: new[] { "SolutionHasProjectCapability:PgProj" })]
    // The PostgreSQL editor for .sql files in PgProj projects. Registered for the extension at a
    // higher priority than the built-in T-SQL editor; the factory itself claims only files whose
    // owning project is the PgProj type and declines the rest (VS_E_UNSUPPORTEDFORMAT → the shell
    // falls through), so the global registration is safe for SSDT projects and loose .sql files.
    [ProvideEditorFactory(typeof(ProjectSystem.Editors.PgSqlEditorFactory), 110)]
    [ProvideEditorLogicalView(typeof(ProjectSystem.Editors.PgSqlEditorFactory), Microsoft.VisualStudio.VSConstants.LOGVIEWID.TextView_string)]
    [ProvideEditorExtension(typeof(ProjectSystem.Editors.PgSqlEditorFactory), ".sql", 1000)]
    public sealed class PgProjPackage : AsyncPackage
    {
        /// <summary>Wires the in-proc menu commands and the PostgreSQL editor factory.</summary>
        protected override async Task InitializeAsync(CancellationToken cancellationToken, System.IProgress<ServiceProgressData> progress)
        {
            await base.InitializeAsync(cancellationToken, progress);
            await JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
            RegisterEditorFactory(new ProjectSystem.Editors.PgSqlEditorFactory());
            await ImportDatabaseCommand.InitializeAsync(this);
        }
    }
}

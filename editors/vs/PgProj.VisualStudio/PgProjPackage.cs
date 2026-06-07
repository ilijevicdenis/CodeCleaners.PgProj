// EP-VS #25 Route B — the VS AsyncPackage entry point. SCAFFOLD (requires the VS SDK to build).
using System;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.VisualStudio.Shell;
using Task = System.Threading.Tasks.Task;

namespace PgProj.VisualStudio
{
    /// <summary>
    /// The extension's package. Registers the .pgproj project factory, the commands (Publish /
    /// Schema Compare), the properties pages, and the Schema Compare tool window. Loads on solution
    /// open and when a .pgproj is present.
    /// </summary>
    [PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
    [InstalledProductRegistration("PgProj — PostgreSQL Database Projects", "Open/build/publish .pgproj in Visual Studio.", "0.1.0")]
    [Guid(PgProjGuids.PackageGuidString)]
    [ProvideMenuResource("Menus.ctmenu", 1)]
    // The .pgproj project factory (Route B project system). The actual factory is in ProjectSystem/.
    [ProvideProjectFactory(
        typeof(PgProj.VisualStudio.ProjectSystem.PgProjProjectFactory),
        "PgProj PostgreSQL Database Project",
        "PostgreSQL Database Projects (*.pgproj)#100",
        "pgproj", "pgproj",
        @".\NullPath",
        LanguageVsTemplate = "PgProj")]
    // Auto-load when a .pgproj solution is open so the language client + commands are available.
    [ProvideAutoLoad(Microsoft.VisualStudio.Shell.Interop.UIContextGuids80.SolutionExists, PackageAutoLoadFlags.BackgroundLoad)]
    [ProvideToolWindow(typeof(PgProj.VisualStudio.ToolWindows.SchemaCompareToolWindow))]
    public sealed class PgProjPackage : AsyncPackage
    {
        protected override async Task InitializeAsync(CancellationToken cancellationToken, IProgress<ServiceProgressData> progress)
        {
            await base.InitializeAsync(cancellationToken, progress);
            await JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);

            // Register the project factory for *.pgproj.
            RegisterProjectFactory(new ProjectSystem.PgProjProjectFactory(this));

            // Wire commands (Publish / Schema Compare).
            await Commands.PublishCommand.InitializeAsync(this);
            await Commands.SchemaCompareCommand.InitializeAsync(this);

            // NOTE: the .sql LanguageClient (LanguageClient/PgProjLanguageClient.cs) is a MEF export
            // and is activated by VS automatically for the registered content type — no code here.
        }
    }
}

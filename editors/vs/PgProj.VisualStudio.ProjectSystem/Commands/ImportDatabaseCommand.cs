// EP-VS — the in-proc "Import Database…" command on the .pgproj project context menu.
// In-proc (not the OOP extension) because a local VisualStudio.Extensibility extension cannot be
// installed into the main VS 2026 instance; the engine is net10 so the work shells out to the
// bundled pgproj CLI (tools\PgProj.Cli.dll inside this VSIX).
using System;
using System.ComponentModel.Design;
using System.IO;
using EnvDTE;
using EnvDTE80;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Task = System.Threading.Tasks.Task;

namespace PgProj.VisualStudio.ProjectSystem.Commands
{
    /// <summary>
    /// "Import Database (PgProj)…": shows the WPF import dialog (connection + checkable object list)
    /// for the selected <c>.pgproj</c> and writes the chosen objects into the project as .sql files
    /// (the SDK's <c>**/*.sql</c> auto-glob makes them Build items immediately).
    /// </summary>
    internal static class ImportDatabaseCommand
    {
        public static async Task InitializeAsync(AsyncPackage package)
        {
            // OleMenuCommandService must be obtained/used on the UI thread.
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(package.DisposalToken);

            var commandService = (OleMenuCommandService)await package.GetServiceAsync(typeof(IMenuCommandService))
                ?? throw new InvalidOperationException("IMenuCommandService is unavailable.");
            var commandId = new CommandID(new Guid(PgProjGuids.CommandSetGuidString), PgProjGuids.ImportDatabaseCommandId);
            var command = new OleMenuCommand((_, _) => Execute(package), commandId);
            command.BeforeQueryStatus += (sender, _) =>
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                var menuCommand = (OleMenuCommand)sender;
                var visible = TryGetSelectedPgProj() is not null;
                menuCommand.Visible = visible;
                menuCommand.Enabled = visible;
            };
            commandService.AddCommand(command);
        }

        private static void Execute(AsyncPackage package)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            var projectPath = TryGetSelectedPgProj();
            if (projectPath is null)
            {
                VsShellUtilities.ShowMessageBox(package,
                    "Select a PostgreSQL database project (.pgproj) first.", "PgProj Import",
                    OLEMSGICON.OLEMSGICON_INFO, OLEMSGBUTTON.OLEMSGBUTTON_OK, OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_FIRST);
                return;
            }

            var dialog = new ImportDatabaseDialog(projectPath);
            dialog.ShowModal();
        }

        /// <summary>The selected project's full path when it is a <c>.pgproj</c>, else null.</summary>
        private static string TryGetSelectedPgProj()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                if (Package.GetGlobalService(typeof(SDTE)) is not DTE2 dte)
                    return null;

                var selected = dte.SelectedItems;
                if (selected is null || selected.Count != 1)
                    return null;

                var path = selected.Item(1)?.Project?.FullName;
                return path is not null && path.EndsWith(".pgproj", StringComparison.OrdinalIgnoreCase) && File.Exists(path)
                    ? path
                    : null;
            }
            catch
            {
                // Selection APIs can throw during solution transitions — treat as "not a .pgproj".
                return null;
            }
        }
    }
}

// EP-TESTGEN (#157/#161) — the in-proc "Generate Tests (PgProj)…" command on the .pgproj project node.
// In-proc (not the OOP extension) because a local OOP extension cannot be installed into the main VS
// 2026 instance; the engine is net10 so the work shells out to the bundled pgproj CLI
// (tools\PgProj.Cli.dll inside this VSIX), running the `test generate` verb. All the choices —
// database mode (Testcontainers vs an existing server), categories, seed hooks, output — live in
// GenerateTestsDialog, which runs the CLI async off the UI thread.
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
    /// "Generate Tests (PgProj)…": generates a COMPLETE auto-asserted unit + integration test suite for
    /// the selected <c>.pgproj</c> into <c>Tests\Generated\</c>, via <see cref="GenerateTestsDialog"/>.
    /// </summary>
    internal static class GenerateTestsCommand
    {
        public static async Task InitializeAsync(AsyncPackage package)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(package.DisposalToken);

            var commandService = (OleMenuCommandService)await package.GetServiceAsync(typeof(IMenuCommandService))
                ?? throw new InvalidOperationException("IMenuCommandService is unavailable.");
            var commandId = new CommandID(new Guid(PgProjGuids.CommandSetGuidString), PgProjGuids.GenerateTestsCommandId);
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
                    "Select a PostgreSQL database project (.pgproj) first.", "PgProj Generate Tests",
                    OLEMSGICON.OLEMSGICON_INFO, OLEMSGBUTTON.OLEMSGBUTTON_OK, OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_FIRST);
                return;
            }

            new GenerateTestsDialog(projectPath).ShowModal();
        }

        // ---- helpers ----------------------------------------------------------------------------

        /// <summary>The selected project's full path when it is a <c>.pgproj</c>, else null.</summary>
        private static string TryGetSelectedPgProj()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                if (Package.GetGlobalService(typeof(SDTE)) is not DTE2 dte) return null;
                var selected = dte.SelectedItems;
                if (selected is null || selected.Count != 1) return null;
                var path = selected.Item(1)?.Project?.FullName;
                return path is not null && path.EndsWith(".pgproj", StringComparison.OrdinalIgnoreCase) && File.Exists(path)
                    ? path
                    : null;
            }
            catch
            {
                return null;
            }
        }
    }
}

// EP-VS #25 Route B — "Schema Compare" command. SCAFFOLD (requires the VS SDK to build).
using System;
using System.ComponentModel.Design;
using Microsoft.VisualStudio.Shell;
using Task = System.Threading.Tasks.Task;

namespace PgProj.VisualStudio.Commands
{
    /// <summary>
    /// Opens the Schema Compare tool window for the selected .pgproj. The compare itself is the
    /// engine's two-way comparer (`pgproj compare --source X --target Y -o diff.json --format json`,
    /// EP-SCHEMACOMPARE) — the window renders that structured diff and lets the user pick a target
    /// (project / .pgpkg / .schema.snapshot / live DB) and apply selected changes.
    /// </summary>
    internal sealed class SchemaCompareCommand
    {
        private readonly AsyncPackage _package;

        private SchemaCompareCommand(AsyncPackage package, OleMenuCommandService commandService)
        {
            _package = package;
            var id = new CommandID(PgProjGuids.CommandSet, PgProjGuids.SchemaCompareCommandId);
            commandService.AddCommand(new MenuCommand(Execute, id));
        }

        public static async Task InitializeAsync(AsyncPackage package)
        {
            await package.JoinableTaskFactory.SwitchToMainThreadAsync();
            var commandService = await package.GetServiceAsync(typeof(IMenuCommandService)) as OleMenuCommandService;
            if (commandService != null)
                _ = new SchemaCompareCommand(package, commandService);
        }

        private void Execute(object sender, EventArgs e)
        {
            // SCAFFOLD: show the SchemaCompareToolWindow, seeded with the selected project as source.
        }
    }
}

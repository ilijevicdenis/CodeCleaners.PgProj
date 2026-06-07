// EP-VS #25 Route B — "Publish" context-menu command. SCAFFOLD (requires the VS SDK to build).
using System;
using System.ComponentModel.Design;
using Microsoft.VisualStudio.Shell;
using Task = System.Threading.Tasks.Task;

namespace PgProj.VisualStudio.Commands
{
    /// <summary>
    /// Right-click a .pgproj in Solution Explorer → Publish. Shows a publish dialog (connection /
    /// profile / allow-drops / dry-run) then runs the deploy engine. Implementation simply invokes
    /// the Route-A MSBuild Publish target (`msbuild Project.pgproj /t:Publish
    /// /p:PgProjPublishConnection=…`) — or `pgproj publish` directly — so there is ONE publish code
    /// path shared with the CLI/SDK; the dialog only collects the parameters.
    /// </summary>
    internal sealed class PublishCommand
    {
        private readonly AsyncPackage _package;

        private PublishCommand(AsyncPackage package, OleMenuCommandService commandService)
        {
            _package = package;
            var id = new CommandID(PgProjGuids.CommandSet, PgProjGuids.PublishCommandId);
            commandService.AddCommand(new MenuCommand(Execute, id));
        }

        public static async Task InitializeAsync(AsyncPackage package)
        {
            await ((Microsoft.VisualStudio.Shell.IAsyncServiceProvider)package)
                .GetServiceAsync(typeof(IMenuCommandService));
            await package.JoinableTaskFactory.SwitchToMainThreadAsync();
            var commandService = await package.GetServiceAsync(typeof(IMenuCommandService)) as OleMenuCommandService;
            if (commandService != null)
                _ = new PublishCommand(package, commandService);
        }

        private void Execute(object sender, EventArgs e)
        {
            // SCAFFOLD: 1) find the selected .pgproj; 2) show the publish dialog (collect connection /
            // profile / allow-drops / dry-run); 3) run the MSBuild Publish target with those props and
            // stream output to the Output window. No deploy logic here — it lives in the engine.
        }
    }
}

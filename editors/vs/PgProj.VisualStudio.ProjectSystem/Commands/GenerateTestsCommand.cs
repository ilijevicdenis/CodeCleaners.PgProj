// EP-TESTGEN (#157) — the in-proc "Generate Tests (PgProj)…" command on the .pgproj project node.
// In-proc (not the OOP extension) because a local OOP extension cannot be installed into the main VS
// 2026 instance; the engine is net10 so the work shells out to the bundled pgproj CLI
// (tools\PgProj.Cli.dll inside this VSIX), running the `test generate` verb.
using System;
using System.ComponentModel.Design;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using EnvDTE;
using EnvDTE80;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Task = System.Threading.Tasks.Task;

namespace PgProj.VisualStudio.ProjectSystem.Commands
{
    /// <summary>
    /// "Generate Tests (PgProj)…": generates a COMPLETE auto-asserted unit + integration test suite for
    /// the selected <c>.pgproj</c> into <c>Tests\Generated\</c>. Optionally takes a connection to bring the
    /// database up to date first (preserved = incremental, wipeout = drop+recreate) and to run the suite.
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

            var options = PromptForOptions(projectPath);
            if (options is null) return; // cancelled

            // Build the CLI args. The connection is optional (generation alone needs none); it is required
            // to deploy (any mode) or to run.
            var args = $"test generate \"{projectPath}\"";
            if (!string.IsNullOrWhiteSpace(options.Connection))
            {
                args += $" --connection \"{options.Connection}\" --mode {options.Mode}";
                if (options.Mode == "wipeout") args += " --allow-wipeout";
                if (options.Run) args += " --run";
            }

            var result = package.JoinableTaskFactory.Run(() => PgProjCliRunner.RunAsync(args));
            var body = (string.IsNullOrWhiteSpace(result.Output) ? "" : result.Output) +
                       (string.IsNullOrWhiteSpace(result.Error) ? "" : "\n" + result.Error);

            VsShellUtilities.ShowMessageBox(package, Trim(body, 1600),
                result.Success ? "PgProj Generate Tests — done" : "PgProj Generate Tests — failed",
                result.Success ? OLEMSGICON.OLEMSGICON_INFO : OLEMSGICON.OLEMSGICON_CRITICAL,
                OLEMSGBUTTON.OLEMSGBUTTON_OK, OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_FIRST);
        }

        // ---- options dialog ---------------------------------------------------------------------

        private sealed class Options
        {
            public string Connection { get; set; }
            public string Mode { get; set; }     // "preserved" | "wipeout"
            public bool Run { get; set; }
        }

        private static Options PromptForOptions(string projectPath)
        {
            var box = new TextBox
            {
                MinWidth = 420,
                Text = ConnectionStore.TryGet(projectPath) ?? Environment.GetEnvironmentVariable("PGPROJ_CONNECTION") ?? "",
            };
            var preserved = new RadioButton { Content = "Preserved — bring the database up to date (keep data)", IsChecked = true, Margin = new Thickness(0, 6, 0, 0) };
            var wipeout = new RadioButton { Content = "Wipeout — DROP and recreate the database", Margin = new Thickness(0, 2, 0, 0) };
            var run = new CheckBox { Content = "Run the suite after generating", IsChecked = true, Margin = new Thickness(0, 8, 0, 0) };
            var remember = new CheckBox { Content = "Remember connection for this project", IsChecked = true, Margin = new Thickness(0, 2, 0, 0) };

            var ok = new Button { Content = "Generate", Width = 90, Margin = new Thickness(0, 12, 6, 0), IsDefault = true };
            var cancelBtn = new Button { Content = "Cancel", Width = 90, Margin = new Thickness(0, 12, 0, 0), IsCancel = true };
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            buttons.Children.Add(ok);
            buttons.Children.Add(cancelBtn);

            var panel = new StackPanel { Margin = new Thickness(14) };
            panel.Children.Add(new TextBlock
            {
                Text = "Generates a complete auto-asserted test suite into Tests\\Generated\\.\n" +
                       "Leave the connection blank to only generate files (no deploy / run).",
                TextWrapping = TextWrapping.Wrap, MaxWidth = 440, Margin = new Thickness(0, 0, 0, 8),
            });
            panel.Children.Add(new TextBlock { Text = "PostgreSQL connection string (optional):" });
            panel.Children.Add(box);
            panel.Children.Add(preserved);
            panel.Children.Add(wipeout);
            panel.Children.Add(run);
            panel.Children.Add(remember);
            panel.Children.Add(buttons);

            var dialog = new System.Windows.Window
            {
                Title = "PgProj — Generate Tests",
                Content = panel,
                SizeToContent = SizeToContent.WidthAndHeight,
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
                ResizeMode = ResizeMode.NoResize,
                ShowInTaskbar = false,
            };
            var accepted = false;
            ok.Click += (_, _) =>
            {
                if (wipeout.IsChecked == true && !string.IsNullOrWhiteSpace(box.Text) &&
                    MessageBox.Show("Wipeout DROPs and recreates the target database — all its data is lost. Continue?",
                        "PgProj — confirm wipeout", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                    return;
                accepted = true;
                dialog.Close();
            };
            cancelBtn.Click += (_, _) => dialog.Close();
            dialog.ShowDialog();

            if (!accepted) return null;
            var conn = box.Text?.Trim() ?? "";
            if (remember.IsChecked == true && conn.Length > 0) ConnectionStore.Save(projectPath, conn);
            return new Options { Connection = conn, Mode = wipeout.IsChecked == true ? "wipeout" : "preserved", Run = run.IsChecked == true };
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

        private static string Trim(string s, int max) =>
            string.IsNullOrEmpty(s) ? s : s.Length <= max ? s : s.Substring(0, max) + "…";
    }
}

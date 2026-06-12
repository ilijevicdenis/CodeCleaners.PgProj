// EP-VS — the in-proc "Sync with Database (PgProj)…" command on a .sql ITEM's context menu.
// Flow (the git-style review the user expects): inspect the file against the live database
// (bundled CLI, sync-file verb, JSON) → open Visual Studio's built-in side-by-side diff
// (database version LEFT, local file RIGHT) → a floating action window offers the decisions:
// take the database's version into the file, push the local version to the database, or close.
using System;
using System.ComponentModel.Design;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using EnvDTE;
using EnvDTE80;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Task = System.Threading.Tasks.Task;

namespace PgProj.VisualStudio.ProjectSystem.Commands
{
    internal static class SyncFileCommand
    {
        public static async Task InitializeAsync(AsyncPackage package)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(package.DisposalToken);

            var commandService = (OleMenuCommandService)await package.GetServiceAsync(typeof(IMenuCommandService))
                ?? throw new InvalidOperationException("IMenuCommandService is unavailable.");
            var commandId = new CommandID(new Guid(PgProjGuids.CommandSetGuidString), PgProjGuids.SyncFileCommandId);
            var command = new OleMenuCommand((_, _) => Execute(package), commandId);
            command.BeforeQueryStatus += (sender, _) =>
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                var menuCommand = (OleMenuCommand)sender;
                var visible = TryGetSelectedSqlInPgProj() is not null;
                menuCommand.Visible = visible;
                menuCommand.Enabled = visible;
            };
            commandService.AddCommand(command);
        }

        private static void Execute(AsyncPackage package)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var selection = TryGetSelectedSqlInPgProj();
            if (selection is null) return;
            var (projectPath, filePath) = selection.Value;
            var relative = MakeRelative(Path.GetDirectoryName(projectPath), filePath);

            var connection = ConnectionStore.TryGet(projectPath);
            if (string.IsNullOrWhiteSpace(connection))
            {
                connection = PromptForConnection(projectPath);
                if (string.IsNullOrWhiteSpace(connection)) return; // cancelled
            }

            // Inspect via the bundled CLI (net472 host, net10 engine).
            var inspect = package.JoinableTaskFactory.Run(() =>
                PgProjCliRunner.RunAsync($"sync-file \"{projectPath}\" --file \"{relative}\" --connection \"{connection}\""));
            if (!inspect.Success)
            {
                VsShellUtilities.ShowMessageBox(package,
                    "Could not inspect the file against the database:\n\n" + Trim(inspect.Error, 800),
                    "PgProj Sync", OLEMSGICON.OLEMSGICON_CRITICAL, OLEMSGBUTTON.OLEMSGBUTTON_OK, OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_FIRST);
                return;
            }

            var state = ParseState(inspect.Output);
            if (state is null)
            {
                VsShellUtilities.ShowMessageBox(package, "Unexpected sync-file output.", "PgProj Sync",
                    OLEMSGICON.OLEMSGICON_CRITICAL, OLEMSGBUTTON.OLEMSGBUTTON_OK, OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_FIRST);
                return;
            }

            if (state.Status == "Identical")
            {
                VsShellUtilities.ShowMessageBox(package,
                    $"{Path.GetFileName(filePath)} matches the database — nothing to sync.",
                    "PgProj Sync", OLEMSGICON.OLEMSGICON_INFO, OLEMSGBUTTON.OLEMSGBUTTON_OK, OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_FIRST);
                return;
            }

            // The two screens: database version (left, temp file) vs local file (right) in the
            // standard VS diff editor — the same view a git compare uses.
            var dbTemp = Path.Combine(Path.GetTempPath(),
                Path.GetFileNameWithoutExtension(filePath) + ".database" + Path.GetExtension(filePath));
            File.WriteAllText(dbTemp, state.DatabaseText ?? $"-- {state.Summary}\n-- (no longer exists in the database)\n");

            var diff = (IVsDifferenceService)Package.GetGlobalService(typeof(SVsDifferenceService));
            diff?.OpenComparisonWindow2(
                leftFileMoniker: dbTemp,
                rightFileMoniker: filePath,
                caption: $"Sync: {Path.GetFileName(filePath)}",
                Tooltip: state.Summary,
                leftLabel: "Database",
                rightLabel: $"Local: {relative}",
                inlineLabel: $"Database ↔ {Path.GetFileName(filePath)}",
                roles: null,
                grfDiffOptions: 0);

            ShowActionsWindow(package, projectPath, relative, filePath, connection, state.Summary);
        }

        // ---- the decision window --------------------------------------------------------------

        private static void ShowActionsWindow(AsyncPackage package, string projectPath, string relative,
            string filePath, string connection, string summary)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            var takeDb = new Button { Content = "⬇  Update local file (take database version)", Margin = new Thickness(0, 4, 0, 0), Padding = new Thickness(10, 6, 10, 6) };
            var pushDb = new Button { Content = "⬆  Override database (push local version)", Margin = new Thickness(0, 4, 0, 0), Padding = new Thickness(10, 6, 10, 6) };
            var cancel = new Button { Content = "Cancel (just keep the diff open)", Margin = new Thickness(0, 10, 0, 0), Padding = new Thickness(10, 6, 10, 6) };

            var panel = new StackPanel { Margin = new Thickness(14) };
            panel.Children.Add(new TextBlock
            {
                Text = $"{Path.GetFileName(filePath)} differs from the database.\n{summary}",
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 8),
                MaxWidth = 420,
            });
            panel.Children.Add(takeDb);
            panel.Children.Add(pushDb);
            panel.Children.Add(cancel);

            var window = new System.Windows.Window
            {
                Title = "PgProj Sync",
                Content = panel,
                SizeToContent = SizeToContent.WidthAndHeight,
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
                Topmost = true,           // floats above the (modeless) diff so the choices stay reachable
                ResizeMode = ResizeMode.NoResize,
                ShowInTaskbar = false,
            };

            takeDb.Click += (_, _) =>
            {
                var r = package.JoinableTaskFactory.Run(() => PgProjCliRunner.RunAsync(
                    $"sync-file \"{projectPath}\" --file \"{relative}\" --connection \"{connection}\" --apply-to-local"));
                ReportAndClose(window, r.Success,
                    r.Success ? "Local file updated from the database." : Trim(r.Error, 800));
            };

            pushDb.Click += (_, _) =>
            {
                // Non-destructive first; when nothing non-destructive applies the drift must be
                // destructive — re-ask with the explicit warning before --allow-drops.
                var r = package.JoinableTaskFactory.Run(() => PgProjCliRunner.RunAsync(
                    $"sync-file \"{projectPath}\" --file \"{relative}\" --connection \"{connection}\" --apply-to-db"));
                if (r.Success && r.Output.IndexOf("nothing to push", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    var confirm = MessageBox.Show(
                        "Pushing this file requires DESTRUCTIVE database changes (drops). Continue?",
                        "PgProj Sync — destructive changes", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                    if (confirm != MessageBoxResult.Yes) return;
                    r = package.JoinableTaskFactory.Run(() => PgProjCliRunner.RunAsync(
                        $"sync-file \"{projectPath}\" --file \"{relative}\" --connection \"{connection}\" --apply-to-db --allow-drops"));
                }
                ReportAndClose(window, r.Success,
                    r.Success ? "Database updated from the local file." : Trim(r.Error, 800));
            };

            cancel.Click += (_, _) => window.Close();
            window.Show();
        }

        private static void ReportAndClose(System.Windows.Window window, bool success, string message)
        {
            MessageBox.Show(message, success ? "PgProj Sync — done" : "PgProj Sync — failed",
                MessageBoxButton.OK, success ? MessageBoxImage.Information : MessageBoxImage.Error);
            if (success) window.Close();
        }

        // ---- helpers ----------------------------------------------------------------------------

        /// <summary>(projectPath, filePath) when the single selected item is a .sql inside a .pgproj.</summary>
        private static (string ProjectPath, string FilePath)? TryGetSelectedSqlInPgProj()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                if (Package.GetGlobalService(typeof(SDTE)) is not DTE2 dte) return null;
                var selected = dte.SelectedItems;
                if (selected is null || selected.Count != 1) return null;

                var item = selected.Item(1)?.ProjectItem;
                var projectPath = item?.ContainingProject?.FullName;
                if (projectPath is null || !projectPath.EndsWith(".pgproj", StringComparison.OrdinalIgnoreCase))
                    return null;

                var filePath = item.FileNames[1];
                return filePath is not null && filePath.EndsWith(".sql", StringComparison.OrdinalIgnoreCase) && File.Exists(filePath)
                    ? (projectPath, filePath)
                    : ((string, string)?)null;
            }
            catch
            {
                return null;
            }
        }

        private static string PromptForConnection(string projectPath)
        {
            var box = new TextBox { MinWidth = 380, Text = Environment.GetEnvironmentVariable("PGPROJ_CONNECTION") ?? "" };
            var remember = new CheckBox { Content = "Remember connection for this project", IsChecked = true, Margin = new Thickness(0, 6, 0, 0) };
            var ok = new Button { Content = "OK", Width = 80, Margin = new Thickness(0, 10, 6, 0), IsDefault = true };
            var cancelBtn = new Button { Content = "Cancel", Width = 80, Margin = new Thickness(0, 10, 0, 0), IsCancel = true };
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            buttons.Children.Add(ok);
            buttons.Children.Add(cancelBtn);

            var panel = new StackPanel { Margin = new Thickness(12) };
            panel.Children.Add(new TextBlock { Text = "PostgreSQL connection string:" });
            panel.Children.Add(box);
            panel.Children.Add(remember);
            panel.Children.Add(buttons);

            var dialog = new System.Windows.Window
            {
                Title = "PgProj Sync — connection",
                Content = panel,
                SizeToContent = SizeToContent.WidthAndHeight,
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
                ResizeMode = ResizeMode.NoResize,
                ShowInTaskbar = false,
            };
            var accepted = false;
            ok.Click += (_, _) => { accepted = true; dialog.Close(); };
            cancelBtn.Click += (_, _) => dialog.Close();
            dialog.ShowDialog();

            if (!accepted || string.IsNullOrWhiteSpace(box.Text)) return null;
            if (remember.IsChecked == true) ConnectionStore.Save(projectPath, box.Text.Trim());
            return box.Text.Trim();
        }

        [DataContract]
        private sealed class SyncState
        {
            [DataMember(Name = "file")] public string File { get; set; }
            [DataMember(Name = "status")] public string Status { get; set; }
            [DataMember(Name = "summary")] public string Summary { get; set; }
            [DataMember(Name = "localText")] public string LocalText { get; set; }
            [DataMember(Name = "databaseText")] public string DatabaseText { get; set; }
        }

        private static SyncState ParseState(string json)
        {
            try
            {
                using var ms = new MemoryStream(Encoding.UTF8.GetBytes(json));
                return (SyncState)new DataContractJsonSerializer(typeof(SyncState)).ReadObject(ms);
            }
            catch
            {
                return null;
            }
        }

        private static string MakeRelative(string baseDir, string path)
        {
            var baseUri = new Uri(baseDir.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar);
            return Uri.UnescapeDataString(baseUri.MakeRelativeUri(new Uri(path)).ToString()).Replace('\\', '/');
        }

        private static string Trim(string s, int max) =>
            string.IsNullOrEmpty(s) ? s : s.Length <= max ? s : s.Substring(0, max) + "…";
    }
}

// EP-VS — the in-proc WPF "Import Database" dialog (code-built; no XAML compile needed). Flow:
// connection (prefilled from the per-project store, else PGPROJ_CONNECTION) → Load Objects (runs
// the bundled CLI's extract into a temp dir — doubles as the connection test) → checkable object
// list → Import copies the checked .sql files into the project (skip-existing unless overwrite).
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Microsoft.VisualStudio.PlatformUI;
using Microsoft.VisualStudio.Shell;

namespace PgProj.VisualStudio.ProjectSystem.Commands
{
    /// <summary>
    /// Modal import dialog for one <c>.pgproj</c>. All engine work happens in the pgproj CLI
    /// (<see cref="PgProjCliRunner"/>); this window only collects choices and copies files.
    /// </summary>
    internal sealed class ImportDatabaseDialog : DialogWindow
    {
        private readonly string projectPath;
        private readonly string projectDir;
        private string extractDir;

        private readonly TextBox connectionBox = new TextBox { MinWidth = 380, VerticalContentAlignment = VerticalAlignment.Center };
        private readonly Button loadButton = new Button { Content = "Load Objects", Padding = new Thickness(10, 2, 10, 2) };
        private readonly Button includeAllButton = new Button { Content = "Include All", Padding = new Thickness(10, 2, 10, 2), IsEnabled = false };
        private readonly Button excludeAllButton = new Button { Content = "Exclude All", Padding = new Thickness(10, 2, 10, 2), IsEnabled = false };
        private readonly CheckBox overwriteBox = new CheckBox { Content = "Overwrite existing files", VerticalAlignment = VerticalAlignment.Center };
        private readonly CheckBox rememberBox = new CheckBox { Content = "Remember connection", IsChecked = true, VerticalAlignment = VerticalAlignment.Center, ToolTip = "Stores the connection DPAPI-encrypted under your Windows profile (never in the .pgproj)." };
        private readonly TextBlock statusText = new TextBlock { TextWrapping = TextWrapping.Wrap, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 6, 0, 6) };
        private readonly ListBox objectList = new ListBox();
        private readonly Button importButton = new Button { Content = "Import", Padding = new Thickness(14, 3, 14, 3), IsEnabled = false, IsDefault = true };

        public ImportDatabaseDialog(string projectPath)
        {
            this.projectPath = projectPath;
            this.projectDir = Path.GetDirectoryName(projectPath);

            Title = $"Import Database into '{Path.GetFileName(projectPath)}'";
            Width = 640;
            Height = 560;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;

            connectionBox.Text = ConnectionStore.TryGet(projectPath)
                ?? Environment.GetEnvironmentVariable("PGPROJ_CONNECTION")
                ?? string.Empty;
            statusText.Text = "Enter a connection string, then Load Objects.";

            Content = BuildLayout();

            loadButton.Click += OnLoadObjectsClick;
            includeAllButton.Click += (_, _) => SetAllChecked(true);
            excludeAllButton.Click += (_, _) => SetAllChecked(false);
            importButton.Click += (_, _) => Import();
            Closed += (_, _) => TryCleanupExtractDir();
        }

        private UIElement BuildLayout()
        {
            var root = new Grid { Margin = new Thickness(12) };
            for (var i = 0; i < 5; i++)
                root.RowDefinitions.Add(new RowDefinition { Height = i == 3 ? new GridLength(1, GridUnitType.Star) : GridLength.Auto });

            var connectionRow = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
            var label = new TextBlock { Text = "Connection:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
            DockPanel.SetDock(label, Dock.Left);
            connectionRow.Children.Add(label);
            connectionRow.Children.Add(connectionBox);
            Grid.SetRow(connectionRow, 0);
            root.Children.Add(connectionRow);

            var buttonsRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 2) };
            foreach (var element in new UIElement[] { loadButton, includeAllButton, excludeAllButton, overwriteBox, rememberBox })
            {
                if (element is FrameworkElement fe)
                    fe.Margin = new Thickness(0, 0, 8, 0);
                buttonsRow.Children.Add(element);
            }
            Grid.SetRow(buttonsRow, 1);
            root.Children.Add(buttonsRow);

            Grid.SetRow(statusText, 2);
            root.Children.Add(statusText);

            Grid.SetRow(objectList, 3);
            root.Children.Add(objectList);

            var footer = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0) };
            var cancel = new Button { Content = "Cancel", Padding = new Thickness(14, 3, 14, 3), Margin = new Thickness(8, 0, 0, 0), IsCancel = true };
            footer.Children.Add(importButton);
            footer.Children.Add(cancel);
            Grid.SetRow(footer, 4);
            root.Children.Add(footer);

            return root;
        }

        /// <summary>Event handler shell: an exception escaping an async handler would crash VS.</summary>
        private void OnLoadObjectsClick(object sender, RoutedEventArgs e)
        {
            // VSSDK007 wants a package-joined JTF; this is a dialog-scoped fire-and-forget whose
            // faults are caught below and filed by FileAndForget — package join adds nothing here.
#pragma warning disable VSSDK007
            ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
            {
                try
                {
                    await LoadObjectsAsync();
                }
                catch (Exception ex)
                {
                    SetBusy(false);
                    statusText.Text = "Load failed: " + ex.Message;
                }
            }).FileAndForget("pgproj/importdialog/load");
#pragma warning restore VSSDK007
        }

        /// <summary>Runs `pgproj extract` into a temp dir (this is also the connection test) and lists the units.</summary>
        private async System.Threading.Tasks.Task LoadObjectsAsync()
        {
            var connection = connectionBox.Text.Trim();
            if (connection.Length == 0)
            {
                statusText.Text = "Enter a connection string first.";
                return;
            }

            SetBusy(true);
            statusText.Text = "Reading the database…";
            TryCleanupExtractDir();
            extractDir = Path.Combine(Path.GetTempPath(), "pgproj_import_" + Guid.NewGuid().ToString("N"));

            var result = await PgProjCliRunner.RunAsync(
                $"extract --connection \"{connection.Replace("\"", "\\\"")}\" -o \"{extractDir}\"");

            SetBusy(false);
            if (!result.Success)
            {
                statusText.Text = "Load failed: " + FirstLine(result.Error.Length > 0 ? result.Error : result.Output);
                return;
            }

            objectList.Items.Clear();
            // One row per extracted .sql unit, relative path (Tables/app.customers.sql, …).
            // The extract-generated .pgproj scaffold is NOT offered — the user already has a project.
            var units = Directory.EnumerateFiles(extractDir, "*.sql", SearchOption.AllDirectories)
                .Select(f => f.Substring(extractDir.Length + 1).Replace('\\', '/'))
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase);
            foreach (var unit in units)
                objectList.Items.Add(new CheckBox { Content = unit, IsChecked = true, Tag = unit });

            var any = objectList.Items.Count > 0;
            includeAllButton.IsEnabled = any;
            excludeAllButton.IsEnabled = any;
            importButton.IsEnabled = any;
            statusText.Text = any
                ? $"{objectList.Items.Count} object(s) found — uncheck what you don't want, then Import."
                : "Connected, but the database has no user objects to import.";
        }

        private void Import()
        {
            var checkedUnits = objectList.Items.OfType<CheckBox>()
                .Where(c => c.IsChecked == true)
                .Select(c => (string)c.Tag)
                .ToList();
            if (checkedUnits.Count == 0 || extractDir is null)
            {
                statusText.Text = "Nothing is checked.";
                return;
            }

            var overwrite = overwriteBox.IsChecked == true;
            int written = 0, skipped = 0;
            var failures = new List<string>();
            foreach (var unit in checkedUnits)
            {
                var source = Path.Combine(extractDir, unit.Replace('/', Path.DirectorySeparatorChar));
                var target = Path.Combine(projectDir, unit.Replace('/', Path.DirectorySeparatorChar));
                try
                {
                    if (File.Exists(target) && !overwrite)
                    {
                        skipped++;
                        continue;
                    }
                    Directory.CreateDirectory(Path.GetDirectoryName(target));
                    File.Copy(source, target, overwrite: true);
                    written++;
                }
                catch (Exception ex)
                {
                    failures.Add($"{unit}: {ex.Message}");
                }
            }

            // The connection just worked (the objects came from it) — persist or forget per the checkbox.
            var connection = connectionBox.Text.Trim();
            if (rememberBox.IsChecked == true && connection.Length > 0)
                ConnectionStore.Save(projectPath, connection);
            else if (rememberBox.IsChecked != true)
                ConnectionStore.Forget(projectPath);

            var summary = $"Imported {written} object file(s)"
                + (skipped > 0 ? $", skipped {skipped} (already exist — check Overwrite to replace)" : "")
                + (failures.Count > 0 ? $", {failures.Count} FAILED:\n" + string.Join("\n", failures) : ".");
            MessageBox.Show(this, summary, "PgProj Import", MessageBoxButton.OK,
                failures.Count > 0 ? MessageBoxImage.Warning : MessageBoxImage.Information);

            if (failures.Count == 0)
            {
                DialogResult = true;
                Close();
            }
        }

        private void SetAllChecked(bool value)
        {
            foreach (var item in objectList.Items.OfType<CheckBox>())
                item.IsChecked = value;
        }

        private void SetBusy(bool busy)
        {
            loadButton.IsEnabled = !busy;
            connectionBox.IsEnabled = !busy;
            importButton.IsEnabled = !busy && objectList.Items.Count > 0;
        }

        private void TryCleanupExtractDir()
        {
            if (extractDir is null)
                return;
            try { Directory.Delete(extractDir, recursive: true); } catch { /* best effort */ }
            extractDir = null;
        }

        private static string FirstLine(string text)
        {
            var trimmed = (text ?? string.Empty).Trim();
            var newline = trimmed.IndexOf('\n');
            return newline < 0 ? trimmed : trimmed.Substring(0, newline).Trim();
        }
    }
}

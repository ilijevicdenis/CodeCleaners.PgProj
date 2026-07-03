// EP-TESTGEN (#157/#161) — the in-proc WPF "Generate Tests" dialog (code-built; no XAML compile needed).
// Collects the generation choices — test categories, database mode (Testcontainers vs an existing
// PostgreSQL), seed-hook emission, output folder — and shells the bundled pgproj CLI's `test generate`
// verb (async off the UI thread, mirroring ImportDatabaseDialog). The connection string is never written
// into committed files: the CLI puts it in a git-ignored *.local.runsettings, and "Remember connection"
// stores it DPAPI-encrypted per user (purpose "testgen", so it never collides with the import connection).
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using EnvDTE;
using EnvDTE80;
using Microsoft.VisualStudio.PlatformUI;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;

namespace PgProj.VisualStudio.ProjectSystem.Commands
{
    /// <summary>
    /// Modal options dialog for "Generate Tests (PgProj)…". All engine work happens in the pgproj CLI
    /// (<see cref="PgProjCliRunner"/>); this window only collects choices and reports the result.
    /// </summary>
    internal sealed class GenerateTestsDialog : DialogWindow
    {
        private const string TestConnectionPurpose = "testgen";

        private readonly string projectPath;
        private readonly string testProjectName;

        // categories (all on by default = the CLI's default set)
        private readonly CheckBox constraintsBox = Category("Constraint negatives (NOT NULL / PK / UNIQUE / CHECK)", "constraints");
        private readonly CheckBox fkBox = Category("Foreign-key orphan negatives", "fk");
        private readonly CheckBox crudBox = Category("CRUD insert round-trips", "crud");
        private readonly CheckBox viewBox = Category("View queryability", "view");
        private readonly CheckBox unitBox = Category("Function/trigger behaviour stubs", "unit");
        private readonly CheckBox existsBox = Category("Catalog existence smoke tests", "exists");

        // database mode
        private readonly RadioButton autoRadio = new RadioButton
        {
            GroupName = "DbMode",
            IsChecked = true,
            Content = "Automatic — Docker via Testcontainers, or PGPROJ_TEST_CONNECTION when set",
            Margin = new Thickness(0, 2, 0, 0),
        };
        private readonly RadioButton dockerRadio = new RadioButton
        {
            GroupName = "DbMode",
            Content = "Docker container (Testcontainers) — always spins a throwaway PostgreSQL",
            Margin = new Thickness(0, 2, 0, 0),
        };
        private readonly RadioButton existingRadio = new RadioButton
        {
            GroupName = "DbMode",
            Content = "Existing PostgreSQL server — a throwaway database is created and dropped",
            Margin = new Thickness(0, 2, 0, 0),
        };
        private readonly TextBox connectionBox = new TextBox { MinWidth = 380, VerticalContentAlignment = VerticalAlignment.Center, IsEnabled = false };
        private readonly CheckBox rememberBox = new CheckBox
        {
            Content = "Remember connection",
            IsChecked = true,
            IsEnabled = false,
            ToolTip = "Stores the connection DPAPI-encrypted under your Windows profile (never in the .pgproj).",
        };

        // output + misc
        private readonly TextBox outputBox = new TextBox { MinWidth = 380, VerticalContentAlignment = VerticalAlignment.Center };
        private readonly CheckBox seedHooksBox = new CheckBox
        {
            Content = "Generate seed-data hooks (Seeds\\*.Seed.cs + SuiteSeed.cs — never overwritten)",
            IsChecked = true,
        };
        private readonly CheckBox forceBox = new CheckBox
        {
            Content = "Overwrite scaffold-once files (csproj, fixture, seed stubs) — needed to change the database mode of an existing suite",
        };

        private readonly TextBlock statusText = new TextBlock { TextWrapping = TextWrapping.Wrap, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 8, 0, 0) };
        private readonly Button generateButton = new Button { Content = "Generate", Padding = new Thickness(14, 3, 14, 3), IsDefault = true };

        public GenerateTestsDialog(string projectPath)
        {
            this.projectPath = projectPath;
            var projectDir = Path.GetDirectoryName(projectPath);
            testProjectName = Path.GetFileNameWithoutExtension(projectPath) + ".Tests";

            Title = $"Generate Tests for '{Path.GetFileName(projectPath)}'";
            SizeToContent = SizeToContent.WidthAndHeight;
            MaxWidth = 660;
            ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;

            outputBox.Text = Path.Combine(projectDir ?? "", "Tests", testProjectName);
            connectionBox.Text = ConnectionStore.TryGet(projectPath, TestConnectionPurpose)
                ?? Environment.GetEnvironmentVariable("PGPROJ_TEST_CONNECTION")
                ?? string.Empty;
            statusText.Text = "Generates a standalone xUnit project — run it with `dotnet test` or the Test Explorer.";

            Content = BuildLayout();

            RoutedEventHandler syncConnection = (_, _) =>
            {
                var wantsConnection = existingRadio.IsChecked == true || autoRadio.IsChecked == true;
                // required for "existing"; optional for "auto" (it only pre-writes the runsettings)
                connectionBox.IsEnabled = wantsConnection;
                rememberBox.IsEnabled = wantsConnection;
            };
            autoRadio.Checked += syncConnection;
            dockerRadio.Checked += syncConnection;
            existingRadio.Checked += syncConnection;
            syncConnection(null, null);

            generateButton.Click += OnGenerateClick;
        }

        private static CheckBox Category(string label, string token) =>
            new CheckBox { Content = label, IsChecked = true, Tag = token, Margin = new Thickness(0, 2, 0, 0) };

        private UIElement BuildLayout()
        {
            var root = new StackPanel { Margin = new Thickness(12) };

            root.Children.Add(new TextBlock
            {
                Text = "The generated suite deploys the project's schema into its own PostgreSQL and runs every " +
                       "test in a rolled-back transaction. Regeneration never overwrites your seed hooks.",
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 10),
            });

            root.Children.Add(new TextBlock { Text = "Database at test-run time:", FontWeight = FontWeights.Bold });
            root.Children.Add(autoRadio);
            root.Children.Add(dockerRadio);
            root.Children.Add(existingRadio);

            var connectionRow = new DockPanel { Margin = new Thickness(18, 4, 0, 0) };
            var connectionLabel = new TextBlock { Text = "Connection:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
            DockPanel.SetDock(connectionLabel, Dock.Left);
            connectionRow.Children.Add(connectionLabel);
            connectionRow.Children.Add(connectionBox);
            root.Children.Add(connectionRow);
            rememberBox.Margin = new Thickness(18, 4, 0, 8);
            root.Children.Add(rememberBox);

            root.Children.Add(new TextBlock { Text = "Test categories:", FontWeight = FontWeights.Bold, Margin = new Thickness(0, 4, 0, 0) });
            foreach (var box in CategoryBoxes())
                root.Children.Add(box);

            var outputRow = new DockPanel { Margin = new Thickness(0, 10, 0, 0) };
            var outputLabel = new TextBlock { Text = "Output folder:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
            DockPanel.SetDock(outputLabel, Dock.Left);
            outputRow.Children.Add(outputLabel);
            outputRow.Children.Add(outputBox);
            root.Children.Add(outputRow);

            seedHooksBox.Margin = new Thickness(0, 8, 0, 0);
            root.Children.Add(seedHooksBox);
            forceBox.Margin = new Thickness(0, 4, 0, 0);
            root.Children.Add(forceBox);

            root.Children.Add(statusText);

            var footer = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
            var cancel = new Button { Content = "Cancel", Padding = new Thickness(14, 3, 14, 3), Margin = new Thickness(8, 0, 0, 0), IsCancel = true };
            footer.Children.Add(generateButton);
            footer.Children.Add(cancel);
            root.Children.Add(footer);

            return root;
        }

        private IEnumerable<CheckBox> CategoryBoxes()
        {
            yield return constraintsBox;
            yield return fkBox;
            yield return crudBox;
            yield return viewBox;
            yield return unitBox;
            yield return existsBox;
        }

        /// <summary>Event handler shell: an exception escaping an async handler would crash VS.</summary>
        private void OnGenerateClick(object sender, RoutedEventArgs e)
        {
            // VSSDK007 wants a package-joined JTF; this is a dialog-scoped fire-and-forget whose
            // faults are caught below and filed by FileAndForget — package join adds nothing here.
#pragma warning disable VSSDK007
            ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
            {
                try
                {
                    await GenerateAsync();
                }
                catch (Exception ex)
                {
                    SetBusy(false);
                    statusText.Text = "Generation failed: " + ex.Message;
                }
            }).FileAndForget("pgproj/generatetestsdialog/generate");
#pragma warning restore VSSDK007
        }

        private async System.Threading.Tasks.Task GenerateAsync()
        {
            var args = BuildArguments(out var error);
            if (args == null)
            {
                statusText.Text = error;
                return;
            }

            SetBusy(true);
            statusText.Text = "Generating the test project…";

            var result = await PgProjCliRunner.RunAsync(args);

            SetBusy(false);
            if (!result.Success)
            {
                statusText.Text = "Generation failed: " + FirstLine(result.Error.Length > 0 ? result.Error : result.Output);
                return;
            }

            // The generation succeeded — persist or forget the test connection per the checkbox.
            var connection = connectionBox.Text.Trim();
            if (connectionBox.IsEnabled && rememberBox.IsChecked == true && connection.Length > 0)
                ConnectionStore.Save(projectPath, connection, TestConnectionPurpose);
            else if (connectionBox.IsEnabled && rememberBox.IsChecked != true)
                ConnectionStore.Forget(projectPath, TestConnectionPurpose);

            // Add the freshly generated test project to the open solution so it shows up in
            // Solution Explorer / Test Explorer without a manual "Add > Existing Project".
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            var addNote = TryAddGeneratedProjectToSolution(outputBox.Text.Trim());

            MessageBox.Show(this, result.Output.Trim() + addNote, "PgProj Generate Tests",
                MessageBoxButton.OK, MessageBoxImage.Information);
            DialogResult = true;
            Close();
        }

        /// <summary>
        /// Adds the emitted <c>*.csproj</c> under <paramref name="outputDir"/> to the currently open
        /// solution (idempotent — a no-op if it is already loaded). Returns a short line to append to
        /// the success message; failures are reported as guidance, never thrown (the files exist on
        /// disk regardless, so a solution-add hiccup must not read as a generation failure).
        /// </summary>
        private static string TryAddGeneratedProjectToSolution(string outputDir)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                if (outputDir.Length == 0 || !Directory.Exists(outputDir)) return "";
                if (Package.GetGlobalService(typeof(SDTE)) is not DTE2 dte || dte.Solution is null) return "";

                var csproj = Directory.EnumerateFiles(outputDir, "*.csproj", SearchOption.TopDirectoryOnly).FirstOrDefault();
                if (csproj is null) return "";

                foreach (Project existing in dte.Solution.Projects)
                {
                    try
                    {
                        if (string.Equals(existing.FullName, csproj, StringComparison.OrdinalIgnoreCase))
                            return "\n\nThe test project is already part of the solution.";
                    }
                    catch { /* solution folders / unloaded projects throw on FullName — skip */ }
                }

                dte.Solution.AddFromFile(csproj, /*Exclusive*/ false);
                return "\n\nAdded the test project to the solution.";
            }
            catch (Exception ex)
            {
                return "\n\nThe project was generated on disk, but adding it to the solution failed: "
                     + ex.Message + "\nAdd it manually via Solution ▸ Add ▸ Existing Project.";
            }
        }

        /// <summary>The `test generate` CLI arguments for the current choices, or null (with a message) when invalid.</summary>
        private string BuildArguments(out string error)
        {
            error = null;

            var categories = new List<string>();
            foreach (var box in CategoryBoxes())
                if (box.IsChecked == true)
                    categories.Add((string)box.Tag);
            if (categories.Count == 0)
            {
                error = "Pick at least one test category.";
                return null;
            }

            var connection = connectionBox.Text.Trim();
            if (existingRadio.IsChecked == true && connection.Length == 0)
            {
                error = "Enter the PostgreSQL connection string for the existing-server mode.";
                return null;
            }

            var args = $"test generate \"{projectPath}\"";

            var output = outputBox.Text.Trim();
            if (output.Length > 0)
                args += $" -o \"{output}\"";

            // all six checked = the CLI default; omit the flag to keep the invocation minimal
            if (categories.Count < 6)
                args += $" --categories {string.Join(",", categories)}";

            if (dockerRadio.IsChecked == true)
                args += " --db-mode container";
            else if (existingRadio.IsChecked == true)
                args += " --db-mode existing";

            // The CLI writes the connection into a git-ignored *.local.runsettings (auto-applied by
            // dotnet test / Test Explorer) — never into committed files. Optional in automatic mode.
            if (connectionBox.IsEnabled && connection.Length > 0)
                args += $" --connection \"{connection.Replace("\"", "\\\"")}\"";

            if (seedHooksBox.IsChecked != true)
                args += " --no-seeds";
            if (forceBox.IsChecked == true)
                args += " --force";

            return args;
        }

        private void SetBusy(bool busy)
        {
            generateButton.IsEnabled = !busy;
            outputBox.IsEnabled = !busy;
            connectionBox.IsEnabled = !busy && (existingRadio.IsChecked == true || autoRadio.IsChecked == true);
        }

        private static string FirstLine(string text)
        {
            var trimmed = (text ?? string.Empty).Trim();
            var newline = trimmed.IndexOf('\n');
            return newline < 0 ? trimmed : trimmed.Substring(0, newline).Trim();
        }
    }
}

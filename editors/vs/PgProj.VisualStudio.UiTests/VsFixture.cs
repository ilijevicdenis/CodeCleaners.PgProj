// EP-VS — the one VS instance every UI test shares. Launching VS 2026 costs ~40s, so the fixture
// does it once: scaffold a scratch .pgproj solution under %TEMP%, launch the INSTALLED (main
// instance) devenv on it, attach DTE over the ROT, and wait for the solution to load. The harness
// is FOCUS-INDEPENDENT: all actions go through DTE COM (no synthesized input), UIA is read-only —
// so it runs in the background while the user keeps working, other devenv instances stay untouched
// (we match DTE by PID and only ever kill our own), and the VS window can sit minimized or on
// another virtual desktop. Teardown kills the launched process (buffer edits are never saved) and
// deletes the scratch dir.
using System;
using System.IO;
using System.Linq;
using System.Threading;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Tools;
using FlaUI.UIA3;
using Xunit;

namespace PgProj.VisualStudio.UiTests;

public sealed class VsFixture : IDisposable
{
    public UIA3Automation Automation { get; }
    public Application App { get; }
    public DteRemote Dte { get; }
    public string ScratchDir { get; }

    /// <summary>The opened view's .sql path; in DB mode it comes from the real extract.</summary>
    public string ViewFilePath { get; private set; } = null!;

    /// <summary>The view's bare name — used both as the coloring probe and the expected completion item.</summary>
    public string ViewIdentifier { get; private set; } = null!;

    /// <summary>The schema the view lives in (a managed schema, so a bogus relation in it is flagged).</summary>
    public string SchemaName { get; private set; } = null!;

    /// <summary>Per-attempt log of the open/close/reopen association loop, for failure reports.</summary>
    public System.Collections.Generic.List<string> OpenAttempts { get; private set; } = new();

    private const string EnvDteMiscFilesKind = "{66A2671D-8FB5-11D2-AA7E-00C04F688DDE}";

    private readonly Timer _dialogSlayer;

    public VsFixture()
    {
        ScratchDir = ScaffoldScratchSolution();

        // /log LAST (a path after /log would become the log file): the editor factory writes its
        // claim/decline reasoning through IVsActivityLog, which only persists under /log.
        App = Application.Launch(new System.Diagnostics.ProcessStartInfo(
            LocateDevenv(), $"\"{Path.Combine(ScratchDir, "UiTest.slnx")}\" /log"));
        Automation = new UIA3Automation();

        // The folder-trust prompt appears on its own schedule and blocks solution load. Dismissing it
        // is a UIA Invoke (no input queue), so the background swatter is focus-safe too.
        _dialogSlayer = new Timer(_ => TryDismissBlockingDialogs(), null, 1000, 1500);

        Dte = Retry.WhileNull(() => DteRemote.TryAttach(App.ProcessId),
                timeout: TimeSpan.FromSeconds(120), interval: TimeSpan.FromSeconds(1), throwOnTimeout: true)
            .Result!;

        // Solution open + our project loaded (Projects.Count goes 0 → 1 when CPS finishes).
        Retry.WhileFalse(
            () => Dte.Invoke<bool>(d => d.Solution.IsOpen && d.Solution.Projects.Count >= 1),
            timeout: TimeSpan.FromSeconds(120), interval: TimeSpan.FromSeconds(2), throwOnTimeout: true);

        // A document opened before CPS finishes materializing gets NO owning project: VS hands the
        // editor factory a solution/misc hierarchy, the factory declines, and the doc permanently
        // lands in the plain text editor (proven via the factory's ActivityLog instrumentation).
        // Open → check association (the claimed editor sets the pgsql content type, which DTE
        // reports as a non-"Plain Text" language OR the file's project item is resolvable) →
        // close + reopen until the project owns it. Each attempt is logged for the failure report.
        OpenAttempts = new System.Collections.Generic.List<string>();
        for (var attempt = 1; attempt <= 8; attempt++)
        {
            Dte.Invoke(d => d.ItemOperations.OpenFile(ViewFilePath));
            System.Threading.Thread.Sleep(2000);
            var (language, hasProjectItem) = Dte.Invoke<(string, bool)>(d =>
            {
                var doc = d.ActiveDocument;
                string lang = doc.Language;
                bool inProject;
                try { inProject = doc.ProjectItem is not null && doc.ProjectItem.ContainingProject is not null
                                  && (string)doc.ProjectItem.ContainingProject.Kind != EnvDteMiscFilesKind; }
                catch { inProject = false; }
                return (lang, inProject);
            });
            OpenAttempts.Add($"attempt {attempt}: language={language} projectOwned={hasProjectItem}");
            if (hasProjectItem) break;
            Dte.Invoke(d => d.ActiveDocument.Close(2 /* vsSaveChangesNo (1 is YES — saves!) */));
            System.Threading.Thread.Sleep(5000);
        }

        BaselineText = GetBufferText();

        // Project ownership is necessary but NOT sufficient: a doc opened during CPS
        // materialization keeps the plain-text editor even after the project claims the file
        // (DTE reports "Plain Text" for our pgsql buffer too, so language can't discriminate).
        // The LSP server process is the definitive "factory claimed + client attached" signal —
        // close/reopen until it spawns, else every language assertion fails mysteriously later.
        for (var attempt = 1; attempt <= 4 && !Retry.WhileFalse(IsLspServerRunning,
                 timeout: TimeSpan.FromSeconds(20), interval: TimeSpan.FromSeconds(1)).Result; attempt++)
        {
            OpenAttempts.Add($"lsp not up after open #{attempt} — closing and reopening the document");
            Dte.Invoke(d => d.ActiveDocument.Close(2 /* vsSaveChangesNo */));
            Thread.Sleep(3000);
            Dte.Invoke(d => d.ItemOperations.OpenFile(ViewFilePath));
        }
        if (!IsLspServerRunning())
        {
            var shot = Path.Combine(Path.GetTempPath(), $"pgproj-uitest-barrier-{Guid.NewGuid().ToString("N")[..6]}.png");
            try { FlaUI.Core.Capturing.Capture.Screen().ToFile(shot); } catch { }
            string processes = "";
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo("powershell.exe",
                    "-NoProfile -Command \"(Get-CimInstance Win32_Process -Filter \\\"Name='dotnet.exe'\\\").CommandLine\"")
                { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
                using var p = System.Diagnostics.Process.Start(psi)!;
                processes = p.StandardOutput.ReadToEnd().Trim();
                p.WaitForExit();
            }
            catch { }
            throw new InvalidOperationException(
                "The PgProj LSP server never started — the document did not get the pgsql editor. Attempts:\n"
                + string.Join("\n", OpenAttempts)
                + "\nEnvironment:\n   " + CollectDiagnostics()
                + "\ndotnet processes:\n" + processes
                + "\nscreenshot: " + shot);
        }
    }

    /// <summary>True when the VSIX-bundled `pgproj serve` child process is alive.</summary>
    public bool IsLspServerRunning()
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("powershell.exe",
                "-NoProfile -Command \"(Get-CimInstance Win32_Process -Filter \\\"Name='dotnet.exe'\\\" | Where-Object { $_.CommandLine -match 'PgProj.Cli' -and $_.CommandLine -match 'serve' }).Count\"")
            { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
            using var p = System.Diagnostics.Process.Start(psi)!;
            var output = p.StandardOutput.ReadToEnd().Trim();
            p.WaitForExit();
            return int.TryParse(output, out var n) && n > 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Puts the caret in the middle of <paramref name="word"/> on the LAST line of the document —
    /// deterministic line/column math instead of TextSelection.FindText, which silently failed to
    /// move the caret and sent every F12 onto a semicolon.
    /// </summary>
    public void PlaceCaretOnLastLineWord(string word) => Dte.Invoke(d =>
    {
        var td = d.ActiveDocument.Object("TextDocument");
        int lastLine = td.EndPoint.Line;
        var ep = td.StartPoint.CreateEditPoint();
        ep.MoveToLineAndOffset(lastLine, 1);
        string lineText = (string)ep.GetText(td.EndPoint);
        // word-boundary match: a one-letter alias like "o" must not hit the 'o' inside "FROM"
        var m = System.Text.RegularExpressions.Regex.Match(lineText,
            $@"\b{System.Text.RegularExpressions.Regex.Escape(word)}\b",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (!m.Success) throw new InvalidOperationException($"'{word}' not found on the last line: {lineText}");
        d.ActiveDocument.Selection.MoveToLineAndOffset(lastLine, m.Index + 1 + word.Length / 2);
    });

    /// <summary>The VS text editor control (class WpfTextView) hosting the opened document — UIA, read-only use.</summary>
    public AutomationElement GetEditor() =>
        Retry.WhileNull(
            () => GetMainWindow()?.FindFirstDescendant(cf => cf.ByClassName("WpfTextView")),
            timeout: TimeSpan.FromSeconds(30), interval: TimeSpan.FromMilliseconds(500), throwOnTimeout: true).Result!;

    public Window? GetMainWindow()
    {
        try
        {
            return App.GetMainWindow(Automation, TimeSpan.FromSeconds(2));
        }
        catch
        {
            return null;
        }
    }

    /// <summary>The opened view's pristine content — every scenario resets to this baseline.</summary>
    public string BaselineText { get; private set; } = "";

    // ---- DTE-driven editing helpers (no input queue) ----------------------------------------

    /// <summary>Replaces the active document's entire content (deterministic per-case reset).</summary>
    public void SetBufferText(string text) => Dte.Invoke(d =>
    {
        var sel = d.ActiveDocument.Selection;
        sel.SelectAll();
        sel.Delete(1);
        sel.Insert(text, 2);
        sel.EndOfDocument(false);
    });

    /// <summary>The active document's full text.</summary>
    public string GetBufferText() => Dte.Invoke<string>(d =>
    {
        var td = d.ActiveDocument.Object("TextDocument");
        var ep = td.StartPoint.CreateEditPoint();
        return (string)ep.GetText(td.EndPoint);
    });

    /// <summary>Back to the pristine file content, then waits for this file's Error List rows to clear.</summary>
    public void ResetBuffer()
    {
        SetBufferText(BaselineText);
        var file = Path.GetFileName(ViewFilePath);
        FlaUI.Core.Tools.Retry.WhileTrue(() => ErrorListShows(file),
            timeout: TimeSpan.FromSeconds(15), interval: TimeSpan.FromMilliseconds(500));
    }

    /// <summary>UIA scan of the Error List pane for a row mentioning <paramref name="token"/>.</summary>
    public bool ErrorListShows(string token)
    {
        try
        {
            var pane = GetMainWindow()?.FindFirstDescendant(cf => cf.ByName("Error List"));
            if (pane is null) return false;
            return pane.FindAllDescendants().Any(e =>
            {
                try { return (e.Name ?? "").IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0; }
                catch { return false; }
            });
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Polls until a diagnostic mentioning <paramref name="token"/> shows up (or not).</summary>
    public bool WaitForDiagnostic(string token, int seconds = 20) =>
        FlaUI.Core.Tools.Retry.WhileFalse(() => ErrorListShows(token),
            timeout: TimeSpan.FromSeconds(seconds), interval: TimeSpan.FromMilliseconds(500)).Result;

    /// <summary>
    /// Types text as REAL input into the editor (only into the VS this harness owns) — and only
    /// after VERIFYING the editor holds keyboard focus: stray console windows on the test desktop
    /// can grab foreground, and keystrokes typed into them wedge the whole suite.
    /// </summary>
    public void TypeInEditor(string text)
    {
        Dte.Invoke(d => d.ActiveDocument.Activate());
        var editor = GetEditor();
        FlaUI.Core.Tools.Retry.WhileFalse(() =>
        {
            try { editor.Focus(); return editor.Properties.HasKeyboardFocus.ValueOrDefault; }
            catch { return false; }
        }, timeout: TimeSpan.FromSeconds(10), interval: TimeSpan.FromMilliseconds(400), throwOnTimeout: true);
        FlaUI.Core.Input.Keyboard.Type(text);
    }

    public void PressKey(FlaUI.Core.WindowsAPI.VirtualKeyShort key) => FlaUI.Core.Input.Keyboard.Type(key);

    /// <summary>
    /// Dismisses any open completion session for sure: focused ESC plus a DTE caret move (which
    /// cancels the session even if the keystroke went astray). An open session keeps VS's COM
    /// message filter answering "busy" — one leaked popup poisons every test after it.
    /// </summary>
    public void DismissCompletion()
    {
        try
        {
            var editor = GetEditor();
            editor.Focus();
        }
        catch { }
        FlaUI.Core.Input.Keyboard.Type(FlaUI.Core.WindowsAPI.VirtualKeyShort.ESCAPE);
        FlaUI.Core.Input.Wait.UntilInputIsProcessed(TimeSpan.FromMilliseconds(500));
        Dte.Invoke(d => d.ActiveDocument.Selection.EndOfDocument(false));
    }

    /// <summary>A completion-popup entry by display name (popup items don't expose as ListItem).</summary>
    public AutomationElement? FindCompletionItem(string label)
    {
        foreach (var popup in Automation.GetDesktop().FindAllChildren(cf => cf.ByProcessId(App.ProcessId)))
        {
            if (Equals(popup, GetMainWindow())) continue;
            var item = popup.FindFirstDescendant(cf => cf.ByName(label));
            if (item is not null) return item;
        }
        return GetMainWindow()?.FindFirstDescendant(cf =>
            cf.ByName(label).And(cf.ByControlType(FlaUI.Core.Definitions.ControlType.TreeItem).Not()));
    }

    /// <summary>
    /// Appends <paramref name="text"/> on a fresh line at the end of the active document and leaves
    /// the caret AFTER it — Insert can collapse the selection to the START of the inserted text
    /// (a typed trigger character then lands at the line start; cost one debugging round).
    /// </summary>
    public void AppendLine(string text) => Dte.Invoke(d =>
    {
        var sel = d.ActiveDocument.Selection;
        sel.EndOfDocument(false);
        sel.NewLine(1);
        sel.Insert(text, 2 /* vsInsertFlagsInsertAtEnd */);
        sel.EndOfDocument(false);
    });

    /// <summary>Undoes every buffered edit so the next check starts from the pristine file.</summary>
    public void UndoAll()
    {
        for (var i = 0; i < 16; i++)
        {
            var undone = Dte.Invoke<bool>(d =>
            {
                try { d.ExecuteCommand("Edit.Undo", ""); return true; }
                catch { return false; } // undo stack exhausted → command disabled → COM error
            });
            if (!undone) break;
        }
    }

    /// <summary>
    /// Environment facts for failure reports: what the project/editor chain ACTUALLY resolved to.
    /// Decides between "factory declined the file" (project kind mismatch / plain-text language)
    /// and "factory claimed it but downstream broke" (pgsql buffer, LSP process state).
    /// </summary>
    public string CollectDiagnostics()
    {
        var lines = new System.Collections.Generic.List<string>();
        try
        {
            lines.Add("project kind : " + Dte.Invoke<string>(d => d.Solution.Projects.Item(1).Kind));
            lines.Add("expected kind: {B0000000-0000-0000-0000-0000000000A1}");
        }
        catch (Exception ex) { lines.Add("project kind : <failed: " + ex.Message + ">"); }
        try
        {
            lines.Add("doc language : " + Dte.Invoke<string>(d => d.ActiveDocument.Language));
        }
        catch (Exception ex) { lines.Add("doc language : <failed: " + ex.Message + ">"); }
        foreach (var a in OpenAttempts) lines.Add(a);
        try
        {
            // The factory's own decision entries (VS runs with /log). Read fresh from disk each time.
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var logFile = Directory.EnumerateDirectories(Path.Combine(appData, "Microsoft", "VisualStudio"), "18.0_*")
                .Select(d => Path.Combine(d, "ActivityLog.xml"))
                .Where(File.Exists)
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();
            if (logFile is null) { lines.Add("factory log  : <no ActivityLog.xml found>"); }
            else
            {
                // devenv keeps the log open for append while running — read shared.
                using var fs = new FileStream(logFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                var doc = System.Xml.Linq.XDocument.Load(fs);
                var entries = doc.Descendants("entry")
                    .Where(e => (string?)e.Element("source") == "PgSqlEditorFactory")
                    .Select(e => (string?)e.Element("description") ?? "")
                    .ToList();
                if (entries.Count == 0) lines.Add("factory log  : NO PgSqlEditorFactory entries — the factory was never invoked for this open");
                else foreach (var e in entries) lines.Add("factory log  : " + e);
            }
        }
        catch (Exception ex) { lines.Add("factory log  : <failed: " + ex.Message + ">"); }
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("powershell.exe",
                "-NoProfile -Command \"(Get-CimInstance Win32_Process -Filter \\\"Name='dotnet.exe'\\\" | Where-Object { $_.CommandLine -match 'PgProj.Cli' }).CommandLine\"")
            { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
            using var p = System.Diagnostics.Process.Start(psi)!;
            var cmdline = p.StandardOutput.ReadToEnd().Trim();
            p.WaitForExit();
            lines.Add("lsp process  : " + (cmdline.Length > 0 ? cmdline : "NOT RUNNING"));
        }
        catch (Exception ex) { lines.Add("lsp process  : <failed: " + ex.Message + ">"); }
        return string.Join("\n   ", lines);
    }

    // ---- scaffolding --------------------------------------------------------------------------

    private string ScaffoldScratchSolution()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pgproj-uitest-" + Guid.NewGuid().ToString("N").Substring(0, 8));

        // DB mode: extract the REAL sample database (tests/sample-db) into the scratch dir with the
        // VSIX-bundled CLI — the very payload under test. The hand-written fallback keeps the suite
        // runnable with no database around.
        var dbConnection = Environment.GetEnvironmentVariable("PGPROJ_UITEST_DB");
        var pgproj = dbConnection is not null
            ? ExtractFromDatabase(dir, dbConnection)
            : WriteHandRolledProject(dir);

        var relProject = Path.GetRelativePath(dir, pgproj).Replace('\\', '/');
        File.WriteAllText(Path.Combine(dir, "UiTest.slnx"), $"""
            <Solution>
              <Project Path="{relProject}" Type="b0000000-0000-0000-0000-0000000000a1" Id="71d3d077-1939-46a8-a449-c927120a6ec8" />
            </Solution>
            """);

        // The view under test: any Views\*.sql of the project (the sample DB has sales.v_open_orders;
        // the hand-rolled project has public.v_customers). Its bare name doubles as the coloring
        // probe and the expected completion item; its schema is where the bogus relation goes.
        ViewFilePath = Directory.EnumerateFiles(Path.GetDirectoryName(pgproj)!, "*.sql", SearchOption.AllDirectories)
            .First(f => Path.GetFileName(Path.GetDirectoryName(f)!).Equals("Views", StringComparison.OrdinalIgnoreCase));
        ViewIdentifier = Path.GetFileNameWithoutExtension(ViewFilePath);
        var text = File.ReadAllText(ViewFilePath);
        // Handles the extract emitter's full form: CREATE [MATERIALIZED] VIEW [IF NOT EXISTS] "schema"."name"
        var match = System.Text.RegularExpressions.Regex.Match(text,
            @"VIEW\s+(?:IF\s+NOT\s+EXISTS\s+)?""?(?<schema>\w+)""?\.",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        SchemaName = match.Success ? match.Groups["schema"].Value : "public";
        return dir;
    }

    private static string WriteHandRolledProject(string dir)
    {
        Directory.CreateDirectory(Path.Combine(dir, "Db", "Tables"));
        Directory.CreateDirectory(Path.Combine(dir, "Db", "Views"));
        File.WriteAllText(Path.Combine(dir, "Db", "Db.pgproj"), """
            <Project Sdk="PgProj.Sdk/0.1.0" DefaultTargets="Build">
              <PropertyGroup>
                <Name>UiTestDb</Name>
                <DefaultSchema>public</DefaultSchema>
                <EnableDefaultSqlItems>false</EnableDefaultSqlItems>
              </PropertyGroup>
              <ItemGroup>
                <Build Include="Tables\customers.sql" />
                <Build Include="Views\v_customers.sql" />
              </ItemGroup>
            </Project>
            """);
        File.WriteAllText(Path.Combine(dir, "Db", "Tables", "customers.sql"),
            "CREATE TABLE public.customers (id integer NOT NULL, name text);\n");
        File.WriteAllText(Path.Combine(dir, "Db", "Views", "v_customers.sql"),
            "CREATE VIEW public.v_customers AS SELECT id FROM public.customers;\n");
        return Path.Combine(dir, "Db", "Db.pgproj");
    }

    private static string ExtractFromDatabase(string dir, string connection)
    {
        var outDir = Path.Combine(dir, "Db");
        var psi = new System.Diagnostics.ProcessStartInfo("dotnet",
            $"\"{LocateBundledCli()}\" extract --connection \"{connection}\" -o \"{outDir}\"")
        { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        using var p = System.Diagnostics.Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEnd();
        var stderr = p.StandardError.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0)
            throw new InvalidOperationException($"pgproj extract failed ({p.ExitCode}):\n{stdout}\n{stderr}");
        return Directory.EnumerateFiles(outDir, "*.pgproj", SearchOption.TopDirectoryOnly).Single();
    }

    /// <summary>The pgproj CLI bundled inside the INSTALLED extension — the exact payload VS uses.</summary>
    private static string LocateBundledCli()
    {
        var extensionsRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "VisualStudio");
        if (Directory.Exists(extensionsRoot))
        {
            var cli = Directory.EnumerateDirectories(extensionsRoot, "18.0_*")
                .Select(inst => Path.Combine(inst, "Extensions"))
                .Where(Directory.Exists)
                .SelectMany(ext => Directory.EnumerateFiles(ext, "PgProj.Cli.dll", SearchOption.AllDirectories))
                .FirstOrDefault(f => f.Contains("tools", StringComparison.OrdinalIgnoreCase));
            if (cli is not null) return cli;
        }
        throw new FileNotFoundException(
            "The VSIX-bundled PgProj.Cli.dll was not found under any VS 2026 per-user Extensions dir — install the extension first (editors/vs/install-pgproj.cmd).");
    }

    private static string LocateDevenv()
    {
        var vswhere = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            "Microsoft Visual Studio", "Installer", "vswhere.exe");
        if (File.Exists(vswhere))
        {
            var psi = new System.Diagnostics.ProcessStartInfo(vswhere, "-latest -prerelease -products * -property productPath")
            { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
            using var p = System.Diagnostics.Process.Start(psi)!;
            var path = p.StandardOutput.ReadToEnd().Trim();
            p.WaitForExit();
            if (File.Exists(path)) return path;
        }
        var fallback = @"C:\Program Files\Microsoft Visual Studio\18\Community\Common7\IDE\devenv.exe";
        if (File.Exists(fallback)) return fallback;
        throw new FileNotFoundException("devenv.exe not found (vswhere gave nothing and the VS 2026 default path is empty).");
    }

    /// <summary>
    /// Clicks away anything modal that blocks solution load (the folder-trust dialog above all).
    /// UIA Invoke — focus-safe. Runs on a timer; must never throw.
    /// </summary>
    private void TryDismissBlockingDialogs()
    {
        try
        {
            if (App.HasExited) return;
            var desktop = Automation.GetDesktop();
            foreach (var w in desktop.FindAllChildren(cf => cf.ByProcessId(App.ProcessId)))
            {
                var name = w.Name ?? "";
                if (!name.Contains("trust", StringComparison.OrdinalIgnoreCase)
                    && !name.Contains("security", StringComparison.OrdinalIgnoreCase)) continue;
                var button = w.FindAllDescendants(cf => cf.ByControlType(ControlType.Button))
                    .FirstOrDefault(b => (b.Name ?? "").Contains("trust", StringComparison.OrdinalIgnoreCase)
                                      || (b.Name ?? "").Contains("continue", StringComparison.OrdinalIgnoreCase));
                button?.AsButton().Invoke();
            }
        }
        catch
        {
            // background best-effort only
        }
    }

    public void Dispose()
    {
        _dialogSlayer.Dispose();
        try { if (!App.HasExited) App.Kill(); } catch { }
        Automation.Dispose();
        try { Directory.Delete(ScratchDir, recursive: true); } catch { }
    }
}

[CollectionDefinition("vs")]
public sealed class VsCollection : ICollectionFixture<VsFixture>;

// A SEPARATE collection (and therefore a separate VS instance) for cross-file scenarios: editing a
// second document's buffer back and forth wedges VS's COM message filter for minutes, and inside
// the main collection that one test poisoned every case scheduled after it.
[CollectionDefinition("vs-crossfile")]
public sealed class VsCrossFileCollection : ICollectionFixture<VsFixture>;

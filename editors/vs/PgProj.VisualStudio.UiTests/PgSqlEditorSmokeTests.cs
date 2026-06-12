// EP-VS — the E2E smoke test for the installed PostgreSQL editor experience. One launched VS, one
// scratch solution, three checks in a fixed order (read-only first, buffer-mutating after), every
// failure collected so a single run reports the full broken/working picture — this automates the
// manual loop of "open a .sql in the .pgproj, look at colors, dot-complete, type a bogus table".
// All ACTIONS are DTE COM (focus-independent); UIA is used only to READ (colors, completion popup).
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Capturing;
using FlaUI.Core.Definitions;
using FlaUI.Core.Tools;
using Xunit;

namespace PgProj.VisualStudio.UiTests;

[Collection("vs")]
public sealed class PgSqlEditorSmokeTests
{
    private readonly VsFixture _vs;

    public PgSqlEditorSmokeTests(VsFixture vs) => _vs = vs;

    [Fact]
    public void Coloring_completion_and_semantic_diagnostics_all_work_in_the_installed_product()
    {
        var failures = new List<string>();

        CheckSyntaxColoring(failures);
        CheckCompletion(failures);
        CheckSemanticDiagnostics(failures);

        if (failures.Count > 0)
        {
            var shot = Path.Combine(Path.GetTempPath(), $"pgproj-uitest-failure-{DateTime.Now:HHmmss}.png");
            try { Capture.Screen().ToFile(shot); failures.Add($"(screenshot: {shot})"); } catch { }
            failures.Add("environment:\n   " + _vs.CollectDiagnostics());
            Assert.Fail("PostgreSQL editor smoke checks failed:\n - " + string.Join("\n - ", failures));
        }
    }

    // ---- 1) syntax coloring: the CREATE keyword must be painted differently from an identifier ----

    private void CheckSyntaxColoring(List<string> failures)
    {
        try
        {
            var editor = _vs.GetEditor();
            var text = editor.Patterns.Text.Pattern;
            var keyword = text.DocumentRange.FindText("CREATE", backward: false, ignoreCase: false);
            var identifier = text.DocumentRange.FindText(_vs.ViewIdentifier, backward: false, ignoreCase: false);
            if (keyword is null || identifier is null)
            {
                failures.Add($"coloring: could not locate 'CREATE' / '{_vs.ViewIdentifier}' in the editor text (wrong document open?).");
                return;
            }

            var keywordColor = keyword.GetAttributeValue(_vs.Automation.TextAttributeLibrary.ForegroundColor);
            var identifierColor = identifier.GetAttributeValue(_vs.Automation.TextAttributeLibrary.ForegroundColor);
            if (Equals(keywordColor, identifierColor))
                failures.Add($"coloring: 'CREATE' has the same foreground as a plain identifier ({keywordColor}) — the pgsql classifier is not running.");
        }
        catch (Exception ex)
        {
            failures.Add($"coloring: check itself failed — {ex.Message}");
        }
    }

    // ---- 2) IntelliSense: 'public.' must offer the project's tables --------------------------------

    private void CheckCompletion(List<string> failures)
    {
        try
        {
            // The real user flow: type the trigger character and let the async-completion broker
            // pop the LSP list. (DTE Edit.ListMembers reports "not available" from automation even
            // with the client attached, so the dot is typed as actual input — into the VS instance
            // THIS harness launched, never someone's own.)
            _vs.AppendLine($"SELECT id FROM {_vs.SchemaName}");
            _vs.Dte.Invoke(d => d.ActiveDocument.Activate());
            var editor = _vs.GetEditor();
            editor.Focus();
            FlaUI.Core.Input.Keyboard.Type(".");

            var found = Retry.WhileFalse(() => FindCompletionItem(_vs.ViewIdentifier) is not null,
                timeout: TimeSpan.FromSeconds(20), interval: TimeSpan.FromMilliseconds(500)).Result;
            if (!found)
            {
                // Capture the moment: what IS on screen, and which list items exist anywhere in
                // the devenv popups — distinguishes "no popup" from "popup with unexpected names".
                var midShot = Path.Combine(Path.GetTempPath(), $"pgproj-uitest-completion-{DateTime.Now:HHmmss}.png");
                try { Capture.Screen().ToFile(midShot); } catch { }
                var inventory = DesktopPopups()
                    .SelectMany(p => p.FindAllDescendants(cf => cf.ByControlType(ControlType.ListItem)))
                    .Select(SafeName).Where(n => n.Length > 0).Distinct().Take(25).ToList();
                failures.Add($"completion: no IntelliSense popup offering '{_vs.ViewIdentifier}' appeared after typing '{_vs.SchemaName}.' — " +
                    $"visible list items: [{string.Join(", ", inventory)}] (mid-check screenshot: {midShot})");
            }
            FlaUI.Core.Input.Keyboard.Type(FlaUI.Core.WindowsAPI.VirtualKeyShort.ESCAPE);

            // Any caret-moving command dismisses the session (focus-free Escape equivalent).
            _vs.Dte.Invoke(d => d.ActiveDocument.Selection.EndOfDocument(false));
        }
        catch (Exception ex)
        {
            failures.Add($"completion: check itself failed — {ex.Message}");
        }
        finally
        {
            _vs.UndoAll();
        }
    }

    private AutomationElement? FindCompletionItem(string label)
    {
        // The async-completion presenter is a top-level WPF popup owned by the devenv process.
        // Its items do NOT expose as ControlType.ListItem (a visible popup produced zero ListItems
        // in a real run) — match by Name across the popup's whole subtree instead. The main window
        // is searched as a fallback with TreeItem excluded so a Solution Explorer node named like
        // the object can't satisfy the check.
        foreach (var popup in DesktopPopups().Where(w => !Equals(w, _vs.GetMainWindow())))
        {
            var item = popup.FindFirstDescendant(cf => cf.ByName(label));
            if (item is not null) return item;
        }
        return _vs.GetMainWindow()?.FindFirstDescendant(cf =>
            cf.ByName(label).And(cf.ByControlType(ControlType.TreeItem).Not()));
    }

    private static string SafeName(AutomationElement e)
    {
        try { return e.Name ?? ""; } catch { return ""; }
    }

    /// <summary>Top-level windows of the devenv process (WPF popups live at desktop level).</summary>
    private IEnumerable<AutomationElement> DesktopPopups() =>
        _vs.Automation.GetDesktop().FindAllChildren(cf => cf.ByProcessId(_vs.App.ProcessId));

    // ---- 3) semantic check: an unresolved relation must reach the Error List, unsaved --------------

    private void CheckSemanticDiagnostics(List<string> failures)
    {
        try
        {
            _vs.AppendLine($"SELECT 1 FROM {_vs.SchemaName}.zzz_missing_table;");

            // Show the Error List, then poll BOTH channels while the LSP round-trips: the legacy
            // DTE ErrorItems (classic providers) and a UIA text scan of the pane — LSP diagnostics
            // land in the modern error table, which the DTE automation model does not surface.
            _vs.Dte.Invoke(d => d.ExecuteCommand("View.ErrorList", ""));
            var found = Retry.WhileFalse(
                () => ErrorListMentions("zzz_missing_table") || ErrorListPaneShows("zzz_missing_table"),
                timeout: TimeSpan.FromSeconds(30), interval: TimeSpan.FromSeconds(1)).Result;
            if (!found)
                failures.Add("semantic: 'zzz_missing_table' never reached the Error List — live reference validation is not running.");
        }
        catch (Exception ex)
        {
            failures.Add($"semantic: check itself failed — {ex.Message}");
        }
        finally
        {
            _vs.UndoAll();
        }
    }

    private bool ErrorListMentions(string token) => _vs.Dte.Invoke<bool>(d =>
    {
        try
        {
            // NOT d.ToolWindows: the ROT object dispatches through the EnvDTE.DTE (v1) default
            // interface, which has no ToolWindows member — go via the tool window's Object instead
            // (vsWindowKindErrorList GUID), which dispatches to the ErrorList automation object.
            var errorList = d.Windows.Item("{D78612C7-9962-4B83-95D9-268046DAD23A}").Object;
            var items = errorList.ErrorItems;
            for (var i = 1; i <= (int)items.Count; i++)
            {
                string description = items.Item(i).Description ?? "";
                if (description.Contains(token, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }
        catch
        {
            return false; // empty/uninitialized list shapes vary — treat as "not there yet" and re-poll
        }
    });

    /// <summary>UIA read of the Error List pane's rows (LSP diagnostics live in the modern error table).</summary>
    private bool ErrorListPaneShows(string token)
    {
        try
        {
            var pane = _vs.GetMainWindow()?.FindFirstDescendant(cf => cf.ByName("Error List"));
            if (pane is null) return false;
            return pane.FindAllDescendants()
                .Any(e => { try { return (e.Name ?? "").Contains(token, StringComparison.OrdinalIgnoreCase); } catch { return false; } });
        }
        catch
        {
            return false;
        }
    }
}

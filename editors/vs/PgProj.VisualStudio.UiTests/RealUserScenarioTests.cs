// EP-VS — real-user scenario suite for the installed PostgreSQL editor: 100+ data-driven cases
// sharing ONE launched VS (the "vs" collection serializes all classes onto the fixture). Every
// case is a thing a database developer actually does: type SQL and watch for (or expect no)
// squiggles, dot-complete schema members, jump to definitions, edit/undo/save. Cases reset the
// buffer to the pristine file and wait for that file's diagnostics to clear, so they are
// order-independent. Unique zzz_* markers keep one case's diagnostic from satisfying another's.
using System;
using System.IO;
using System.Linq;
using FlaUI.Core.Tools;
using FlaUI.Core.WindowsAPI;
using Xunit;

namespace PgProj.VisualStudio.UiTests;

// =================================================================================================
// 1) SYNTAX COLORING — every probe word must be painted differently from a plain identifier.
// =================================================================================================
[Collection("vs")]
public sealed class ColoringScenarios
{
    private readonly VsFixture _vs;
    public ColoringScenarios(VsFixture vs) => _vs = vs;

    // One rich document exercising the classifier's token classes; probe words are unique in it.
    private const string RichSql = """
        -- pgproj rich coloring probe
        /* block comment /* nested per postgres */ still comment */
        CREATE TABLE sales.probe_widgets (
            widget_id   integer NOT NULL DEFAULT 42,
            label       text COLLATE "C",
            price       numeric(12,2) CHECK (price >= 0.5),
            "QuotedCol" boolean
        );
        CREATE FUNCTION sales.probe_fn() RETURNS text LANGUAGE sql AS $body$
            SELECT 'dollar quoted contents'
        $body$;
        select lower('MiXeD');
        SELECT E'escaped\nstring', 12345, price FROM sales.probe_widgets WHERE label LIKE 'abc%';
        """;

    private void EnsureRichDoc()
    {
        if (!_vs.GetBufferText().Contains("probe_widgets")) _vs.SetBufferText(RichSql);
    }

    private object? ForegroundOf(string word)
    {
        var text = _vs.GetEditor().Patterns.Text.Pattern;
        var range = text.DocumentRange.FindText(word, backward: false, ignoreCase: false);
        return range?.GetAttributeValue(_vs.Automation.TextAttributeLibrary.ForegroundColor);
    }

    [Theory]
    // keywords (upper and lower case)
    [InlineData("CREATE")]
    [InlineData("TABLE")]
    [InlineData("FUNCTION")]
    [InlineData("RETURNS")]
    [InlineData("LANGUAGE")]
    [InlineData("DEFAULT")]
    [InlineData("CHECK")]
    [InlineData("SELECT")]
    [InlineData("FROM")]
    [InlineData("WHERE")]
    [InlineData("LIKE")]
    [InlineData("select")]
    // type names
    [InlineData("integer")]
    [InlineData("numeric")]
    [InlineData("boolean")]
    // literals & comments (each colored unlike an identifier)
    [InlineData("'dollar quoted contents'")]
    [InlineData("E'escaped")]
    [InlineData("block comment")]
    [InlineData("still comment")]
    [InlineData("pgproj rich coloring probe")]
    public void Token_is_colored_unlike_a_plain_identifier(string token)
    {
        EnsureRichDoc();
        var tokenColor = ForegroundOf(token);
        var identifierColor = ForegroundOf("probe_widgets");
        Assert.NotNull(tokenColor);
        Assert.NotNull(identifierColor);
        Assert.NotEqual(identifierColor, tokenColor);
    }

    [Theory]
    [InlineData("widget_id")]
    [InlineData("label")]
    [InlineData("price")]
    public void Plain_identifiers_share_the_default_color(string identifier)
    {
        EnsureRichDoc();
        Assert.Equal(ForegroundOf("probe_widgets"), ForegroundOf(identifier));
    }
}

// =================================================================================================
// 2) LIVE DIAGNOSTICS — broken SQL surfaces in the Error List as you type (unsaved); valid SQL
//    stays clean. Every broken case also proves the fix clears it.
// =================================================================================================
[Collection("vs")]
public sealed class DiagnosticsScenarios : IDisposable
{
    private readonly VsFixture _vs;
    public DiagnosticsScenarios(VsFixture vs) => _vs = vs;
    public void Dispose() => _vs.ResetBuffer();

    [Theory]
    // -- unresolved relations, in every clause a user writes them ------------------------------
    [InlineData("SELECT 1 FROM sales.zzz_a01;", "zzz_a01")]
    [InlineData("SELECT 1 FROM inventory.zzz_a02;", "zzz_a02")]
    [InlineData("SELECT 1 FROM audit.zzz_a03;", "zzz_a03")]
    [InlineData("SELECT 1 FROM \"sales\".\"zzz_a04\";", "zzz_a04")]
    [InlineData("SELECT o.id FROM sales.orders o JOIN sales.zzz_a05 z ON z.id = o.id;", "zzz_a05")]
    [InlineData("SELECT o.id FROM sales.orders o LEFT JOIN inventory.zzz_a06 z ON z.id = o.id;", "zzz_a06")]
    [InlineData("INSERT INTO sales.zzz_a07 (id) VALUES (1);", "zzz_a07")]
    [InlineData("UPDATE sales.zzz_a08 SET id = 1;", "zzz_a08")]
    [InlineData("DELETE FROM sales.zzz_a09;", "zzz_a09")]
    [InlineData("WITH c AS (SELECT 1 FROM sales.zzz_a10) SELECT * FROM c;", "zzz_a10")]
    [InlineData("SELECT (SELECT count(*) FROM sales.zzz_a11);", "zzz_a11")]
    [InlineData("CREATE VIEW sales.zzz_view_a12 AS SELECT id FROM sales.zzz_a12;", "zzz_a12")]
    // -- parse errors (their messages don't carry the marker — assert on the file's row instead) --
    [InlineData("CREATE TABLE sales.zzz_p01 (id integer", "##file##")]
    [InlineData("CREATE TABLE sales.zzz_p02 (id integer,);", "##file##")]
    [InlineData("CREATE TABLE sales.zzz_p03 (id integer, CONSTRAINT c CHECK (zzz_p03_col > 0));", "zzz_p03_col")]
    // -- duplicate definitions ------------------------------------------------------------------
    [InlineData("CREATE TABLE sales.orders (id integer);", "Duplicate table definition")]
    [InlineData("CREATE VIEW sales.v_open_orders AS SELECT 1;", "Duplicate view definition")]
    public void Broken_sql_is_flagged_while_unsaved_and_clears_after_revert(string sql, string expectedToken)
    {
        if (expectedToken == "##file##") expectedToken = Path.GetFileName(_vs.ViewFilePath);
        _vs.ResetBuffer();
        _vs.AppendLine(sql);
        Assert.True(_vs.WaitForDiagnostic(expectedToken),
            $"expected a diagnostic mentioning '{expectedToken}' for: {sql}");

        // the user reverts the edit → the diagnostic must clear (ResetBuffer asserts the wait)
        _vs.SetBufferText(_vs.BaselineText);
        var file = Path.GetFileName(_vs.ViewFilePath);
        var cleared = Retry.WhileTrue(() => _vs.ErrorListShows(expectedToken),
            timeout: TimeSpan.FromSeconds(15), interval: TimeSpan.FromMilliseconds(500)).Success;
        Assert.True(cleared, $"diagnostic '{expectedToken}' did not clear after reverting {file}");
    }

    [Theory]
    // -- valid statements a user types every day must stay squiggle-free -----------------------
    [InlineData("SELECT id, name FROM sales.customers;")]
    [InlineData("SELECT o.id, c.name FROM sales.orders o JOIN sales.customers c ON c.id = o.customer_id;")]
    [InlineData("SELECT p.sku FROM inventory.products p WHERE NOT p.discontinued;")]
    [InlineData("SELECT count(*) FROM sales.order_lines;")]
    [InlineData("WITH t AS (SELECT id FROM sales.orders) SELECT * FROM t;")]
    [InlineData("SELECT sum(quantity * unit_price) FROM sales.order_lines GROUP BY order_id HAVING sum(quantity) > 1;")]
    [InlineData("SELECT id, row_number() OVER (ORDER BY id) FROM sales.customers;")]
    [InlineData("SELECT CASE WHEN id > 1 THEN 'big' ELSE 'small' END FROM sales.customers;")]
    [InlineData("SELECT id::text FROM sales.customers;")]
    [InlineData("SELECT 1 WHERE EXISTS (SELECT 1 FROM inventory.stock_levels);")]
    [InlineData("SELECT id FROM sales.customers UNION ALL SELECT id FROM sales.orders;")]
    [InlineData("INSERT INTO sales.customers (name) VALUES ('Probe Co');")]
    [InlineData("UPDATE inventory.stock_levels SET quantity = quantity + 1 WHERE warehouse = 'ZG-01';")]
    [InlineData("DELETE FROM audit.order_status_changes WHERE id = -1;")]
    [InlineData("SELECT sales.order_total(1);")]
    [InlineData("-- a lonely comment line")]
    [InlineData("/* block comment with sales.zzz_in_comment, never flagged */")]
    [InlineData("SELECT 'string mentioning sales.zzz_in_literal';")]
    [InlineData("CREATE TABLE sales.zzz_ok_defaults (id integer, who text DEFAULT current_user, at date DEFAULT current_date);")]
    [InlineData("SELECT * FROM information_schema.tables;")]
    [InlineData("SELECT * FROM pg_catalog.pg_class;")]
    public void Valid_sql_produces_no_diagnostics(string sql)
    {
        _vs.ResetBuffer();
        _vs.AppendLine(sql);
        // give the round-trip a moment to (not) produce findings, then assert the file is clean
        System.Threading.Thread.Sleep(5000);
        var file = Path.GetFileName(_vs.ViewFilePath);
        Assert.False(_vs.ErrorListShows(file), $"unexpected diagnostic for valid SQL: {sql}");
    }

}

// =================================================================================================
// 2b) CROSS-FILE — heavyweight second-document churn, ISOLATED in its own VS instance (see the
//     collection definition for why). The original user repro lives here.
// =================================================================================================
[Collection("vs-crossfile")]
public sealed class CrossFileScenarios
{
    private readonly VsFixture _vs;
    public CrossFileScenarios(VsFixture vs) => _vs = vs;

    [Fact]
    public void Deleting_a_table_buffer_breaks_dependents_and_restoring_heals_them()
    {
        // The original repro: a user clears out a table's definition WITHOUT saving — every
        // dependent view must light up; putting it back must heal them.
        _vs.ResetBuffer();
        var tableFile = Directory.EnumerateFiles(Path.GetDirectoryName(_vs.ViewFilePath)!
                .Replace("Views", "Tables"), "*.sql").First();
        _vs.Dte.Invoke(d => d.ItemOperations.OpenFile(tableFile));
        var original = _vs.GetBufferText();

        // The SECOND opened document can hit the same claim race the fixture guards the view
        // against: prove it is wired to the LSP (a broken edit must produce a diagnostic for THIS
        // file) before running the real scenario — close/reopen until it is.
        var tableName = Path.GetFileName(tableFile);
        for (var attempt = 1; attempt <= 4; attempt++)
        {
            _vs.SetBufferText("CREATE TABLE wired_probe (broken\n");
            if (Retry.WhileFalse(() => _vs.ErrorListShows(tableName),
                    timeout: TimeSpan.FromSeconds(15), interval: TimeSpan.FromMilliseconds(500)).Result)
                break;
            _vs.Dte.Invoke(d => d.ActiveDocument.Close(2 /* vsSaveChangesNo */));
            System.Threading.Thread.Sleep(3000);
            _vs.Dte.Invoke(d => d.ItemOperations.OpenFile(tableFile));
            Assert.True(attempt < 4, "the table document never wired to the LSP across 4 open attempts");
        }
        _vs.SetBufferText(original);
        Retry.WhileTrue(() => _vs.ErrorListShows(tableName),
            timeout: TimeSpan.FromSeconds(15), interval: TimeSpan.FromMilliseconds(500));
        try
        {
            _vs.SetBufferText("-- table definition deleted by user\n");
            Assert.True(_vs.WaitForDiagnostic("does not exist", 45),
                "no diagnostic appeared after deleting a depended-on table definition");

            _vs.SetBufferText(original);
            var healed = Retry.WhileTrue(() => _vs.ErrorListShows("does not exist"),
                timeout: TimeSpan.FromSeconds(45), interval: TimeSpan.FromMilliseconds(500)).Success;
            Assert.True(healed, "diagnostics did not clear after restoring the table definition");
        }
        finally
        {
            _vs.SetBufferText(original);
            _vs.Dte.Invoke(d => d.ActiveDocument.Close(2 /* vsSaveChangesNo */));
            _vs.Dte.Invoke(d => d.ItemOperations.OpenFile(_vs.ViewFilePath));
        }
    }
}

// =================================================================================================
// 3) INTELLISENSE — dot- and space-triggered completion offers the right members everywhere.
// =================================================================================================
[Collection("vs")]
public sealed class CompletionScenarios : IDisposable
{
    private readonly VsFixture _vs;
    public CompletionScenarios(VsFixture vs) => _vs = vs;
    public void Dispose()
    {
        _vs.DismissCompletion();
        _vs.ResetBuffer();
    }

    private void AssertCompletionOffers(string lineBeforeTrigger, string trigger, string expectedItem)
    {
        _vs.ResetBuffer();
        _vs.AppendLine(lineBeforeTrigger);
        if (trigger == " ")
        {
            // explicit invoke (Ctrl+Space) — exactly what a user does to "list everything here"
            _vs.TypeInEditor(" ");
            FlaUI.Core.Input.Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.SPACE);
        }
        else
        {
            _vs.TypeInEditor(trigger);
        }
        // Type the item's first letters: VS filters the list, which BOTH mirrors real usage and
        // defeats popup virtualization (UIA only sees rendered rows — late-sorting items like
        // v_open_orders are otherwise permanently off-screen).
        FlaUI.Core.Input.Keyboard.Type(expectedItem.Substring(0, Math.Min(3, expectedItem.Length)));

        // Success is EITHER the popup showing the item OR the editor having auto-committed it:
        // Ctrl+Space (Complete Word) inserts the match directly when the typed prefix is unique.
        var found = Retry.WhileFalse(
            () => _vs.FindCompletionItem(expectedItem) is not null
                  || _vs.GetBufferText().Contains(expectedItem, StringComparison.OrdinalIgnoreCase),
            timeout: TimeSpan.FromSeconds(15), interval: TimeSpan.FromMilliseconds(400)).Result;
        _vs.DismissCompletion();
        Assert.True(found, $"completion after '{lineBeforeTrigger}{trigger}' did not offer '{expectedItem}'");
    }

    [Theory]
    // -- schema members after "schema." ---------------------------------------------------------
    [InlineData("SELECT 1 FROM sales", ".", "orders")]
    [InlineData("SELECT 1 FROM sales", ".", "customers")]
    [InlineData("SELECT 1 FROM sales", ".", "v_open_orders")]
    [InlineData("SELECT 1 FROM sales", ".", "mv_revenue_by_customer")]
    [InlineData("SELECT 1 FROM sales", ".", "order_total")]
    [InlineData("SELECT 1 FROM inventory", ".", "products")]
    [InlineData("SELECT 1 FROM inventory", ".", "stock_levels")]
    [InlineData("SELECT 1 FROM audit", ".", "order_status_changes")]
    [InlineData("SELECT 1 FROM audit", ".", "log_order_status_change")]
    // -- table columns after "table." ------------------------------------------------------------
    [InlineData("SELECT orders", ".", "id")]
    [InlineData("SELECT orders", ".", "order_number")]
    [InlineData("SELECT orders", ".", "customer_id")]
    [InlineData("SELECT orders", ".", "status")]
    [InlineData("SELECT orders", ".", "placed_at")]
    [InlineData("SELECT customers", ".", "name")]
    [InlineData("SELECT customers", ".", "email")]
    [InlineData("SELECT customers", ".", "created_at")]
    [InlineData("SELECT products", ".", "sku")]
    [InlineData("SELECT products", ".", "unit_price")]
    [InlineData("SELECT products", ".", "discontinued")]
    [InlineData("SELECT stock_levels", ".", "warehouse")]
    [InlineData("SELECT stock_levels", ".", "quantity")]
    [InlineData("SELECT order_status_changes", ".", "changed_by")]
    [InlineData("SELECT order_status_changes", ".", "changed_at")]
    // -- alias members after "alias." (FROM sales.orders o → o. lists orders' columns) ----------
    [InlineData("SELECT 1 FROM sales.orders zz1 WHERE zz1", ".", "customer_id")]
    [InlineData("SELECT 1 FROM sales.customers zz2 WHERE zz2", ".", "email")]
    [InlineData("SELECT 1 FROM inventory.products zz3 WHERE zz3", ".", "unit_price")]
    [InlineData("SELECT 1 FROM sales.orders o JOIN sales.customers c ON c", ".", "name")]
    // -- top-level objects + keywords after a space trigger --------------------------------------
    [InlineData("SELECT id FROM", " ", "sales")]
    [InlineData("SELECT id FROM", " ", "inventory")]
    [InlineData("SELECT id FROM", " ", "audit")]
    [InlineData("SELECT id FROM", " ", "orders")]
    [InlineData("SELECT id FROM", " ", "customers")]
    [InlineData("SELECT id", " ", "FROM")]
    [InlineData("SELECT 1 FROM sales.orders", " ", "WHERE")]
    public void Completion_offers_expected_member(string line, string trigger, string expected) =>
        AssertCompletionOffers(line, trigger, expected);
}

// =================================================================================================
// 4) NAVIGATION — F12 lands on the defining file (the LSP definition round-trip).
// =================================================================================================
[Collection("vs")]
public sealed class NavigationScenarios : IDisposable
{
    private readonly VsFixture _vs;
    public NavigationScenarios(VsFixture vs) => _vs = vs;
    public void Dispose()
    {
        // close anything navigation opened, restore the scratch document
        _vs.Dte.Invoke(d => d.ItemOperations.OpenFile(_vs.ViewFilePath));
        _vs.ResetBuffer();
    }

    [Theory]
    [InlineData("SELECT 1 FROM sales.orders;", "orders", "orders.sql")]
    [InlineData("SELECT 1 FROM sales.customers;", "customers", "customers.sql")]
    [InlineData("SELECT 1 FROM inventory.products;", "products", "products.sql")]
    [InlineData("SELECT 1 FROM inventory.stock_levels;", "stock_levels", "stock_levels.sql")]
    [InlineData("SELECT sales.order_total(1);", "order_total", "order_total.sql")]
    // F12 on a query ALIAS lands on the aliased relation's definition
    [InlineData("SELECT zz9.id FROM sales.orders zz9 WHERE zz9.status = 'draft';", "zz9", "orders.sql")]
    [InlineData("SELECT 1 FROM inventory.products prod9 WHERE prod9.sku = 'X';", "prod9", "products.sql")]
    public void Go_to_definition_opens_the_defining_file(string sql, string word, string expectedFile)
    {
        _vs.ResetBuffer();
        _vs.AppendLine(sql);

        // put the caret on the identifier (deterministic line/column on the line just added)
        _vs.PlaceCaretOnLastLineWord(word);
        _vs.TypeInEditor(""); // focus only (verified)
        _vs.PressKey(VirtualKeyShort.F12);

        bool Landed() => _vs.Dte.Invoke<string>(d => d.ActiveDocument.Name)
            .Equals(expectedFile, StringComparison.OrdinalIgnoreCase);
        var landed = Retry.WhileFalse(Landed,
            timeout: TimeSpan.FromSeconds(10), interval: TimeSpan.FromMilliseconds(500)).Result;
        if (!landed)
        {
            // one more press — the first F12 occasionally fires while the definition round-trip
            // (server cold path) is still warming up
            _vs.TypeInEditor("");
            _vs.PressKey(VirtualKeyShort.F12);
            landed = Retry.WhileFalse(Landed,
                timeout: TimeSpan.FromSeconds(15), interval: TimeSpan.FromMilliseconds(500)).Result;
        }
        if (!landed)
        {
            var shot = Path.Combine(Path.GetTempPath(), $"pgproj-uitest-nav-{word}-{DateTime.Now:HHmmss}.png");
            try { FlaUI.Core.Capturing.Capture.Screen().ToFile(shot); } catch { }
            Assert.Fail($"F12 on '{word}' did not open {expectedFile} " +
                $"(active: {_vs.Dte.Invoke<string>(d => d.ActiveDocument.Name)}; screenshot: {shot})");
        }

        _vs.Dte.Invoke(d => { if (d.ActiveDocument.Name != Path.GetFileName(_vs.ViewFilePath)) d.ActiveDocument.Close(2 /* vsSaveChangesNo */); });
    }

    [Fact]
    public void Go_to_definition_on_an_alias_qualified_column_lands_on_the_columns_own_line()
    {
        _vs.ResetBuffer();
        _vs.AppendLine("SELECT zz8.status FROM sales.orders zz8;");
        _vs.PlaceCaretOnLastLineWord("status");
        _vs.TypeInEditor(""); // verified focus
        _vs.PressKey(VirtualKeyShort.F12);

        var landed = Retry.WhileFalse(
            () => _vs.Dte.Invoke<string>(d => d.ActiveDocument.Name)
                      .Equals("orders.sql", StringComparison.OrdinalIgnoreCase),
            timeout: TimeSpan.FromSeconds(20), interval: TimeSpan.FromMilliseconds(500)).Result;
        Assert.True(landed, "F12 on the column segment did not open orders.sql");

        // the caret must sit on the column's own line inside the CREATE TABLE, not on line 1
        var lineText = _vs.Dte.Invoke<string>(d =>
        {
            var sel = d.ActiveDocument.Selection;
            var td = d.ActiveDocument.Object("TextDocument");
            var ep = td.StartPoint.CreateEditPoint();
            ep.MoveToLineAndOffset((int)sel.ActivePoint.Line, 1);
            var eol = td.StartPoint.CreateEditPoint();
            eol.MoveToLineAndOffset((int)sel.ActivePoint.Line, 1);
            eol.EndOfLine();
            return (string)ep.GetText(eol);
        });
        Assert.Contains("status", lineText, StringComparison.OrdinalIgnoreCase);

        _vs.Dte.Invoke(d => { if (d.ActiveDocument.Name != Path.GetFileName(_vs.ViewFilePath)) d.ActiveDocument.Close(2 /* vsSaveChangesNo */); });
    }
}

// =================================================================================================
// 4b) PEEK DEFINITION — Alt+F12 shows the definition INLINE (an embedded editor appears, the
//     active document does not change). Rides the same LSP textDocument/definition as F12.
// =================================================================================================
[Collection("vs")]
public sealed class PeekDefinitionScenarios : IDisposable
{
    private readonly VsFixture _vs;
    public PeekDefinitionScenarios(VsFixture vs) => _vs = vs;
    public void Dispose()
    {
        _vs.PressKey(VirtualKeyShort.ESCAPE); // close any open peek view
        _vs.ResetBuffer();
    }

    [Theory]
    [InlineData("SELECT 1 FROM sales.orders;", "orders", "CREATE TABLE")]
    // expects the QUOTED name: the extract emitter writes `CREATE OR REPLACE VIEW "sales"."v_open_orders"`,
    // so a bare "CREATE VIEW" substring never matches, and the quoted form can't be satisfied by the
    // host document's own (unquoted) usage line.
    [InlineData("SELECT 1 FROM sales.v_open_orders;", "v_open_orders", "\"v_open_orders\"")]
    // peek on a query ALIAS peeks the aliased relation
    [InlineData("SELECT zz7.id FROM sales.orders zz7;", "zz7", "CREATE TABLE")]
    public void Peek_definition_shows_the_definition_inline(string sql, string word, string expectedInPeek)
    {
        _vs.ResetBuffer();
        _vs.AppendLine(sql);
        var docBefore = _vs.Dte.Invoke<string>(d => d.ActiveDocument.Name);
        var editorsBefore = CountEditors();

        _vs.PlaceCaretOnLastLineWord(word);
        _vs.TypeInEditor(""); // verified focus
        FlaUI.Core.Input.Keyboard.TypeSimultaneously(VirtualKeyShort.ALT, VirtualKeyShort.F12);

        // the peek view hosts an EXTRA embedded editor inside the same document window
        var peeked = Retry.WhileFalse(() => CountEditors() > editorsBefore,
            timeout: TimeSpan.FromSeconds(20), interval: TimeSpan.FromMilliseconds(500)).Result;
        Assert.True(peeked, $"Alt+F12 on '{word}' never opened a peek view");

        // still on the same document — peek is inline, not a navigation
        Assert.Equal(docBefore, _vs.Dte.Invoke<string>(d => d.ActiveDocument.Name));

        // and the peeked content is the definition
        var defVisible = Retry.WhileFalse(() =>
        {
            var text = _vs.GetMainWindow()?.FindAllDescendants(cf => cf.ByClassName("WpfTextView"))
                .Skip(1) // first is the host document
                .Select(v => { try { return v.Patterns.Text.Pattern.DocumentRange.GetText(2000); } catch { return ""; } })
                .FirstOrDefault(t => t.Contains(expectedInPeek, StringComparison.OrdinalIgnoreCase));
            return text is not null;
        }, timeout: TimeSpan.FromSeconds(10), interval: TimeSpan.FromMilliseconds(500)).Result;
        Assert.True(defVisible, $"the peek view for '{word}' does not show '{expectedInPeek}'");

        _vs.PressKey(VirtualKeyShort.ESCAPE);
    }

    private int CountEditors() =>
        _vs.GetMainWindow()?.FindAllDescendants(cf => cf.ByClassName("WpfTextView")).Length ?? 0;
}

// =================================================================================================
// 5) EVERYDAY EDITING — dirty tracking, save, undo, multi-document work keep behaving.
// =================================================================================================
[Collection("vs")]
public sealed class EditingScenarios : IDisposable
{
    private readonly VsFixture _vs;
    public EditingScenarios(VsFixture vs) => _vs = vs;
    public void Dispose() => _vs.ResetBuffer();

    [Fact]
    public void Editing_marks_the_document_dirty_and_save_clears_it()
    {
        _vs.ResetBuffer();
        _vs.Dte.Invoke(d => d.ActiveDocument.Save());
        _vs.AppendLine("-- dirty probe");
        Assert.False(_vs.Dte.Invoke<bool>(d => d.ActiveDocument.Saved), "edit did not mark the document dirty");
        _vs.Dte.Invoke(d => d.ActiveDocument.Save());
        Assert.True(_vs.Dte.Invoke<bool>(d => d.ActiveDocument.Saved), "save did not clear the dirty flag");
        _vs.SetBufferText(_vs.BaselineText);
        _vs.Dte.Invoke(d => d.ActiveDocument.Save());
    }

    [Fact]
    public void Typed_characters_actually_land_in_the_buffer()
    {
        _vs.ResetBuffer();
        _vs.AppendLine("-- typing probe:");
        _vs.TypeInEditor(" zz_typed_marker");
        // typed input is asynchronous — poll instead of asserting the instant after the keystrokes
        var landed = Retry.WhileFalse(() => _vs.GetBufferText().Contains("zz_typed_marker"),
            timeout: TimeSpan.FromSeconds(10), interval: TimeSpan.FromMilliseconds(300)).Result;
        Assert.True(landed, "typed characters never appeared in the buffer");
    }

    [Fact]
    public void Undo_restores_the_pristine_content()
    {
        _vs.ResetBuffer();
        var before = _vs.GetBufferText();
        _vs.AppendLine("-- to be undone");
        // undo until the text matches (bounded) — prior scenarios may have left a deep undo stack
        var restored = Retry.WhileFalse(() =>
        {
            if (_vs.GetBufferText().TrimEnd() == before.TrimEnd()) return true;
            _vs.Dte.Invoke(d => { try { d.ExecuteCommand("Edit.Undo", ""); } catch { } });
            return false;
        }, timeout: TimeSpan.FromSeconds(30), interval: TimeSpan.FromMilliseconds(300)).Result;
        Assert.True(restored, "undo never restored the pristine content");
    }

    [Fact]
    public void Switching_between_two_documents_keeps_both_editors_alive()
    {
        _vs.ResetBuffer();
        var tableFile = Directory.EnumerateFiles(
            Path.GetDirectoryName(_vs.ViewFilePath)!.Replace("Views", "Tables"), "*.sql").First();
        _vs.Dte.Invoke(d => d.ItemOperations.OpenFile(tableFile));
        Assert.Contains("CREATE", _vs.GetBufferText());
        _vs.Dte.Invoke(d => d.ItemOperations.OpenFile(_vs.ViewFilePath));
        Assert.Equal(_vs.BaselineText.TrimEnd(), _vs.GetBufferText().TrimEnd());
    }

    [Fact]
    public void Reopening_the_document_keeps_the_postgres_editor()
    {
        _vs.ResetBuffer();
        _vs.Dte.Invoke(d => d.ActiveDocument.Close(2 /* vsSaveChangesNo */));
        _vs.Dte.Invoke(d => d.ItemOperations.OpenFile(_vs.ViewFilePath));
        System.Threading.Thread.Sleep(2000);
        var text = _vs.GetEditor().Patterns.Text.Pattern;
        var kw = text.DocumentRange.FindText("CREATE", false, false)?
            .GetAttributeValue(_vs.Automation.TextAttributeLibrary.ForegroundColor);
        var ident = text.DocumentRange.FindText(_vs.ViewIdentifier, false, false)?
            .GetAttributeValue(_vs.Automation.TextAttributeLibrary.ForegroundColor);
        Assert.NotNull(kw);
        Assert.NotEqual(ident, kw);
    }

    [Fact]
    public void A_long_line_does_not_break_the_editor_or_diagnostics()
    {
        _vs.ResetBuffer();
        var longCols = string.Join(", ", Enumerable.Range(1, 60).Select(i => $"id AS col_{i:D2}"));
        _vs.AppendLine($"SELECT {longCols} FROM sales.customers;");
        System.Threading.Thread.Sleep(4000);
        Assert.False(_vs.ErrorListShows(Path.GetFileName(_vs.ViewFilePath)), "long valid line was flagged");
    }
}

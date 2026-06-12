using System.Linq;
using System.Threading.Tasks;
using PgProj.Lsp.Handlers;
using PgProj.Lsp.Protocol;
using PgProj.Lsp.Workspace;
using Xunit;

namespace PgProj.Lsp.Tests;

/// <summary>definition / hover / completion backed by the project model tree + source-position index.</summary>
public sealed class NavigationHandlerTests
{
    private static (LanguageService Svc, DocumentStore Store, TempProject Tp, string UseUri) Fixture()
    {
        var tp = new TempProject();
        // Two files: a defining table, and a view that references it.
        tp.WriteSql("tables/customer.sql", "CREATE TABLE public.customer (id int, name text);\n");
        var useSql = "CREATE VIEW public.v AS SELECT id FROM public.customer;\n";
        var useUri = tp.UriFor("views/v.sql");
        tp.WriteSql("views/v.sql", useSql);

        var store = new DocumentStore();
        store.Open(useUri, useSql, 1);
        store.Open(tp.UriFor("tables/customer.sql"), "CREATE TABLE public.customer (id int, name text);\n", 1);
        return (new LanguageService(store, tp.ProjectFilePath), store, tp, useUri);
    }

    [Fact]
    public async Task Definition_on_a_query_alias_resolves_to_the_aliased_table()
    {
        var tp = new TempProject();
        using var _tp = tp;
        tp.WriteSql("tables/orders.sql", "CREATE TABLE public.orders (id int, customer_id int);\n");
        var sql = "SELECT o.id FROM public.orders o WHERE o.customer_id = 1;\n";
        var uri = tp.UriFor("queries/q.sql");
        tp.WriteSql("queries/q.sql", sql);
        var store = new DocumentStore();
        store.Open(uri, sql, 1);
        var svc = new LanguageService(store, tp.ProjectFilePath);

        // cursor on the alias usage "o" in "WHERE o.customer_id"
        var col = sql.IndexOf("o.customer_id", System.StringComparison.Ordinal);
        var loc = await svc.DefinitionAsync(uri, new Position(0, col));

        Assert.NotNull(loc);
        Assert.EndsWith("orders.sql", loc!.Uri);
    }

    [Fact]
    public async Task Definition_on_an_alias_qualified_column_lands_on_the_columns_own_line()
    {
        var tp = new TempProject();
        using var _tp = tp;
        // one column per line so the column's line is distinguishable from the CREATE line
        tp.WriteSql("tables/orders.sql",
            "CREATE TABLE public.orders (\n" +
            "    id integer,\n" +
            "    customer_id integer,\n" +   // ← line 2 (0-based)
            "    status text\n" +
            ");\n");
        var sql = "SELECT o.customer_id FROM public.orders o;\n";
        var uri = tp.UriFor("queries/q.sql");
        tp.WriteSql("queries/q.sql", sql);
        var store = new DocumentStore();
        store.Open(uri, sql, 1);
        var svc = new LanguageService(store, tp.ProjectFilePath);

        // caret ON the column segment
        var col = sql.IndexOf("customer_id", System.StringComparison.Ordinal) + 3;
        var loc = await svc.DefinitionAsync(uri, new Position(0, col));

        Assert.NotNull(loc);
        Assert.EndsWith("orders.sql", loc!.Uri);
        Assert.Equal(2, loc.Range.Start.Line);   // the column's own line, not the CREATE line
        Assert.Equal(4, loc.Range.Start.Character);

        // caret ON the alias segment of the same chain → the CREATE line instead
        var aliasCol = sql.IndexOf("o.customer_id", System.StringComparison.Ordinal);
        var relLoc = await svc.DefinitionAsync(uri, new Position(0, aliasCol));
        Assert.NotNull(relLoc);
        Assert.Equal(0, relLoc!.Range.Start.Line);
    }

    [Fact]
    public async Task Definition_on_a_schema_qualified_column_lands_on_the_columns_own_line()
    {
        var tp = new TempProject();
        using var _tp = tp;
        tp.WriteSql("tables/orders.sql",
            "CREATE TABLE public.orders (\n    id integer,\n    status text\n);\n");
        var sql = "SELECT public.orders.status FROM public.orders;\n";
        var uri = tp.UriFor("queries/q.sql");
        tp.WriteSql("queries/q.sql", sql);
        var store = new DocumentStore();
        store.Open(uri, sql, 1);
        var svc = new LanguageService(store, tp.ProjectFilePath);

        var col = sql.IndexOf("status", System.StringComparison.Ordinal) + 2;
        var loc = await svc.DefinitionAsync(uri, new Position(0, col));

        Assert.NotNull(loc);
        Assert.EndsWith("orders.sql", loc!.Uri);
        Assert.Equal(2, loc.Range.Start.Line);
    }

    [Fact]
    public async Task Completion_after_alias_dot_offers_the_aliased_tables_columns()
    {
        var tp = new TempProject();
        using var _tp = tp;
        tp.WriteSql("tables/orders.sql", "CREATE TABLE public.orders (id int, customer_id int, status text);\n");
        var sql = "SELECT 1 FROM public.orders o WHERE o.\n";
        var uri = tp.UriFor("queries/q.sql");
        tp.WriteSql("queries/q.sql", sql);
        var store = new DocumentStore();
        store.Open(uri, sql, 1);
        var svc = new LanguageService(store, tp.ProjectFilePath);

        var col = sql.IndexOf("o.\n", System.StringComparison.Ordinal) + 2; // right after the dot
        var list = await svc.CompletionAsync(uri, new Position(0, col));

        Assert.Contains(list.Items, i => i.Label == "customer_id");
        Assert.Contains(list.Items, i => i.Label == "status");
        Assert.DoesNotContain(list.Items, i => i.Label == "orders"); // columns, not schema members
    }

    [Fact]
    public async Task Hover_on_an_alias_shows_the_aliased_relation()
    {
        var tp = new TempProject();
        using var _tp = tp;
        tp.WriteSql("tables/orders.sql", "CREATE TABLE public.orders (id int);\n");
        var sql = "SELECT o.id FROM public.orders o;\n";
        var uri = tp.UriFor("queries/q.sql");
        tp.WriteSql("queries/q.sql", sql);
        var store = new DocumentStore();
        store.Open(uri, sql, 1);
        var svc = new LanguageService(store, tp.ProjectFilePath);

        var col = sql.IndexOf("o.id", System.StringComparison.Ordinal);
        var hover = await svc.HoverAsync(uri, new Position(0, col));

        Assert.NotNull(hover);
        Assert.Contains("public.orders", hover!.Contents.Value);
    }

    [Fact]
    public async Task Definition_on_a_reference_resolves_to_the_defining_file_and_line()
    {
        var (svc, _, tp, useUri) = Fixture();
        using var _tp = tp;

        // Cursor on "customer" in "FROM public.customer".
        var text = "CREATE VIEW public.v AS SELECT id FROM public.customer;\n";
        var col = text.IndexOf("customer", System.StringComparison.Ordinal) + "cust".Length;
        var loc = await svc.DefinitionAsync(useUri, new Position(0, col));

        Assert.NotNull(loc);
        Assert.EndsWith("customer.sql", loc!.Uri);
        Assert.Equal(0, loc.Range.Start.Line); // CREATE TABLE is on line 1 (0-based 0)
    }

    [Fact]
    public async Task Hover_returns_the_symbol_card()
    {
        var (svc, _, tp, useUri) = Fixture();
        using var _tp = tp;

        var text = "CREATE VIEW public.v AS SELECT id FROM public.customer;\n";
        var col = text.IndexOf("customer", System.StringComparison.Ordinal) + 1;
        var hover = await svc.HoverAsync(useUri, new Position(0, col));

        Assert.NotNull(hover);
        Assert.Contains("table", hover!.Contents.Value, System.StringComparison.OrdinalIgnoreCase);
        Assert.Contains("public.customer", hover.Contents.Value, System.StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Completion_returns_project_symbols_in_scope()
    {
        var (svc, _, tp, useUri) = Fixture();
        using var _tp = tp;

        var list = await svc.CompletionAsync(useUri, new Position(0, 0));
        var labels = list.Items.Select(i => i.Label).ToList();

        Assert.Contains("customer", labels);
        Assert.Contains("v", labels);          // the view
        Assert.Contains("public", labels);     // the schema
    }

    [Fact]
    public async Task Completion_after_table_dot_lists_its_columns()
    {
        var tp = new TempProject();
        using var _tp = tp;
        var sql = "CREATE TABLE public.customer (id int, name text);\nSELECT customer. FROM public.customer;\n";
        var uri = tp.UriFor("c.sql");
        tp.WriteSql("c.sql", sql);

        var store = new DocumentStore();
        store.Open(uri, sql, 1);
        var svc = new LanguageService(store, tp.ProjectFilePath);

        // Cursor right after "customer." on line 2 (0-based line 1).
        var line2 = "SELECT customer. FROM public.customer;";
        var dotCol = line2.IndexOf("customer.", System.StringComparison.Ordinal) + "customer.".Length;
        var list = await svc.CompletionAsync(uri, new Position(1, dotCol));
        var labels = list.Items.Select(i => i.Label).ToList();

        Assert.Contains("id", labels);
        Assert.Contains("name", labels);
    }

    [Fact]
    public async Task References_finds_occurrences_across_project_files()
    {
        var (svc, _, tp, useUri) = Fixture();
        using var _tp = tp;

        // Cursor on "customer" in the view's FROM clause → expect the defining CREATE TABLE
        // occurrence AND the view's reference.
        var text = "CREATE VIEW public.v AS SELECT id FROM public.customer;\n";
        var col = text.IndexOf("customer", System.StringComparison.Ordinal) + 1;
        var refs = await svc.ReferencesAsync(useUri, new Position(0, col));

        Assert.Equal(2, refs.Count);
        Assert.Contains(refs, r => r.Uri.EndsWith("customer.sql"));
        Assert.Contains(refs, r => r.Uri.EndsWith("v.sql"));
    }

    [Fact]
    public async Task References_skips_comments_and_string_literals()
    {
        var tp = new TempProject();
        using var _tp = tp;
        var sql = "CREATE TABLE public.widget (id int);\n"
                + "-- widget in a line comment\n"
                + "/* widget in a /* nested */ block comment */\n"
                + "CREATE VIEW public.w AS SELECT 'widget' AS lit, id FROM widget;\n";
        var uri = tp.UriFor("w.sql");
        tp.WriteSql("w.sql", sql);

        var store = new DocumentStore();
        store.Open(uri, sql, 1);
        var svc = new LanguageService(store, tp.ProjectFilePath);

        var col = sql.IndexOf("public.widget", System.StringComparison.Ordinal) + "public.w".Length;
        var refs = await svc.ReferencesAsync(uri, new Position(0, col));

        // The CREATE TABLE occurrence + the FROM reference; not the comment/literal mentions.
        Assert.Equal(2, refs.Count);
    }

    [Fact]
    public async Task References_matches_quoted_identifiers()
    {
        var tp = new TempProject();
        using var _tp = tp;
        var sql = "CREATE TABLE public.geo (id int);\nCREATE VIEW public.gv AS SELECT id FROM \"geo\";\n";
        var uri = tp.UriFor("g.sql");
        tp.WriteSql("g.sql", sql);

        var store = new DocumentStore();
        store.Open(uri, sql, 1);
        var svc = new LanguageService(store, tp.ProjectFilePath);

        var col = sql.IndexOf("geo", System.StringComparison.Ordinal) + 1;
        var refs = await svc.ReferencesAsync(uri, new Position(0, col));

        Assert.Equal(2, refs.Count); // bare definition + quoted reference
    }
}

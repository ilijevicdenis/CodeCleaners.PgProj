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

using System.Linq;
using PgProj.Core.Model.Identity;
using PgProj.Core.Semantics;
using PgProj.Core.Syntax;
using Xunit;

namespace PgProj.Core.Tests;

/// <summary>
/// Phase 2 (issue #46) global symbol table: overload-keyed functions, reverse lookup, search_path-aware
/// resolution, and identity-carrying entries. All DB-free — built straight from PgParser output.
/// </summary>
public sealed class SymbolTableTests
{
    // ---- Overloaded functions resolve independently by signature -----------------------------------

    [Fact]
    public void Overloaded_functions_resolve_independently_by_signature()
    {
        var c = CatalogBuilder.Build(
            "CREATE FUNCTION app.f(a int) RETURNS int AS $$ SELECT a $$ LANGUAGE sql;\n" +
            "CREATE FUNCTION app.f(a text) RETURNS text AS $$ SELECT a $$ LANGUAGE sql;", "app");

        var fInt = c.Symbols.ResolveFunction("app", "f", new FunctionSignature("integer"));
        var fText = c.Symbols.ResolveFunction("app", "f", new FunctionSignature("text"));

        Assert.NotNull(fInt);
        Assert.NotNull(fText);
        Assert.NotEqual(fInt!.Key, fText!.Key);                 // two distinct symbols
        Assert.Equal(SymbolKind.Function, fInt.Kind);

        // Both overloads enumerable under the one name; a wrong signature does not resolve.
        Assert.Equal(2, c.Symbols.FunctionOverloads("app", "f").Count);
        Assert.Null(c.Symbols.ResolveFunction("app", "f", new FunctionSignature("boolean")));

        // The existence-set API still answers the unqualified-name probe (back-compat).
        Assert.True(c.HasFunction("f"));
        Assert.True(c.HasFunctionOverload("f", new FunctionSignature("integer")));
        Assert.True(c.HasFunctionOverload("f", new FunctionSignature("text")));
    }

    // ---- Reverse lookup returns all referencers of a symbol ----------------------------------------

    [Fact]
    public void Reverse_lookup_returns_all_referencers_of_a_symbol()
    {
        var c = CatalogBuilder.Build(
            "CREATE TABLE app.t (id int, name text);\n" +
            "CREATE VIEW app.v1 AS SELECT id FROM app.t;\n" +
            "CREATE VIEW app.v2 AS SELECT name FROM app.t;", "app");

        ReferenceCollector.Collect(c, new PgParser().Parse(
            "CREATE VIEW app.v1 AS SELECT id FROM app.t;\n" +
            "CREATE VIEW app.v2 AS SELECT name FROM app.t;"), "schema.sql");

        var t = c.Symbols.ResolveQualified("app", "t");
        Assert.NotNull(t);

        var referencers = c.Symbols.ReferencesTo(t!);
        Assert.Equal(2, referencers.Count);
        Assert.Contains(referencers, r => r.ReferencerKey == "app.v1");
        Assert.Contains(referencers, r => r.ReferencerKey == "app.v2");
        Assert.All(referencers, r => Assert.Equal("schema.sql", r.ReferencerFile));

        // An unreferenced symbol has no referencers.
        var v1 = c.Symbols.ResolveQualified("app", "v1");
        Assert.Empty(c.Symbols.ReferencesTo(v1!));
    }

    // ---- search_path resolution: unqualified via path == qualified direct --------------------------

    [Fact]
    public void Search_path_resolves_unqualified_and_qualified_to_same_entry()
    {
        // Default schema "app" → search_path is "$user"(=app), public. A table created unqualified lands
        // in app; an unqualified read resolves it via the path, a qualified read resolves it directly, and
        // both reach the very same symbol entry.
        var c = CatalogBuilder.Build("CREATE TABLE app.users (id int);", "app");

        var qualified = c.Symbols.ResolveQualified("app", "users");
        var viaPath = c.Symbols.ResolveUnqualified("users", SymbolKind.Relation, c.SearchPath);

        Assert.NotNull(qualified);
        Assert.NotNull(viaPath);
        Assert.Same(qualified, viaPath);                        // symmetric: same object

        // A name only present in a schema NOT on the path does not resolve unqualified.
        var c2 = CatalogBuilder.Build("CREATE TABLE other.thing (id int);", "app");
        Assert.NotNull(c2.Symbols.ResolveQualified("other", "thing"));
        Assert.Null(c2.Symbols.ResolveUnqualified("thing", SymbolKind.Relation, c2.SearchPath));
    }

    [Fact]
    public void Search_path_walks_schemas_in_order()
    {
        // public is second on the default path; an object only in public still resolves unqualified.
        var c = CatalogBuilder.Build("CREATE TABLE public.shared (id int);", "app");
        var viaPath = c.Symbols.ResolveUnqualified("shared", SymbolKind.Relation, c.SearchPath);
        Assert.NotNull(viaPath);
        Assert.Same(c.Symbols.ResolveQualified("public", "shared"), viaPath);
    }

    [Fact]
    public void DollarUser_token_expands_to_default_schema()
    {
        var path = SearchPath.Default("app");
        Assert.Equal("app", path.Head);
        Assert.Equal(new[] { "app", "public" }, path.Schemas);
        Assert.Equal("app", path.CurrentUserSchema);
    }

    // ---- A symbol entry carries its StableId from the Identity Model -------------------------------

    [Fact]
    public void Symbol_entry_carries_StableId_from_identity_model()
    {
        var c = CatalogBuilder.Build("CREATE TABLE app.t (id int, name text);", "app");
        var t = c.Symbols.ResolveQualified("app", "t");
        Assert.NotNull(t);
        Assert.NotEqual(default, t!.StableId);                   // a real StableId was stamped

        // It is the SAME StableId the Identity Model computes for the structurally-equal table — name-
        // independent, so a pure rename preserves it.
        var same = CatalogBuilder.Build("CREATE TABLE app.renamed (id int, name text);", "app")
            .Symbols.ResolveQualified("app", "renamed");
        Assert.Equal(t.StableId, same!.StableId);                // rename preserves StableId

        // A structural change (a different column type) flips it.
        var changed = CatalogBuilder.Build("CREATE TABLE app.t (id bigint, name text);", "app")
            .Symbols.ResolveQualified("app", "t");
        Assert.NotEqual(t.StableId, changed!.StableId);
    }

    [Fact]
    public void Function_overload_entries_carry_distinct_StableIds_from_signature()
    {
        var c = CatalogBuilder.Build(
            "CREATE FUNCTION app.f(a int) RETURNS int AS $$ SELECT a $$ LANGUAGE sql;\n" +
            "CREATE FUNCTION app.f(a text) RETURNS text AS $$ SELECT a $$ LANGUAGE sql;", "app");

        var fInt = c.Symbols.ResolveFunction("app", "f", new FunctionSignature("integer"))!;
        var fText = c.Symbols.ResolveFunction("app", "f", new FunctionSignature("text"))!;
        Assert.NotEqual(default, fInt.StableId);
        Assert.NotEqual(fInt.StableId, fText.StableId);          // distinct signatures → distinct identity
    }

    // ---- Column entries carry resolved type metadata (Phase 4/5 seed) ------------------------------

    [Fact]
    public void Column_entries_carry_resolved_type_metadata()
    {
        var c = CatalogBuilder.Build("CREATE TABLE app.t (id int, name varchar(50));", "app");

        var cols = c.ColumnsWithTypes("app", "t");
        Assert.NotNull(cols);
        Assert.Equal("integer", cols!.First(x => x.Name == "id").Type);          // normalized
        Assert.Equal("character varying(50)", cols.First(x => x.Name == "name").Type);

        var idCol = c.Symbols.Entries.FirstOrDefault(e => e.Kind == SymbolKind.Column && e.Name == "id");
        Assert.NotNull(idCol);
        Assert.Equal("integer", idCol!.ColumnType);
        Assert.Equal("app.t.id", idCol.Fqn);

        // Existing name-only Columns() API is unchanged.
        Assert.Equal(new[] { "id", "name" }, c.Columns("app", "t"));
    }
}

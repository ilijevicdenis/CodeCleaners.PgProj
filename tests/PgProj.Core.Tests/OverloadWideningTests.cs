using System.Linq;
using PgProj.Core.Semantics;
using PgProj.Core.Semantics.Binding;
using PgProj.Core.Syntax;
using Xunit;

namespace PgProj.Core.Tests;

/// <summary>
/// Overload resolution vs integer literals (audit P1): every whole-number literal was typed bigint and
/// then EXACT-string matched, so fn(42) against a lone-among-many fn(integer) overload reported a false
/// "no function matches". Literals now type by PG's smallest-fit rule and resolution gets an unambiguous
/// numeric-widening pass.
/// </summary>
public class OverloadWideningTests
{
    private static System.Collections.Generic.IReadOnlyList<PgProj.Core.Diagnostics.Diagnostic> Validate(string sql)
    {
        var parsed = new PgParser().Parse(sql);
        var catalog = CatalogBuilder.Build(parsed);
        var validator = new SemanticValidator(catalog);
        validator.IndexFile("f.sql", sql, parsed);
        var diags = validator.Validate("f.sql", sql, parsed);
        parsed.ReleaseTokens();
        return diags;
    }

    [Fact]
    public void Small_integer_literal_matches_the_integer_overload()
    {
        // Two overloads → the sole-overload fallback can't save it; the exact/widened match must work.
        var diags = Validate(@"
            CREATE FUNCTION public.f(x integer) RETURNS int LANGUAGE sql AS $$ SELECT 1 $$;
            CREATE FUNCTION public.f(x text) RETURNS int LANGUAGE sql AS $$ SELECT 2 $$;
            CREATE TABLE public.t (id int);
            CREATE VIEW public.v AS SELECT public.f(42) FROM public.t;");
        Assert.DoesNotContain(diags, d => d.Message.Contains("f", System.StringComparison.OrdinalIgnoreCase)
                                          && d.Message.Contains("match", System.StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Integer_literal_widens_to_a_bigint_overload()
    {
        var diags = Validate(@"
            CREATE FUNCTION public.g(x bigint) RETURNS int LANGUAGE sql AS $$ SELECT 1 $$;
            CREATE FUNCTION public.g(x text) RETURNS int LANGUAGE sql AS $$ SELECT 2 $$;
            CREATE TABLE public.t (id int);
            CREATE VIEW public.v AS SELECT public.g(5) FROM public.t;");
        Assert.DoesNotContain(diags, d => d.Message.Contains("match", System.StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Literal_typing_follows_pg_smallest_fit()
    {
        static string TypeOf(string literal)
        {
            var parsed = new PgParser().Parse($"SELECT {literal};");
            var q = ((QueryStatement)parsed.Statements.Single()).Query;
            var binder = new Binder(CatalogBuilder.Build(new PgParser().Parse("")));
            var bound = binder.BindQuery(q);
            parsed.ReleaseTokens();
            return bound.SelectItems.Single().Type.Name;
        }

        Assert.Equal("integer", TypeOf("42"));
        Assert.Equal("integer", TypeOf("2147483647"));
        Assert.Equal("bigint", TypeOf("2147483648"));
        Assert.Equal("numeric", TypeOf("99999999999999999999999999"));   // exceeds int8
        Assert.Equal("numeric", TypeOf("1.5"));
        Assert.Equal("integer", TypeOf("0xFF"));
    }
}

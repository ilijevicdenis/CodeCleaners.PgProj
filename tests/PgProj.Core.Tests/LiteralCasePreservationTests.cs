using System.Linq;
using PgProj.Core.Comparison;
using Xunit;

namespace PgProj.Core.Tests;

/// <summary>
/// Canonicalizer.NormalizeText lower-cased string-literal CONTENT (audit P1): a case-only literal edit
/// in a default / CHECK / view / function body compared equal — the deploy silently never happened.
/// Case is now preserved inside '…' literals; keywords/identifiers outside still fold.
/// </summary>
public class LiteralCasePreservationTests
{
    [Fact]
    public void Literal_content_keeps_its_case_while_keywords_fold()
    {
        Assert.Equal("select 'ACTIVE' from t", Canonicalizer.NormalizeText("SELECT 'ACTIVE'  FROM T"));
        Assert.Equal("default 'Y'", Canonicalizer.NormalizeText("DEFAULT 'Y'"));
        // '' doubling stays inside the literal; the text after it is still literal content
        Assert.Equal("'It''S OK' and x", Canonicalizer.NormalizeText("'It''S OK' AND X"));
    }

    [Fact]
    public void Case_only_literal_edit_now_surfaces_as_a_change()
    {
        var upper = TestModel.Build("CREATE TABLE public.t (s text DEFAULT 'ACTIVE');");
        var lower = TestModel.Build("CREATE TABLE public.t (s text DEFAULT 'active');");
        Assert.NotEmpty(new SchemaComparer().Compare(upper, lower));

        var upperCheck = TestModel.Build("CREATE TABLE public.t (s text, CHECK (s = 'A'));");
        var lowerCheck = TestModel.Build("CREATE TABLE public.t (s text, CHECK (s = 'a'));");
        Assert.NotEmpty(new SchemaComparer().Compare(upperCheck, lowerCheck));

        var upperView = TestModel.Build("CREATE VIEW public.v AS SELECT 'YES' AS a;");
        var lowerView = TestModel.Build("CREATE VIEW public.v AS SELECT 'yes' AS a;");
        Assert.NotEmpty(new SchemaComparer().Compare(upperView, lowerView));
    }

    [Fact]
    public void Keyword_case_and_whitespace_still_compare_equal()
    {
        var shouty = TestModel.Build("CREATE VIEW public.v AS SELECT   'x' AS a FROM public.t;");
        var quiet = TestModel.Build("create view public.v as select 'x' as a from public.t;");
        Assert.Empty(new SchemaComparer().Compare(shouty, quiet));
    }
}

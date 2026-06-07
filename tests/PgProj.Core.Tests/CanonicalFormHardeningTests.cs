using System.Linq;
using PgProj.Core.Comparison;
using PgProj.Core.Extensibility;
using PgProj.Core.Model;
using PgProj.Core.Model.Identity;
using Xunit;

namespace PgProj.Core.Tests;

/// <summary>
/// Acceptance tests for issue #51 — canonical semantic model hardening. All DB-free.
///
/// Covers: (1) the canonical EXPRESSION form (paren-folding + operator spacing + type aliases) makes
/// reformatted-but-equivalent CHECK/default edits hash identically via <see cref="ObjectIdentityComputer"/>;
/// (2) <see cref="IProjectObject.Canonicalize"/> returns the exact form CanonicalHash is derived from;
/// (3) the gated, default-off column-order normalization.
/// </summary>
public sealed class CanonicalFormHardeningTests
{
    private static readonly ObjectIdentityComputer Computer = new();

    // ---- canonical expression form: paren/whitespace/case/alias-insensitive --------------------------

    [Theory]
    [InlineData("a>0", "(a>0)")]
    [InlineData("a>0", "( a > 0 )")]
    [InlineData("a>0", "((a > 0))")]
    [InlineData("amount >= 0", "(amount>=0)")]
    [InlineData("status = 'active'", "(status = 'active')")]
    public void Equivalent_check_expressions_share_one_canonical_form(string a, string b) =>
        Assert.Equal(Canonicalizer.NormalizeExpression(a), Canonicalizer.NormalizeExpression(b));

    [Fact]
    public void NormalizeExpression_does_not_strip_non_enclosing_parens()
    {
        // "(a)>(b)" is NOT wholly enclosed — the leading '(' closes mid-string — so it must survive,
        // and must remain distinct from a genuinely different predicate.
        Assert.Equal("(a)>(b)", Canonicalizer.NormalizeExpression("( (a) > (b) )"));
        Assert.NotEqual(Canonicalizer.NormalizeExpression("(a)>(b)"), Canonicalizer.NormalizeExpression("(a)<(b)"));
    }

    [Fact]
    public void NormalizeExpression_is_idempotent()
    {
        var once = Canonicalizer.NormalizeExpression("( (PRICE) > 0 )");
        Assert.Equal(once, Canonicalizer.NormalizeExpression(once));
    }

    // ---- the property test the issue's Acceptance calls for: reformat + reorder ⇒ identical hash -----

    [Fact]
    public void Reformatted_and_alias_equivalent_table_yields_identical_CanonicalHash()
    {
        // Tight project spelling: aliased types (int/varchar), no spacing, bare CHECK.
        var a = TestModel.Build(
            "CREATE TABLE s.t (id int NOT NULL, name varchar(50), qty int CHECK (qty>0) DEFAULT 0);", "s")
            .Tables.Single();

        // Equivalent but reformatted: canonical types, wide spacing, parenthesised CHECK, spaced default.
        var b = TestModel.Build(
            "CREATE  TABLE  s.t (\n  id    integer NOT NULL,\n  name  character varying(50),\n" +
            "  qty   integer CHECK ((qty > 0)) DEFAULT ( 0 )\n);", "s")
            .Tables.Single();

        Assert.Equal(Computer.CanonicalHashOf(a), Computer.CanonicalHashOf(b));
    }

    [Fact]
    public void Paren_only_check_difference_does_not_flip_CanonicalHash()
    {
        var a = TestModel.Build("CREATE TABLE s.t (id int, n int CHECK (n>0));", "s").Tables.Single();
        var b = TestModel.Build("CREATE TABLE s.t (id int, n int CHECK ((n > 0)));", "s").Tables.Single();
        Assert.Equal(Computer.CanonicalHashOf(a), Computer.CanonicalHashOf(b));
    }

    [Fact]
    public void A_real_check_change_still_flips_CanonicalHash()
    {
        var a = TestModel.Build("CREATE TABLE s.t (id int, n int CHECK (n>0));", "s").Tables.Single();
        var b = TestModel.Build("CREATE TABLE s.t (id int, n int CHECK (n>1));", "s").Tables.Single();
        Assert.NotEqual(Computer.CanonicalHashOf(a), Computer.CanonicalHashOf(b));
    }

    // ---- canonicalization lives in the model: IProjectObject.Canonicalize == the hashed form ---------

    [Fact]
    public void IProjectObject_Canonicalize_is_a_faithful_proxy_for_Hash()
    {
        // Canonicalize() now returns the SAME canonical FORM that Hash() is derived from (issue #51 #3),
        // so two cosmetically-different-but-equivalent objects agree on BOTH, and a real change flips BOTH.
        var aModel = TestModel.Build(
            "CREATE TABLE s.t (id int NOT NULL, qty int CHECK (qty>0)); CREATE VIEW s.v AS SELECT id FROM s.t;", "s");
        var bModel = TestModel.Build(
            "CREATE  TABLE s.t (id integer NOT NULL, qty integer CHECK ((qty > 0)));  CREATE VIEW s.v AS\n SELECT id FROM s.t;", "s");
        var cModel = TestModel.Build(
            "CREATE TABLE s.t (id int NOT NULL, qty int CHECK (qty>5)); CREATE VIEW s.v AS SELECT id FROM s.t;", "s");

        var a = new ProjectObjectRegistry(aModel).All.ToList();
        var b = new ProjectObjectRegistry(bModel).All.ToList();
        var c = new ProjectObjectRegistry(cModel).All.ToList();

        foreach (var (oa, ob) in a.Zip(b))
        {
            Assert.Equal(oa.Canonicalize(), ob.Canonicalize());
            Assert.Equal(oa.Hash(), ob.Hash());
        }

        // The table's CHECK changed (qty>0 → qty>5): its Canonicalize AND Hash must both differ.
        var ta = a.First(o => o.Kind == "table");
        var tc = c.First(o => o.Kind == "table");
        Assert.NotEqual(ta.Canonicalize(), tc.Canonicalize());
        Assert.NotEqual(ta.Hash(), tc.Hash());
    }

    [Fact]
    public void Canonicalize_normalizes_a_column_built_bypassing_TypeNormalizer()
    {
        // A hand-built (introspection-style) table with an aliased type spelling — TypeNormalizer was
        // never applied to the field. Canonicalize() must still fold int → integer and varchar → ...
        var introspected = new TableDefinition
        {
            Schema = "s", Name = "t",
            Columns = { new ColumnDefinition("id", "int", IsNullable: false),
                        new ColumnDefinition("name", "varchar(50)", IsNullable: true) },
        };
        var parsed = TestModel.Build(
            "CREATE TABLE s.t (id integer NOT NULL, name character varying(50));", "s").Tables.Single();

        var computer = new ObjectIdentityComputer();
        Assert.Equal(
            new TableProjectObjectProbe(parsed, computer).Form,
            new TableProjectObjectProbe(introspected, computer).Form);
    }

    // Small probe to reach CanonicalFormOf via the public accessor (TableProjectObject is internal).
    private readonly struct TableProjectObjectProbe
    {
        public string Form { get; }
        public TableProjectObjectProbe(TableDefinition t, ObjectIdentityComputer c) => Form = c.CanonicalFormOf(t);
    }

    // ---- gated column-order normalization: OFF by default, ON when enabled ---------------------------

    [Fact]
    public void Column_order_is_significant_by_default()
    {
        var ab = TestModel.Build("CREATE TABLE s.t (a int, b int);", "s").Tables.Single();
        var ba = TestModel.Build("CREATE TABLE s.t (b int, a int);", "s").Tables.Single();

        var def = new ObjectIdentityComputer(); // default options
        Assert.NotEqual(def.CanonicalHashOf(ab), def.CanonicalHashOf(ba));
        Assert.NotEqual(def.StableIdOf(ab), def.StableIdOf(ba));
    }

    [Fact]
    public void Column_order_is_ignored_when_the_option_is_enabled()
    {
        var ab = TestModel.Build("CREATE TABLE s.t (a int, b int);", "s").Tables.Single();
        var ba = TestModel.Build("CREATE TABLE s.t (b int, a int);", "s").Tables.Single();

        var ignore = new ObjectIdentityComputer(new CanonicalFormOptions { IgnoreColumnOrder = true });
        Assert.Equal(ignore.CanonicalHashOf(ab), ignore.CanonicalHashOf(ba));
        Assert.Equal(ignore.StableIdOf(ab), ignore.StableIdOf(ba));
    }

    [Fact]
    public void Enabling_column_order_ignoring_does_not_change_the_default_computers_verdict()
    {
        // Guard: constructing an ignore-order computer must not perturb the default one (no shared state).
        var t = TestModel.Build("CREATE TABLE s.t (a int, b int);", "s").Tables.Single();
        var def1 = new ObjectIdentityComputer().CanonicalHashOf(t);
        _ = new ObjectIdentityComputer(new CanonicalFormOptions { IgnoreColumnOrder = true }).CanonicalHashOf(t);
        var def2 = new ObjectIdentityComputer().CanonicalHashOf(t);
        Assert.Equal(def1, def2);
    }
}

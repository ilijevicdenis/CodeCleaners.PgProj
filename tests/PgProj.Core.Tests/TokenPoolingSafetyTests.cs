using System.Linq;
using System.Text;
using PgProj.Core.Syntax;
using Xunit;

namespace PgProj.Core.Tests;

/// <summary>
/// Guards the token-pooling safety contract (audit F3). <c>ParseResult.ReleaseTokens</c> returns the
/// rented <c>Token[]</c> to the shared <see cref="System.Buffers.ArrayPool{T}"/> WITHOUT clearing it, on
/// the invariant that every statement's source-segment view has been dropped first, so no live reader can
/// ever observe a recycled buffer. That invariant is hand-maintained across the parser; these tests pin it
/// down so a future refactor that breaks "drop-then-return" produces a red test, not silent cross-parse
/// data bleed. Inputs are sized above the pool threshold (2048 chars) so pooling actually engages.
/// </summary>
public class TokenPoolingSafetyTests
{
    // A raw/unsupported statement (COMMENT ON) whose SourceText IS rendered from the token segment — the
    // path that would expose a recycled buffer if the contract broke. Repeated to clear the pool threshold.
    private static string BigScript(string marker)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < 120; i++)
            sb.Append($"COMMENT ON TABLE app.{marker}_{i:D3} IS 'note for {marker} row {i}';\n");
        return sb.ToString();
    }

    [Fact]
    public void ReleaseTokens_then_reparse_does_not_bleed_previous_tokens()
    {
        var first = BigScript("alpha_marker");
        var second = BigScript("beta_marker");
        Assert.True(first.Length > 2048 && second.Length > 2048, "inputs must exceed the pool threshold");

        // Parse A and return its pooled buffer to the shared pool.
        var a = new PgParser().Parse(first);
        a.ReleaseTokens();

        // Parse B — likely renting the very array A just returned — then force every statement to render
        // its SourceText from the (now B's) buffer. It must read B's tokens, never A's recycled ones.
        var b = new PgParser().Parse(second);
        var rendered = string.Concat(b.Statements.Select(s => s.SourceText ?? ""));

        Assert.Contains("beta_marker", rendered);
        Assert.DoesNotContain("alpha_marker", rendered);
    }

    [Fact]
    public void ReleaseTokens_drops_unread_SourceText_to_null_not_garbage()
    {
        var a = new PgParser().Parse(BigScript("gamma_marker"));
        a.ReleaseTokens();   // segments dropped, buffer returned — SourceText was never read

        // A statement whose SourceText was never materialized must read back as null after release,
        // never as another parse's recycled token text.
        Assert.All(a.Statements, s => Assert.Null(s.SourceText));
    }

    [Fact]
    public void ReleaseTokens_is_idempotent()
    {
        var a = new PgParser().Parse(BigScript("delta_marker"));
        a.ReleaseTokens();
        var ex = Record.Exception(() => a.ReleaseTokens());   // second call must be a harmless no-op
        Assert.Null(ex);
    }
}

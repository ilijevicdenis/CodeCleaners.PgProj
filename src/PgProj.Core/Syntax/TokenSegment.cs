using System.Collections;
using System.Collections.Generic;
using PgProj.Core.Parsing;

namespace PgProj.Core.Syntax;

/// <summary>
/// A read-only window over a slice of a parent token list — the per-statement segment produced by
/// <c>PgParser.SplitStatements</c>. It replaces the old "copy each statement's tokens into a fresh
/// <c>List&lt;Token&gt;</c>" approach, which was measured (AllocProbe) at ~7 MB/op on the All bucket —
/// ~25% of the grammar+render allocation — almost all of it <c>Token[]</c> backing arrays churned by the
/// lists' 1→4→8→… doubling growth. A segment is never mutated after creation (the grammar only reads it
/// through a <see cref="TokenCursor"/> and renders it lazily), so a window with no backing array of its
/// own is sufficient and allocates a single small object instead.
///
/// <para>It is a <b>class</b>, not a struct, on purpose: it is consumed as <see cref="IReadOnlyList{Token}"/>
/// (by <see cref="TokenCursor"/> and the lazy <c>SourceText</c>), and a struct would box on each such use —
/// two allocations per statement — defeating the point. One shared reference allocates once.</para>
/// </summary>
public sealed class TokenSegment : IReadOnlyList<Token>
{
    private readonly IReadOnlyList<Token> _source;
    private readonly int _start;

    public TokenSegment(IReadOnlyList<Token> source, int start, int count)
    {
        _source = source;
        _start = start;
        Count = count;
    }

    public int Count { get; }

    public Token this[int index] => _source[_start + index];

    public IEnumerator<Token> GetEnumerator()
    {
        for (var i = 0; i < Count; i++) yield return _source[_start + i];
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

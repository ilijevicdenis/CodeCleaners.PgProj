using System.Buffers;
using System.Collections;
using System.Collections.Generic;

namespace PgProj.Core.Parsing;

/// <summary>
/// The parser's per-parse token stream, backed by an array rented from <see cref="ArrayPool{T}"/>. It is
/// exposed as <see cref="IReadOnlyList{Token}"/> so the existing segment/cursor/render code reads it
/// unchanged. The backing array is returned to the pool by <see cref="Return"/>, which the parse pipeline
/// calls (via <c>ParseResult.ReleaseTokens</c>) ONLY after the model is built and every statement has
/// dropped its source-segment view — so at the moment of return there is no live reader, and a recycled
/// array can never be observed (this is the whole safety contract; see the class remarks below).
///
/// <para>Drop-then-return is what makes pooling safe here: a lazy <c>SourceText</c> segment references this
/// stream, so the array cannot be returned while any segment is live. <c>ReleaseTokens</c> nulls all
/// segments first, then calls <see cref="Return"/>. Callers that never Return (unit tests, ad-hoc parses)
/// simply let the GC reclaim the array — correct, just unpooled. Reading after Return yields an empty
/// stream, never another parse's tokens.</para>
/// </summary>
public sealed class PooledTokens : IReadOnlyList<Token>
{
    private Token[] _array;
    private readonly bool _pooled;   // false for small inputs (plain array) → Return is a no-op
    public int Count { get; private set; }

    internal PooledTokens(Token[] array, int count, bool pooled) { _array = array; Count = count; _pooled = pooled; }

    public Token this[int index] => _array[index];   // index < Count by contract (segments/cursor honor Count)

    internal Token[] Array => _array;
    internal void SetCount(int count) => Count = count;   // after the in-place operator-merge compaction

    /// <summary>Return the backing array to the pool. Idempotent. After this the stream is empty.</summary>
    public void Return()
    {
        var a = _array;
        if (!_pooled || a.Length == 0) return;   // small-input plain array → nothing to return
        _array = System.Array.Empty<Token>();
        Count = 0;
        ArrayPool<Token>.Shared.Return(a);   // Tokens are structs holding only a string ref; no clear needed for correctness
    }

    public IEnumerator<Token> GetEnumerator() { for (var i = 0; i < Count; i++) yield return _array[i]; }
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

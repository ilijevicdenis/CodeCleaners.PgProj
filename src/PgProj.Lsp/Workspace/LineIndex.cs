using System;
using System.Collections.Generic;

namespace PgProj.Lsp.Workspace;

/// <summary>
/// Bidirectional map between a 0-based character offset in a document and a (line, character) position.
/// LSP positions are 0-based; the engine's diagnostics/positions are 1-based — this type works in 0-based
/// LSP terms and exposes a 1-based view for engine interop. Built once per analysed buffer revision.
/// </summary>
public sealed class LineIndex
{
    private readonly string _text;
    private readonly int[] _lineStarts; // _lineStarts[i] = char offset where (0-based) line i begins

    public LineIndex(string text)
    {
        _text = text;
        var starts = new List<int> { 0 };
        for (var i = 0; i < text.Length; i++)
            if (text[i] == '\n') starts.Add(i + 1);
        _lineStarts = starts.ToArray();
    }

    public string Text => _text;
    public int LineCount => _lineStarts.Length;

    /// <summary>0-based (line, character) → 0-based character offset, clamped to the buffer.</summary>
    public int OffsetOf(int line, int character)
    {
        if (line < 0) return 0;
        if (line >= _lineStarts.Length) return _text.Length;
        var start = _lineStarts[line];
        var end = line + 1 < _lineStarts.Length ? _lineStarts[line + 1] - 1 : _text.Length; // exclude the \n
        return Math.Clamp(start + Math.Max(0, character), start, Math.Max(start, end));
    }

    /// <summary>0-based character offset → 0-based (line, character).</summary>
    public (int Line, int Character) PositionOf(int offset)
    {
        offset = Math.Clamp(offset, 0, _text.Length);
        // binary search for the greatest line start ≤ offset
        var lo = 0;
        var hi = _lineStarts.Length - 1;
        while (lo < hi)
        {
            var mid = (lo + hi + 1) / 2;
            if (_lineStarts[mid] <= offset) lo = mid; else hi = mid - 1;
        }
        return (lo, offset - _lineStarts[lo]);
    }

    /// <summary>1-based (line, column) — the engine's coordinate space — → 0-based char offset.</summary>
    public int OffsetOfOneBased(int line, int column) => OffsetOf(line - 1, Math.Max(0, column - 1));

    /// <summary>
    /// The identifier-ish token under/just-before a 0-based offset, plus its [start,end) span. SQL identifiers
    /// here are <c>[A-Za-z0-9_.]</c> runs (dotted so <c>schema.table</c> resolves as one word). Returns an empty
    /// word with a zero-width span when the cursor is not on an identifier.
    /// </summary>
    public (string Word, int Start, int End) WordAt(int offset)
    {
        offset = Math.Clamp(offset, 0, _text.Length);
        bool IsWord(char c) => char.IsLetterOrDigit(c) || c == '_' || c == '.';

        var start = offset;
        // If the cursor is just past the end of a word (common: caret after typing), step back one.
        if (start > 0 && (start == _text.Length || !IsWord(_text[start])) && IsWord(_text[start - 1]))
            start--;
        if (start < _text.Length && !IsWord(_text[start]))
            return ("", offset, offset);

        var s = start;
        while (s > 0 && IsWord(_text[s - 1])) s--;
        var e = start;
        while (e < _text.Length && IsWord(_text[e])) e++;
        return (_text[s..e], s, e);
    }
}

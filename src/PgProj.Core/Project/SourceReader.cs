using System.IO;

namespace PgProj.Core.Project;

/// <summary>
/// The single point that reads a <c>.sql</c> source file off disk for the build/analysis pipeline.
/// Source text is normalised to LF (<c>\n</c>) line endings at load time so the parsed model — and
/// every artifact that embeds source text verbatim (<c>model.json</c>, deploy scripts, function
/// bodies) — is byte-identical regardless of whether the working tree was checked out with CRLF or
/// LF (issue #62, M1 determinism). Normalisation happens here, before tokenizing, so the parser's
/// statement offsets and the source positions derived from them are line-ending-independent too.
/// </summary>
public static class SourceReader
{
    /// <summary>Reads <paramref name="file"/> and returns its text with CRLF/CR folded to LF.</summary>
    public static string ReadAllText(string file) => NormalizeLineEndings(File.ReadAllText(file));

    /// <summary>
    /// Folds Windows (<c>\r\n</c>) and classic-Mac (<c>\r</c>) line endings to Unix (<c>\n</c>).
    /// A no-op (returns the same instance) when the text already contains no carriage returns, so the
    /// common already-LF checkout pays only one scan and no allocation.
    /// </summary>
    public static string NormalizeLineEndings(string text)
    {
        if (text.IndexOf('\r') < 0) return text;
        return text.Replace("\r\n", "\n").Replace('\r', '\n');
    }
}

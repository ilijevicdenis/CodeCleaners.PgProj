// EP-VS — syntax coloring for PostgreSQL .sql buffers (the "pgsql" content type). The content type
// derives from plain "code", so without a classifier the buffer renders as uncolored text. This is a
// self-contained lexical classifier (net472 cannot reference the net10 engine): PG keywords/types,
// line and NESTED block comments, standard/E''/dollar-quoted strings, quoted identifiers, numbers and
// operators, mapped onto the standard classification types so every VS theme colors them natively.
using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using Microsoft.VisualStudio.Language.StandardClassification;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Classification;
using Microsoft.VisualStudio.Utilities;

namespace PgProj.VisualStudio.ProjectSystem.Editors
{
    [Export(typeof(IClassifierProvider))]
    [ContentType(PgSqlContentType.Name)]
    internal sealed class PgSqlClassifierProvider : IClassifierProvider
    {
        [Import]
        internal IStandardClassificationService Standard { get; set; }

        public IClassifier GetClassifier(ITextBuffer textBuffer) =>
            textBuffer.Properties.GetOrCreateSingletonProperty(() => new PgSqlClassifier(textBuffer, Standard));
    }

    internal sealed class PgSqlClassifier : IClassifier
    {
        private readonly ITextBuffer _buffer;
        private readonly IStandardClassificationService _standard;

        // The full-buffer tokenization, cached per snapshot (object files are small — one DB object
        // per file — so a whole-buffer rescan on edit is cheap and keeps multi-line state trivially
        // correct: a /* opened on line 1 colors everything below it without line-state bookkeeping).
        private ITextSnapshot _tokenizedSnapshot;
        private List<ClassificationSpan> _spans = new List<ClassificationSpan>();

        public PgSqlClassifier(ITextBuffer buffer, IStandardClassificationService standard)
        {
            _buffer = buffer;
            _standard = standard;
            _buffer.Changed += OnBufferChanged;
        }

        public event EventHandler<ClassificationChangedEventArgs> ClassificationChanged;

        private void OnBufferChanged(object sender, TextContentChangedEventArgs e)
        {
            // An edit can flip multi-line state (open/close a block comment or dollar-quote), which
            // re-colors everything below it — invalidate from the first change to the end of the buffer.
            var first = int.MaxValue;
            foreach (var c in e.Changes)
                if (c.NewPosition < first) first = c.NewPosition;
            if (first == int.MaxValue) return;
            ClassificationChanged?.Invoke(this, new ClassificationChangedEventArgs(
                new SnapshotSpan(e.After, first, e.After.Length - first)));
        }

        public IList<ClassificationSpan> GetClassificationSpans(SnapshotSpan span)
        {
            if (_tokenizedSnapshot != span.Snapshot)
            {
                _spans = Tokenize(span.Snapshot);
                _tokenizedSnapshot = span.Snapshot;
            }

            var result = new List<ClassificationSpan>();
            foreach (var s in _spans)
                if (s.Span.IntersectsWith(span))
                    result.Add(s);
            return result;
        }

        private List<ClassificationSpan> Tokenize(ITextSnapshot snapshot)
        {
            var text = snapshot.GetText();
            var spans = new List<ClassificationSpan>();
            void Add(int start, int end, IClassificationType type)
            {
                if (end > start)
                    spans.Add(new ClassificationSpan(new SnapshotSpan(snapshot, start, end - start), type));
            }

            var i = 0;
            while (i < text.Length)
            {
                var c = text[i];

                // -- line comment
                if (c == '-' && i + 1 < text.Length && text[i + 1] == '-')
                {
                    var start = i;
                    while (i < text.Length && text[i] != '\n') i++;
                    Add(start, i, _standard.Comment);
                    continue;
                }

                // /* block comment */ — nested, per PostgreSQL
                if (c == '/' && i + 1 < text.Length && text[i + 1] == '*')
                {
                    var start = i;
                    var depth = 1;
                    i += 2;
                    while (i < text.Length && depth > 0)
                    {
                        if (text[i] == '/' && i + 1 < text.Length && text[i + 1] == '*') { depth++; i += 2; }
                        else if (text[i] == '*' && i + 1 < text.Length && text[i + 1] == '/') { depth--; i += 2; }
                        else i++;
                    }
                    Add(start, i, _standard.Comment);
                    continue;
                }

                // 'string' with '' escapes; E'string' additionally honors backslash escapes
                if (c == '\'' || ((c == 'e' || c == 'E') && i + 1 < text.Length && text[i + 1] == '\''))
                {
                    var start = i;
                    var escaped = c != '\'';
                    if (escaped) i++; // the E prefix
                    i++;              // the opening quote
                    while (i < text.Length)
                    {
                        if (escaped && text[i] == '\\') { i += 2; continue; }
                        if (text[i] == '\'')
                        {
                            if (i + 1 < text.Length && text[i + 1] == '\'') { i += 2; continue; }
                            i++;
                            break;
                        }
                        i++;
                    }
                    Add(start, i, _standard.StringLiteral);
                    continue;
                }

                // $tag$ dollar-quoted string $tag$ (function bodies) — only when a valid closer-style
                // tag opens here; otherwise $1, $$ in the middle of text, etc. fall through below.
                if (c == '$')
                {
                    var tagEnd = i + 1;
                    while (tagEnd < text.Length && (char.IsLetterOrDigit(text[tagEnd]) || text[tagEnd] == '_')) tagEnd++;
                    if (tagEnd < text.Length && text[tagEnd] == '$' && (tagEnd == i + 1 || !char.IsDigit(text[i + 1])))
                    {
                        var tag = text.Substring(i, tagEnd - i + 1);
                        var close = text.IndexOf(tag, tagEnd + 1, StringComparison.Ordinal);
                        var end = close < 0 ? text.Length : close + tag.Length;
                        Add(i, end, _standard.StringLiteral);
                        i = end;
                        continue;
                    }
                }

                // "quoted identifier"
                if (c == '"')
                {
                    var start = i;
                    i++;
                    while (i < text.Length && text[i] != '"') i++;
                    if (i < text.Length) i++;
                    Add(start, i, _standard.SymbolDefinition);
                    continue;
                }

                // number (digits, decimal point, exponent — close enough lexically)
                if (char.IsDigit(c) || (c == '.' && i + 1 < text.Length && char.IsDigit(text[i + 1])))
                {
                    var start = i;
                    while (i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] == '.'
                        || ((text[i] == '+' || text[i] == '-') && (text[i - 1] == 'e' || text[i - 1] == 'E')))) i++;
                    Add(start, i, _standard.NumberLiteral);
                    continue;
                }

                // bare identifier / keyword
                if (char.IsLetter(c) || c == '_')
                {
                    var start = i;
                    while (i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] == '_')) i++;
                    var word = text.Substring(start, i - start);
                    if (Keywords.Contains(word))
                        Add(start, i, _standard.Keyword);
                    continue;
                }

                // operators / punctuation
                if (OperatorChars.IndexOf(c) >= 0)
                {
                    var start = i;
                    while (i < text.Length && OperatorChars.IndexOf(text[i]) >= 0) i++;
                    Add(start, i, _standard.Operator);
                    continue;
                }

                i++;
            }
            return spans;
        }

        private const string OperatorChars = "+-*/<>=~!@#%^&|?";

        /// <summary>
        /// PostgreSQL reserved words, common statement/clause keywords and built-in type names — one
        /// set, all colored as keywords (the SSDT/SSMS convention). Case-insensitive.
        /// </summary>
        private static readonly HashSet<string> Keywords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            // statements / DDL
            "select", "insert", "update", "delete", "merge", "create", "alter", "drop", "truncate",
            "table", "view", "materialized", "index", "sequence", "function", "procedure", "trigger",
            "schema", "type", "domain", "extension", "policy", "rule", "comment", "owner", "grant",
            "revoke", "vacuum", "analyze", "explain", "copy", "call", "do", "prepare", "execute",
            "deallocate", "declare", "fetch", "move", "close", "listen", "notify", "unlisten", "lock",
            "checkpoint", "cluster", "reindex", "refresh", "reset", "set", "show", "discard",
            // transactions
            "begin", "start", "transaction", "commit", "rollback", "savepoint", "release", "abort",
            // clauses / operators
            "from", "where", "group", "by", "having", "order", "limit", "offset", "returning", "with",
            "recursive", "union", "intersect", "except", "distinct", "all", "as", "on", "using", "join",
            "inner", "left", "right", "full", "outer", "cross", "lateral", "natural", "values", "into",
            "asc", "desc", "nulls", "first", "last", "case", "when", "then", "else", "end", "filter",
            "over", "partition", "window", "range", "rows", "groups", "fetch", "only", "ties",
            // predicates / logic
            "and", "or", "not", "null", "true", "false", "unknown", "is", "isnull", "notnull", "in",
            "exists", "between", "like", "ilike", "similar", "any", "some", "array", "cast", "collate",
            "at", "zone", "interval", "overlaps", "current_date", "current_time", "current_timestamp",
            "current_user", "session_user", "localtime", "localtimestamp", "current_catalog",
            "current_schema", "current_role", "user", "coalesce", "nullif", "greatest", "least",
            // constraints / table options
            "primary", "key", "foreign", "references", "unique", "check", "default", "constraint",
            "exclude", "deferrable", "initially", "deferred", "immediate", "generated", "always",
            "identity", "stored", "virtual", "collation", "inherits", "partition", "of", "for",
            "cascade", "restrict", "no", "action", "match", "simple", "partial", "concurrently",
            "temporary", "temp", "unlogged", "if", "replace", "column", "add", "rename", "to",
            "validate", "enable", "disable", "owned", "owner", "tablespace", "storage", "compression",
            // functions / procedural
            "returns", "return", "language", "immutable", "stable", "volatile", "strict", "security",
            "definer", "invoker", "parallel", "safe", "unsafe", "restricted", "cost", "setof",
            "out", "inout", "variadic", "leakproof", "support", "transform", "called", "input",
            "loop", "while", "foreach", "exit", "continue", "raise", "exception", "notice", "warning",
            "perform", "get", "diagnostics", "open", "cursor", "elsif", "elseif", "assert",
            // triggers / events
            "before", "after", "instead", "each", "row", "statement", "old", "new", "referencing",
            "insert", "execute", "row_security",
            // types
            "int", "int2", "int4", "int8", "integer", "smallint", "bigint", "serial", "smallserial",
            "bigserial", "numeric", "decimal", "real", "double", "precision", "float", "float4",
            "float8", "money", "char", "character", "varying", "varchar", "text", "bytea", "boolean",
            "bool", "bit", "date", "time", "timestamp", "timestamptz", "timetz", "without", "uuid",
            "json", "jsonb", "xml", "cidr", "inet", "macaddr", "macaddr8", "point", "line", "lseg",
            "box", "path", "polygon", "circle", "tsvector", "tsquery", "oid", "regclass", "regproc",
            "regtype", "name", "void", "record", "trigger", "anyelement", "anyarray", "vector",
        };
    }
}

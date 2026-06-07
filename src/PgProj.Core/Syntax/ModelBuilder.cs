using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using PgProj.Core.Contracts;
using PgProj.Core.Model;
using PgProj.Core.Parsing;

namespace PgProj.Core.Syntax;

/// <summary>
/// Lowers PgParser output into the <see cref="DatabaseModel"/> the comparer/emitter consume. Finely
/// modelled kinds (table/view/sequence/index/function) come from their structured nodes; everything
/// else is captured as a <see cref="RawObjectDefinition"/> with the same stable identity the legacy
/// pipeline used, so a project-vs-live round-trip still nets zero diff. Replaces Ast.ModelBuilder.
/// </summary>
public sealed class ModelBuilder
{
    private readonly string _defaultSchema;
    public ModelBuilder(string defaultSchema = "public") => _defaultSchema = defaultSchema;

    public DatabaseModel Build(ParseResult result) { var m = new DatabaseModel(); Build(result, m); return m; }

    public void Build(ParseResult result, DatabaseModel model) => Build(result, model, null, null, null);

    /// <summary>
    /// Lowers a parse result into <paramref name="model"/> and, when <paramref name="positions"/> is
    /// supplied, persists each object's source anchor (file:line:col) into it during the SAME pass —
    /// so IDE navigation / diagnostics / model-tree resolve file+line without a second parse
    /// (issue #45). <paramref name="sourceText"/> is the (already LF-normalised) file text and
    /// <paramref name="relativeFile"/> its project-relative path; both are required when an index is given.
    /// </summary>
    public void Build(ParseResult result, DatabaseModel model,
        SourcePositionIndex? positions, string? sourceText, string? relativeFile)
    {
        foreach (var stmt in result.Statements)
        {
            if (positions is not null && sourceText is not null && relativeFile is not null)
                positions.RecordStatement(stmt, sourceText, relativeFile, _defaultSchema);

            switch (stmt)
            {
                case CreateSchemaStatement s when s.Name is not null: EnsureSchema(model, s.Name); break;
                case CreateTableStatement { IsPartitionOrTyped: true } s: AddRawObject(model, ObjectKind.Table, Sch(s.Schema), s.Name, $"table:{Sch(s.Schema)}.{s.Name}", s.SourceText ?? ""); break;
                case CreateTableStatement s: AddTable(model, s); break;
                case CreateViewStatement s: model.Views.Add(new ViewDefinition(Sch(s.Schema), s.Name, s.BodyText, s.Materialized)); EnsureSchema(model, Sch(s.Schema)); break;
                case CreateSequenceStatement s: model.Sequences.Add(new SequenceDefinition(Sch(s.Schema), s.Name, s.DataType, s.Increment, s.MinValue, s.MaxValue, s.Start, s.Cache, s.Cycle)); EnsureSchema(model, Sch(s.Schema)); break;
                case CreateIndexStatement s: model.Indexes.Add(new IndexDefinition(s.Name ?? $"{s.Table}_idx", Sch(s.Schema), s.Table, s.Columns, s.Unique, s.Method, s.Where)); EnsureSchema(model, Sch(s.Schema)); break;
                case CreateFunctionStatement s:
                    model.Functions.Add(new FunctionDefinition(Sch(s.Schema), s.Name, $"{Sch(s.Schema)}.{s.Name}({s.ArgTypes})", s.SourceText ?? "", s.ArgTypes));
                    EnsureSchema(model, Sch(s.Schema)); break;
                case RawCreateStatement or UnsupportedStatement:
                    var raw = DeriveRaw(stmt.SourceText ?? "");
                    if (raw is not null) { model.Objects.Add(raw); if (!string.IsNullOrEmpty(raw.Schema)) EnsureSchema(model, raw.Schema); }
                    break;
            }
        }
    }

    private string Sch(string? s) => string.IsNullOrEmpty(s) ? _defaultSchema : s!;

    private void AddTable(DatabaseModel model, CreateTableStatement s)
    {
        var table = new TableDefinition { Schema = Sch(s.Schema), Name = s.Name, TrailingOptions = s.TrailingText };
        foreach (var col in s.Columns)
        {
            var typeText = col.Type.Text;
            var isSerial = IsSerial(typeText);
            var nullable = !isSerial;
            string? def = null, idKind = null, generated = null;
            var identity = false;
            foreach (var c in col.Constraints)
            {
                switch (c)
                {
                    case NotNullConstraint: nullable = false; break;
                    case NullConstraint: nullable = true; break;
                    case DefaultConstraint d: def = d.Expression; break;
                    case InlinePrimaryKey: table.PrimaryKey = new PrimaryKeyDefinition(null, new[] { col.Name }); nullable = false; break;
                    case InlineUnique: table.Unique.Add(new UniqueConstraintDefinition(null, new[] { col.Name })); break;
                    case InlineReferences r: table.ForeignKeys.Add(new ForeignKeyDefinition(null, new[] { col.Name }, Sch(r.RefSchema), r.RefTable, r.RefColumns, r.OnDelete?.Action, r.OnUpdate?.Action)); break;
                    case GeneratedIdentity g: identity = true; idKind = g.Kind; break;
                    case GeneratedStored g: generated = g.Expression; break;
                    case InlineCheck ch: table.Checks.Add(new CheckConstraintDefinition(ch.Name, ch.Expression)); break;
                }
            }
            table.Columns.Add(new ColumnDefinition(col.Name, TypeNormalizer.Normalize(typeText), nullable, def, identity, idKind, generated, isSerial));
        }
        foreach (var tc in s.Constraints)
        {
            switch (tc)
            {
                case PrimaryKeyConstraint pk: table.PrimaryKey = new PrimaryKeyDefinition(pk.Name, pk.Columns); break;
                case UniqueConstraint u: table.Unique.Add(new UniqueConstraintDefinition(u.Name, u.Columns)); break;
                case ForeignKeyConstraint fk: table.ForeignKeys.Add(new ForeignKeyDefinition(fk.Name, fk.Columns, Sch(fk.RefSchema), fk.RefTable, fk.RefColumns, fk.OnDelete?.Action, fk.OnUpdate?.Action)); break;
                case CheckConstraint ch: table.Checks.Add(new CheckConstraintDefinition(ch.Name, ch.Expression)); break;
                case ExcludeConstraint ex: table.OtherConstraints.Add((ex.Name is null ? "" : $"CONSTRAINT {ex.Name} ") + "EXCLUDE " + ex.RawText); break;
                case NotNullTableConstraint nn: { var c = table.FindColumn(nn.Column); if (c is not null) table.Columns[table.Columns.IndexOf(c)] = c with { IsNullable = false }; break; }
            }
        }
        EnsureSchema(model, table.Schema);
        model.Tables.Add(table);
    }

    private static bool IsSerial(string typeText)
    {
        var t = typeText.Trim().ToLowerInvariant();
        return t is "serial" or "bigserial" or "smallserial" or "serial4" or "serial8" or "serial2";
    }

    private static void EnsureSchema(DatabaseModel model, string schema)
    {
        if (!string.IsNullOrEmpty(schema) && !model.HasSchema(schema)) model.Schemas.Add(new SchemaDefinition(schema));
    }

    private static void AddRawObject(DatabaseModel model, ObjectKind kind, string schema, string name, string identity, string body)
        => model.Objects.Add(new RawObjectDefinition(kind, schema, name, identity, body));

    // ---- raw-object identity (ported from the legacy parser so round-trip stays zero-diff) -------

    private static RawObjectDefinition? DeriveRaw(string sourceText)
    {
        if (string.IsNullOrWhiteSpace(sourceText)) return null;
        // Re-tokenize to an always-pooled buffer returned immediately. DeriveRaw only *reads* the stream
        // through the cursor (RenderRange/ExpectIdentifier materialise their own strings — nothing retains
        // the buffer), so the rented Token[] goes straight back to the pool (≈0 retained) instead of the old
        // copied-out List<Token>+backing array. Tokens are unmerged (identical to the old Tokenizer.Tokenize
        // path): a pure container swap. TokenizeTransient (not TokenizePooled) so small raw statements — the
        // majority — still pool rather than allocating a one-shot heap array.
        var pooled = Tokenizer.TokenizeTransient(sourceText);
        try
        {
            var cur = new TokenCursor(pooled);

            if (cur.AtWord("COMMENT"))
            {
                cur.MatchWord("COMMENT"); cur.MatchWord("ON");
                int m = cur.Mark();
                while (!cur.AtEnd && !cur.AtWord("IS")) cur.Advance();
                return new RawObjectDefinition(ObjectKind.Comment, "", "", $"comment:{Normalize(cur.RenderRange(m, cur.Mark()))}", sourceText);
            }
            if (cur.AtWord("SECURITY"))
                return new RawObjectDefinition(ObjectKind.Comment, "", "", $"securitylabel:{Normalize(sourceText)}", sourceText, BodyComparable: true);

            if (!cur.MatchWord("CREATE")) return null;
            cur.MatchWords("OR", "REPLACE");
            while (cur.AtAnyWord("GLOBAL", "LOCAL", "TEMP", "TEMPORARY", "UNLOGGED", "TRUSTED", "PROCEDURAL", "RECURSIVE")) cur.Advance();

            var kind = DetectRawKind(cur);
            if (kind is null) return null;

            string schema = "", name = "", onObject = "";
            try
            {
                // Name-parse strategy comes from the object-kind registry (issue #44), not a per-kind
                // switch: a kind that reuses an existing style needs only its registry row. The branch
                // below is keyed by NameParseStyle (the genuinely distinct CREATE-syntax shapes).
                switch (Extensibility.ObjectKindRegistry.Get(kind.Value).NameParse)
                {
                    case Extensibility.NameParseStyle.SchemaQualified:
                        SkipIfNotExists(cur); (schema, name) = Qual(cur); break;
                    case Extensibility.NameParseStyle.GlobalName:
                        SkipIfNotExists(cur); name = cur.ExpectIdentifier(); break;
                    case Extensibility.NameParseStyle.TableScopedOn:
                        name = cur.ExpectIdentifier(); onObject = ScanThenQual(cur, "ON"); schema = SchemaOf(onObject); break;
                    case Extensibility.NameParseStyle.TableScopedTo:
                        name = cur.ExpectIdentifier(); onObject = ScanThenQual(cur, "TO"); schema = SchemaOf(onObject); break;
                    case Extensibility.NameParseStyle.Aggregate:
                        // Leave `schema` empty and fold it into `name` (like Operator/Cast): the name must
                        // carry the schema + arg signature so the identity is `aggregate:<schema>.<name>(<args>)`,
                        // matching the live reader. Setting `schema` here too would double it via BuildIdentity
                        // (`aggregate:afd.afd.sum_int(integer)`) and make every aggregate read as a phantom create.
                        { var (sc, an) = Qual(cur); name = $"{sc}.{an}" + ParenArgs(cur); break; }
                    case Extensibility.NameParseStyle.Operator:
                        name = CaptureUntilOpenParen(cur) + ParenArgs(cur); break;
                    case Extensibility.NameParseStyle.OperatorClassFamily:
                        { var (sc, ocn) = Qual(cur); var method = ScanThenIdent(cur, "USING"); schema = sc; name = $"{sc}.{ocn}" + (method.Length > 0 ? $" USING {method}" : ""); break; }
                    case Extensibility.NameParseStyle.Cast:
                        name = ParenArgs(cur); break;
                    case Extensibility.NameParseStyle.Transform:
                        cur.MatchWord("FOR"); var type = CaptureUntilWord(cur, "LANGUAGE"); var lang = cur.MatchWord("LANGUAGE") ? cur.ExpectIdentifier() : ""; name = $"FOR {type} LANGUAGE {lang}"; break;
                    case Extensibility.NameParseStyle.UserMapping:
                        SkipIfNotExists(cur); cur.MatchWord("FOR"); var usr = cur.ExpectIdentifier(); var srv = ScanThenIdent(cur, "SERVER"); name = $"FOR {usr} SERVER {srv}"; break;
                    case Extensibility.NameParseStyle.BodyBased:
                        break; // no structured name → body-based identity (table/comment)
                }
            }
            catch (ParseException) { /* fall back to body-based identity */ }

            var identity = !string.IsNullOrEmpty(name)
                ? BuildIdentity(kind.Value, schema, name, onObject)
                : $"{kind}:{Normalize(sourceText)}".ToLowerInvariant();
            return new RawObjectDefinition(kind.Value, schema, name, identity, sourceText,
                string.IsNullOrEmpty(onObject) ? null : onObject);
        }
        finally { pooled.Return(); }
    }

    private static ObjectKind? DetectRawKind(TokenCursor c)
    {
        if (c.AtEnd) return null;
        var first = c.Advance().Value.ToUpperInvariant();
        switch (first)
        {
            case "EXTENSION": return ObjectKind.Extension;
            case "LANGUAGE": return ObjectKind.Language;
            case "TYPE": return ObjectKind.Type;
            case "DOMAIN": return ObjectKind.Domain;
            case "COLLATION": return ObjectKind.Collation;
            case "CONVERSION": return ObjectKind.Conversion;
            case "CAST": return ObjectKind.Cast;
            case "AGGREGATE": return ObjectKind.Aggregate;
            case "TRIGGER": return ObjectKind.Trigger;
            case "RULE": return ObjectKind.Rule;
            case "POLICY": return ObjectKind.Policy;
            case "PUBLICATION": return ObjectKind.Publication;
            case "STATISTICS": return ObjectKind.Statistics;
            case "SERVER": return ObjectKind.Server;
            case "TRANSFORM": return ObjectKind.Transform;
            case "CONSTRAINT": return c.MatchWord("TRIGGER") ? ObjectKind.Trigger : null;
            case "OPERATOR":
                if (c.MatchWord("CLASS")) return ObjectKind.OperatorClass;
                if (c.MatchWord("FAMILY")) return ObjectKind.OperatorFamily;
                return ObjectKind.Operator;
            case "EVENT": return c.MatchWord("TRIGGER") ? ObjectKind.EventTrigger : null;
            case "FOREIGN":
                if (c.MatchWord("TABLE")) return ObjectKind.ForeignTable;
                if (c.MatchWord("DATA")) { c.MatchWord("WRAPPER"); return ObjectKind.ForeignDataWrapper; }
                return null;
            case "USER": return c.MatchWord("MAPPING") ? ObjectKind.UserMapping : null;
            case "TEXT":
                if (!c.MatchWord("SEARCH")) return null;
                if (c.MatchWord("CONFIGURATION")) return ObjectKind.TextSearchConfiguration;
                if (c.MatchWord("DICTIONARY")) return ObjectKind.TextSearchDictionary;
                if (c.MatchWord("PARSER")) return ObjectKind.TextSearchParser;
                if (c.MatchWord("TEMPLATE")) return ObjectKind.TextSearchTemplate;
                return null;
            default: return null;
        }
    }

    private static string BuildIdentity(ObjectKind kind, string schema, string name, string onObject)
    {
        // The whole result is lowercased once at the end, so `tag` is left mixed-case (its own
        // ToLowerInvariant was redundant) and the intermediate `qualified` string is folded into the
        // interpolation — one interpolation + one ToLowerInvariant per identity instead of up to three strings.
        var tag = kind.ToString();
        if (!string.IsNullOrEmpty(onObject)) return $"{tag}:{name} on {onObject}".ToLowerInvariant();
        return string.IsNullOrEmpty(schema)
            ? $"{tag}:{name}".ToLowerInvariant()
            : $"{tag}:{schema}.{name}".ToLowerInvariant();
    }

    private static (string, string) Qual(TokenCursor c)
    {
        var first = c.ExpectIdentifier();
        if (c.MatchSymbol('.')) return (first, c.ExpectIdentifier());
        return ("", first);
    }

    private static void SkipIfNotExists(TokenCursor c) => c.MatchWords("IF", "NOT", "EXISTS");
    private static string SchemaOf(string qualified) { var dot = qualified.IndexOf('.'); return dot > 0 ? qualified[..dot] : ""; }

    private static string ScanThenQual(TokenCursor c, string kw)
    {
        while (!c.AtEnd && !c.AtWord(kw)) c.Advance();
        if (!c.MatchWord(kw)) return "";
        var (s, n) = Qual(c);
        return string.IsNullOrEmpty(s) ? n : $"{s}.{n}";
    }

    private static string ScanThenIdent(TokenCursor c, string kw)
    {
        while (!c.AtEnd && !c.AtWord(kw)) c.Advance();
        return c.MatchWord(kw) ? c.ExpectIdentifier() : "";
    }

    private static string CaptureUntilOpenParen(TokenCursor c)
    {
        int m = c.Mark();
        while (!c.AtEnd && !c.AtSymbol('(')) c.Advance();
        return c.RenderRange(m, c.Mark());
    }

    private static string CaptureUntilWord(TokenCursor c, string kw)
    {
        int m = c.Mark();
        while (!c.AtEnd && !c.AtWord(kw)) c.Advance();
        return c.RenderRange(m, c.Mark());
    }

    private static string ParenArgs(TokenCursor c)
    {
        if (!c.AtSymbol('(')) return "";
        int m = c.Mark();
        c.Advance();
        int depth = 1;
        while (!c.AtEnd && depth > 0) { var t = c.Advance(); if (t.IsSymbol('(')) depth++; else if (t.IsSymbol(')')) depth--; }
        return c.RenderRange(m, c.Mark());
    }

    private static string Normalize(string s)
    {
        var sb = new StringBuilder(s.Length);
        bool prevSpace = false;
        foreach (var ch in s.AsSpan().Trim())   // span Trim: no intermediate trimmed-string allocation
        {
            if (char.IsWhiteSpace(ch)) { if (!prevSpace) sb.Append(' '); prevSpace = true; }
            else { sb.Append(char.ToLowerInvariant(ch)); prevSpace = false; }
        }
        return sb.ToString();
    }
}

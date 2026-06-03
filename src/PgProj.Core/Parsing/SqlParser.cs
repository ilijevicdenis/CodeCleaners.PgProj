using System;
using System.Collections.Generic;
using System.Linq;
using PgProj.Core.Model;

namespace PgProj.Core.Parsing;

/// <summary>
/// A pragmatic recursive-descent parser for the <em>declarative</em> subset of Postgres DDL that
/// a database project is built from: CREATE SCHEMA / TABLE / INDEX / VIEW / SEQUENCE / FUNCTION.
/// Non-CREATE statements (ALTER, GRANT, COMMENT, DML) are ignored on purpose — a project states
/// the desired end state, exactly as SSDT's model build does. Statements it cannot parse are
/// recorded in <see cref="Diagnostics"/> rather than aborting the whole build.
/// </summary>
public sealed class SqlParser
{
    private static readonly HashSet<string> ConstraintStartKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "NOT", "NULL", "DEFAULT", "PRIMARY", "UNIQUE", "REFERENCES",
        "CHECK", "CONSTRAINT", "GENERATED", "COLLATE",
    };

    private readonly string _defaultSchema;

    public SqlParser(string defaultSchema = "public") => _defaultSchema = defaultSchema;

    public List<string> Diagnostics { get; } = new();

    public DatabaseModel Parse(string sql)
    {
        var model = new DatabaseModel();
        ParseInto(model, sql);
        return model;
    }

    public void ParseInto(DatabaseModel model, string sql)
    {
        var tokens = Tokenizer.Tokenize(sql);
        foreach (var stmt in SplitStatements(tokens))
        {
            try
            {
                ParseStatement(model, stmt);
            }
            catch (ParseException ex)
            {
                var snippet = Token.Render(stmt.Take(8).ToList());
                Diagnostics.Add($"{ex.Message}  (near: {snippet}…)");
            }
        }
    }

    private static IEnumerable<List<Token>> SplitStatements(IReadOnlyList<Token> tokens)
    {
        var current = new List<Token>();
        var depth = 0;
        foreach (var t in tokens)
        {
            if (t.IsSymbol('(')) depth++;
            else if (t.IsSymbol(')')) depth = Math.Max(0, depth - 1);

            if (t.IsSymbol(';') && depth == 0)
            {
                if (current.Count > 0) yield return current;
                current = new List<Token>();
                continue;
            }
            current.Add(t);
        }
        if (current.Count > 0) yield return current;
    }

    private void ParseStatement(DatabaseModel model, List<Token> tokens)
    {
        var r = new TokenReader(tokens);

        // COMMENT ON ... is not a CREATE but defines persistent schema metadata.
        if (r.IsWord("COMMENT")) { ParseComment(model, tokens); return; }

        if (!r.MatchWord("CREATE"))
            return; // declarative model: only CREATE (and COMMENT) statements define objects

        var unique = false;
        var materialized = false;
        while (r.Cur is { Kind: TokenKind.Word } w)
        {
            var kw = w.Value.ToUpperInvariant();
            if (kw == "OR") { r.MatchWord("OR"); r.MatchWord("REPLACE"); continue; }
            if (kw is "TEMP" or "TEMPORARY" or "UNLOGGED" or "GLOBAL" or "LOCAL") { r.Next(); continue; }
            if (kw == "MATERIALIZED") { r.Next(); materialized = true; continue; }
            if (kw == "UNIQUE") { r.Next(); unique = true; continue; }
            if (kw == "CONCURRENTLY") { r.Next(); continue; }
            break;
        }

        var kindTok = r.Cur;
        if (kindTok is not { Kind: TokenKind.Word })
            return;

        switch (kindTok.Value.ToUpperInvariant())
        {
            case "SCHEMA": r.Next(); ParseSchema(model, r); return;
            case "TABLE": r.Next(); ParseTable(model, r); return;
            case "INDEX": r.Next(); ParseIndex(model, r, unique); return;
            case "VIEW": r.Next(); ParseView(model, r, materialized); return;
            case "SEQUENCE": r.Next(); ParseSequence(model, r); return;
            case "FUNCTION":
            case "PROCEDURE": r.Next(); ParseFunction(model, r, tokens); return;
        }

        // Everything else (extensions, types, domains, triggers, policies, …) is captured by the
        // generic raw-object mechanism so it is still modelled and scriptable.
        var kind = DetectRawKind(r);
        if (kind is not null)
            ParseRawObject(model, kind.Value, tokens, r);
    }

    // ---- CREATE SCHEMA -------------------------------------------------------------------

    private void ParseSchema(DatabaseModel model, TokenReader r)
    {
        SkipIfNotExists(r);
        if (r.IsWord("AUTHORIZATION")) return; // "CREATE SCHEMA AUTHORIZATION role" — unnamed, skip
        var name = r.ParseIdentifier();
        EnsureSchema(model, name);
    }

    // ---- CREATE TABLE --------------------------------------------------------------------

    private void ParseTable(DatabaseModel model, TokenReader r)
    {
        SkipIfNotExists(r);
        var (schema, name) = ParseQualifiedName(r);
        var table = new TableDefinition { Schema = schema, Name = name };

        r.ExpectSymbol('(');
        while (!r.Eof && !r.IsSymbol(')'))
        {
            ParseTableElement(r, table);
            if (!r.MatchSymbol(',')) break;
        }
        r.ExpectSymbol(')');

        EnsureSchema(model, schema);
        model.Tables.Add(table);
    }

    private void ParseTableElement(TokenReader r, TableDefinition table)
    {
        if (r.IsWord("CONSTRAINT"))
        {
            r.Next();
            var cname = r.ParseIdentifier();
            ParseTableConstraint(r, table, cname);
            return;
        }
        if (r.IsWord("PRIMARY") || r.IsWord("UNIQUE") || r.IsWord("FOREIGN")
            || r.IsWord("CHECK") || r.IsWord("EXCLUDE") || r.IsWord("LIKE"))
        {
            ParseTableConstraint(r, table, null);
            return;
        }
        ParseColumn(r, table);
    }

    private void ParseTableConstraint(TokenReader r, TableDefinition table, string? name)
    {
        if (r.MatchWord("PRIMARY"))
        {
            r.MatchWord("KEY");
            table.PrimaryKey = new PrimaryKeyDefinition(name, ParseColumnList(r));
        }
        else if (r.MatchWord("UNIQUE"))
        {
            table.Unique.Add(new UniqueConstraintDefinition(name, ParseColumnList(r)));
        }
        else if (r.MatchWord("FOREIGN"))
        {
            r.MatchWord("KEY");
            var cols = ParseColumnList(r);
            r.MatchWord("REFERENCES");
            var (rs, rt) = ParseQualifiedName(r);
            var refCols = r.IsSymbol('(') ? ParseColumnList(r) : new List<string>();
            var (onDelete, onUpdate) = ParseReferentialActions(r);
            table.ForeignKeys.Add(new ForeignKeyDefinition(name, cols, rs, rt, refCols, onDelete, onUpdate));
        }
        else if (r.MatchWord("CHECK"))
        {
            var expr = r.IsSymbol('(') ? CaptureBalancedParens(r) : string.Empty;
            table.Checks.Add(new CheckConstraintDefinition(name, expr));
            SkipToElementEnd(r); // e.g. NO INHERIT
        }
        else if (r.MatchWord("EXCLUDE"))
        {
            var clause = CaptureToElementEnd(r);
            var prefix = string.IsNullOrEmpty(name) ? string.Empty : $"CONSTRAINT {name} ";
            table.OtherConstraints.Add($"{prefix}EXCLUDE {clause}".Trim());
        }
        else
        {
            // LIKE / anything else — skip the whole element body.
            SkipToElementEnd(r);
        }
    }

    private void ParseColumn(TokenReader r, TableDefinition table)
    {
        var colName = r.ParseIdentifier();
        var dataType = ParseDataType(r);
        var nullable = true;
        string? def = null;
        var identity = false;
        string? identityKind = null;
        string? generated = null;

        while (!r.Eof && !r.IsSymbol(',') && !r.IsSymbol(')'))
        {
            if (r.MatchWord("CONSTRAINT")) { r.ParseIdentifier(); continue; } // named inline constraint
            if (r.MatchWord("NOT")) { r.MatchWord("NULL"); nullable = false; continue; }
            if (r.MatchWord("NULL")) { nullable = true; continue; }
            if (r.MatchWord("DEFAULT")) { def = ParseDefaultExpression(r); continue; }
            if (r.MatchWord("PRIMARY")) { r.MatchWord("KEY"); table.PrimaryKey = new PrimaryKeyDefinition(null, new[] { colName }); nullable = false; continue; }
            if (r.MatchWord("UNIQUE")) { table.Unique.Add(new UniqueConstraintDefinition(null, new[] { colName })); continue; }
            if (r.MatchWord("REFERENCES"))
            {
                var (rs, rt) = ParseQualifiedName(r);
                var refCols = r.IsSymbol('(') ? ParseColumnList(r) : new List<string>();
                var (onDelete, onUpdate) = ParseReferentialActions(r);
                table.ForeignKeys.Add(new ForeignKeyDefinition(null, new[] { colName }, rs, rt, refCols, onDelete, onUpdate));
                continue;
            }
            if (r.MatchWord("GENERATED"))
            {
                // GENERATED { ALWAYS | BY DEFAULT } AS IDENTITY [ ( options ) ]
                // GENERATED ALWAYS AS ( expr ) STORED
                string? idKind = null;
                if (r.MatchWord("ALWAYS")) idKind = "ALWAYS";
                else if (r.MatchWord("BY")) { r.MatchWord("DEFAULT"); idKind = "BY DEFAULT"; }
                r.MatchWord("AS");
                if (r.MatchWord("IDENTITY")) { identity = true; identityKind = idKind ?? "BY DEFAULT"; if (r.IsSymbol('(')) SkipBalancedParens(r); }
                else if (r.IsSymbol('(')) { generated = CaptureBalancedParens(r); r.MatchWord("STORED"); }
                continue;
            }
            if (r.MatchWord("CHECK"))
            {
                var expr = r.IsSymbol('(') ? CaptureBalancedParens(r) : string.Empty;
                table.Checks.Add(new CheckConstraintDefinition(null, expr));
                continue;
            }
            if (r.MatchWord("COLLATE")) { r.ParseIdentifier(); continue; }
            // Unknown column modifier — consume one token to make progress.
            r.Next();
        }

        table.Columns.Add(new ColumnDefinition(colName, dataType, nullable, def, identity, identityKind, generated));
    }

    private static string ParseDataType(TokenReader r)
    {
        var collected = new List<Token>();
        var depth = 0;
        while (!r.Eof)
        {
            var t = r.Cur!;
            if (t.IsSymbol('(')) { collected.Add(r.Next()); depth++; continue; }
            if (t.IsSymbol(')'))
            {
                if (depth == 0) break;
                collected.Add(r.Next()); depth--; continue;
            }
            if (depth == 0)
            {
                if (t.IsSymbol(',')) break;
                if (collected.Count > 0 && t.Kind == TokenKind.Word
                    && ConstraintStartKeywords.Contains(t.Value)) break;
            }
            collected.Add(r.Next());
        }
        if (collected.Count == 0)
            throw new ParseException("Expected a column data type.");
        return TypeNormalizer.Normalize(Token.Render(collected));
    }

    private static string ParseDefaultExpression(TokenReader r)
    {
        var collected = new List<Token>();
        var depth = 0;
        var first = true;
        while (!r.Eof)
        {
            var t = r.Cur!;
            if (t.IsSymbol('(')) { collected.Add(r.Next()); depth++; first = false; continue; }
            if (t.IsSymbol(')'))
            {
                if (depth == 0) break;
                collected.Add(r.Next()); depth--; continue;
            }
            if (depth == 0 && !first)
            {
                if (t.IsSymbol(',')) break;
                if (t.Kind == TokenKind.Word && ConstraintStartKeywords.Contains(t.Value)) break;
            }
            collected.Add(r.Next());
            first = false;
        }
        return Token.Render(collected);
    }

    // ---- CREATE INDEX --------------------------------------------------------------------

    private void ParseIndex(DatabaseModel model, TokenReader r, bool unique)
    {
        r.MatchWord("CONCURRENTLY");
        SkipIfNotExists(r);

        // Name is optional in Postgres; if the next word is ON, there is no explicit name.
        string name;
        if (r.IsWord("ON"))
        {
            name = string.Empty; // auto-named by server; we still track it positionally
        }
        else
        {
            name = r.ParseIdentifier();
        }
        r.MatchWord("ON");
        r.MatchWord("ONLY");
        var (schema, table) = ParseQualifiedName(r);

        string? method = null;
        if (r.MatchWord("USING")) method = r.ParseIdentifier();

        var cols = ParseExpressionList(r);

        string? where = null;
        if (r.MatchWord("WHERE"))
        {
            var rest = new List<Token>();
            while (!r.Eof) rest.Add(r.Next());
            where = Token.Render(rest);
        }

        if (string.IsNullOrEmpty(name))
            name = $"{table}_{string.Join("_", cols)}_idx";

        model.Indexes.Add(new IndexDefinition(name, schema, table, cols, unique, method, where));
        EnsureSchema(model, schema);
    }

    // ---- CREATE VIEW ---------------------------------------------------------------------

    private void ParseView(DatabaseModel model, TokenReader r, bool materialized)
    {
        SkipIfNotExists(r);
        var (schema, name) = ParseQualifiedName(r);
        if (r.IsSymbol('(')) SkipBalancedParens(r); // optional explicit column list
        r.MatchWord("AS");
        var body = new List<Token>();
        while (!r.Eof) body.Add(r.Next());
        model.Views.Add(new ViewDefinition(schema, name, Token.Render(body), materialized));
        EnsureSchema(model, schema);
    }

    // ---- CREATE SEQUENCE -----------------------------------------------------------------

    private void ParseSequence(DatabaseModel model, TokenReader r)
    {
        SkipIfNotExists(r);
        var (schema, name) = ParseQualifiedName(r);

        string? dataType = null;
        long? increment = null, min = null, max = null, start = null, cache = null;
        var cycle = false;

        while (!r.Eof)
        {
            // Sequence AS takes a single simple integer type (smallint/integer/bigint), not the
            // full column-type grammar — read exactly one word.
            if (r.MatchWord("AS")) { dataType = TypeNormalizer.Normalize(r.ParseIdentifier()); continue; }
            if (r.MatchWord("INCREMENT")) { r.MatchWord("BY"); increment = ParseSignedLong(r); continue; }
            if (r.MatchWord("MINVALUE")) { min = ParseSignedLong(r); continue; }
            if (r.MatchWord("MAXVALUE")) { max = ParseSignedLong(r); continue; }
            if (r.MatchWord("START")) { r.MatchWord("WITH"); start = ParseSignedLong(r); continue; }
            if (r.MatchWord("CACHE")) { cache = ParseSignedLong(r); continue; }
            if (r.MatchWord("CYCLE")) { cycle = true; continue; }
            if (r.MatchWord("NO"))
            {
                if (r.MatchWord("CYCLE")) cycle = false;
                else r.Next(); // NO MINVALUE / NO MAXVALUE → leave unset
                continue;
            }
            if (r.MatchWord("OWNED")) { r.MatchWord("BY"); break; } // ownership — not modelled
            r.Next(); // unknown token; keep progressing
        }

        model.Sequences.Add(new SequenceDefinition(schema, name, dataType, increment, min, max, start, cache, cycle));
        EnsureSchema(model, schema);
    }

    private static long? ParseSignedLong(TokenReader r)
    {
        var negative = r.MatchSymbol('-');
        if (r.Cur is { Kind: TokenKind.Number } t && long.TryParse(t.Value, out var v))
        {
            r.Next();
            return negative ? -v : v;
        }
        return null;
    }

    // ---- CREATE FUNCTION / PROCEDURE -----------------------------------------------------

    private void ParseFunction(DatabaseModel model, TokenReader r, IReadOnlyList<Token> rawStatement)
    {
        var (schema, name) = ParseQualifiedName(r);
        var argList = r.IsSymbol('(') ? CaptureBalancedParens(r) : "()";
        var signature = $"{schema}.{name}{argList}";
        var body = Token.Render(rawStatement);
        model.Functions.Add(new FunctionDefinition(schema, name, signature, body));
        EnsureSchema(model, schema);
    }

    // ---- generic raw objects (extension/type/domain/trigger/policy/…) --------------------

    /// <summary>Reads the (possibly multi-word) object-kind phrase, leaving the reader after it.</summary>
    private static ObjectKind? DetectRawKind(TokenReader r)
    {
        if (r.Eof) return null;
        var first = r.Next().Value.ToUpperInvariant();
        switch (first)
        {
            case "EXTENSION": return ObjectKind.Extension;
            case "LANGUAGE": return ObjectKind.Language;
            case "TRUSTED": r.MatchWord("PROCEDURAL"); r.MatchWord("LANGUAGE"); return ObjectKind.Language;
            case "PROCEDURAL": r.MatchWord("LANGUAGE"); return ObjectKind.Language;
            case "TYPE": return ObjectKind.Type;
            case "DOMAIN": return ObjectKind.Domain;
            case "COLLATION": return ObjectKind.Collation;
            case "CONVERSION": return ObjectKind.Conversion;
            case "CAST": return ObjectKind.Cast;
            case "AGGREGATE": return ObjectKind.Aggregate;
            case "TRIGGER": return ObjectKind.Trigger;
            case "RULE": return ObjectKind.Rule;
            case "POLICY": return ObjectKind.Policy;
            case "STATISTICS": return ObjectKind.Statistics;
            case "SERVER": return ObjectKind.Server;
            case "TRANSFORM": return ObjectKind.Transform;
            case "CONSTRAINT": return r.MatchWord("TRIGGER") ? ObjectKind.Trigger : null;
            case "OPERATOR":
                if (r.MatchWord("CLASS")) return ObjectKind.OperatorClass;
                if (r.MatchWord("FAMILY")) return ObjectKind.OperatorFamily;
                return ObjectKind.Operator;
            case "EVENT":
                return r.MatchWord("TRIGGER") ? ObjectKind.EventTrigger : null;
            case "FOREIGN":
                if (r.MatchWord("TABLE")) return ObjectKind.ForeignTable;
                if (r.MatchWord("DATA")) { r.MatchWord("WRAPPER"); return ObjectKind.ForeignDataWrapper; }
                return null;
            case "USER":
                return r.MatchWord("MAPPING") ? ObjectKind.UserMapping : null;
            case "TEXT":
                if (!r.MatchWord("SEARCH")) return null;
                if (r.MatchWord("CONFIGURATION")) return ObjectKind.TextSearchConfiguration;
                if (r.MatchWord("DICTIONARY")) return ObjectKind.TextSearchDictionary;
                if (r.MatchWord("PARSER")) return ObjectKind.TextSearchParser;
                if (r.MatchWord("TEMPLATE")) return ObjectKind.TextSearchTemplate;
                return null;
            default: return null;
        }
    }

    private void ParseRawObject(DatabaseModel model, ObjectKind kind, IReadOnlyList<Token> rawStatement, TokenReader r)
    {
        var body = Token.Render(rawStatement);
        string schema = string.Empty, name = string.Empty, onObject = string.Empty;

        try
        {
            switch (kind)
            {
                // schema-qualified, name only
                case ObjectKind.Type or ObjectKind.Domain or ObjectKind.Collation or ObjectKind.Conversion
                    or ObjectKind.Statistics or ObjectKind.ForeignTable or ObjectKind.TextSearchConfiguration
                    or ObjectKind.TextSearchDictionary or ObjectKind.TextSearchParser or ObjectKind.TextSearchTemplate:
                    SkipIfNotExists(r);
                    (schema, name) = ParseQualifiedName(r);
                    break;

                // global name
                case ObjectKind.Extension or ObjectKind.Language or ObjectKind.Server
                    or ObjectKind.ForeignDataWrapper or ObjectKind.EventTrigger:
                    SkipIfNotExists(r);
                    name = r.ParseIdentifier();
                    break;

                // table-scoped: name ON table
                case ObjectKind.Trigger or ObjectKind.Policy:
                    name = r.ParseIdentifier();
                    onObject = ScanForKeywordThenQualified(r, "ON");
                    schema = SchemaOf(onObject);
                    break;

                case ObjectKind.Rule:
                    name = r.ParseIdentifier();
                    onObject = ScanForKeywordThenQualified(r, "TO");
                    schema = SchemaOf(onObject);
                    break;

                // signature-style
                case ObjectKind.Aggregate:
                    (schema, var an) = ParseQualifiedName(r);
                    name = $"{schema}.{an}" + (r.IsSymbol('(') ? CaptureBalancedParens(r) : "");
                    break;

                case ObjectKind.Operator:
                    name = CaptureUntilSymbol(r, '(') + (r.IsSymbol('(') ? CaptureBalancedParens(r) : "");
                    break;

                case ObjectKind.OperatorClass or ObjectKind.OperatorFamily:
                    (schema, var ocn) = ParseQualifiedName(r);
                    var method = ScanForKeywordThenIdentifier(r, "USING");
                    name = $"{schema}.{ocn}" + (method.Length > 0 ? $" USING {method}" : "");
                    break;

                case ObjectKind.Cast:
                    name = r.IsSymbol('(') ? CaptureBalancedParens(r) : "";
                    break;

                case ObjectKind.Transform:
                    r.MatchWord("FOR");
                    var type = CaptureUntilWord(r, "LANGUAGE");
                    var lang = r.MatchWord("LANGUAGE") ? r.ParseIdentifier() : "";
                    name = $"FOR {type} LANGUAGE {lang}";
                    break;

                case ObjectKind.UserMapping:
                    SkipIfNotExists(r);
                    r.MatchWord("FOR");
                    var usr = r.ParseIdentifier();
                    var srv = ScanForKeywordThenIdentifier(r, "SERVER");
                    name = $"FOR {usr} SERVER {srv}";
                    break;
            }
        }
        catch (ParseException)
        {
            // Fall back below to a body-based identity so the object is still captured.
        }

        var identity = !string.IsNullOrEmpty(name)
            ? BuildIdentity(kind, schema, name, onObject)
            : $"{kind}:{NormalizeForIdentity(body)}";

        model.Objects.Add(new RawObjectDefinition(kind, schema, name, identity, body,
            string.IsNullOrEmpty(onObject) ? null : onObject));
        if (!string.IsNullOrEmpty(schema)) EnsureSchema(model, schema);
    }

    private void ParseComment(DatabaseModel model, IReadOnlyList<Token> rawStatement)
    {
        var r = new TokenReader(rawStatement);
        r.MatchWord("COMMENT");
        r.MatchWord("ON");
        var target = new List<Token>();
        while (!r.Eof && !r.IsWord("IS")) target.Add(r.Next());
        var identity = $"comment:{NormalizeForIdentity(Token.Render(target))}";
        model.Objects.Add(new RawObjectDefinition(ObjectKind.Comment, string.Empty, string.Empty,
            identity, Token.Render(rawStatement)));
    }

    private static string BuildIdentity(ObjectKind kind, string schema, string name, string onObject)
    {
        var tag = kind.ToString().ToLowerInvariant();
        if (!string.IsNullOrEmpty(onObject)) return $"{tag}:{name} on {onObject}".ToLowerInvariant();
        var qualified = string.IsNullOrEmpty(schema) ? name : $"{schema}.{name}";
        return $"{tag}:{qualified}".ToLowerInvariant();
    }

    private static string SchemaOf(string qualified)
    {
        var dot = qualified.IndexOf('.');
        return dot > 0 ? qualified[..dot] : string.Empty;
    }

    private string ScanForKeywordThenQualified(TokenReader r, string keyword)
    {
        while (!r.Eof && !r.IsWord(keyword)) r.Next();
        if (!r.MatchWord(keyword)) return string.Empty;
        var (s, n) = ParseQualifiedName(r);
        return $"{s}.{n}";
    }

    private static string ScanForKeywordThenIdentifier(TokenReader r, string keyword)
    {
        while (!r.Eof && !r.IsWord(keyword)) r.Next();
        return r.MatchWord(keyword) ? r.ParseIdentifier() : string.Empty;
    }

    private static string CaptureUntilSymbol(TokenReader r, char stop)
    {
        var toks = new List<Token>();
        while (!r.Eof && !r.IsSymbol(stop)) toks.Add(r.Next());
        return Token.Render(toks);
    }

    private static string CaptureUntilWord(TokenReader r, string stopWord)
    {
        var toks = new List<Token>();
        while (!r.Eof && !r.IsWord(stopWord)) toks.Add(r.Next());
        return Token.Render(toks);
    }

    private static string NormalizeForIdentity(string s) =>
        System.Text.RegularExpressions.Regex.Replace(s.Trim(), @"\s+", " ").ToLowerInvariant();

    // ---- shared helpers ------------------------------------------------------------------

    private (string Schema, string Name) ParseQualifiedName(TokenReader r)
    {
        var first = r.ParseIdentifier();
        if (r.MatchSymbol('.'))
        {
            var second = r.ParseIdentifier();
            return (first, second);
        }
        return (_defaultSchema, first);
    }

    private static void SkipIfNotExists(TokenReader r)
    {
        if (r.MatchWord("IF")) { r.MatchWord("NOT"); r.MatchWord("EXISTS"); }
    }

    private static List<string> ParseColumnList(TokenReader r)
    {
        var cols = new List<string>();
        r.ExpectSymbol('(');
        while (!r.Eof && !r.IsSymbol(')'))
        {
            cols.Add(r.ParseIdentifier());
            while (!r.Eof && !r.IsSymbol(',') && !r.IsSymbol(')')) r.Next(); // skip ASC/opclass/etc.
            if (!r.MatchSymbol(',')) break;
        }
        r.ExpectSymbol(')');
        return cols;
    }

    private static List<string> ParseExpressionList(TokenReader r)
    {
        var items = new List<string>();
        r.ExpectSymbol('(');
        while (!r.Eof && !r.IsSymbol(')'))
        {
            var toks = new List<Token>();
            var depth = 0;
            while (!r.Eof)
            {
                var t = r.Cur!;
                if (t.IsSymbol('(')) { depth++; toks.Add(r.Next()); continue; }
                if (t.IsSymbol(')')) { if (depth == 0) break; depth--; toks.Add(r.Next()); continue; }
                if (depth == 0 && t.IsSymbol(',')) break;
                toks.Add(r.Next());
            }
            items.Add(Token.Render(toks).Trim());
            if (!r.MatchSymbol(',')) break;
        }
        r.ExpectSymbol(')');
        return items;
    }

    private static (string? OnDelete, string? OnUpdate) ParseReferentialActions(TokenReader r)
    {
        string? onDelete = null, onUpdate = null;
        while (r.IsWord("ON") || r.IsWord("MATCH") || r.IsWord("DEFERRABLE") || r.IsWord("NOT") || r.IsWord("INITIALLY"))
        {
            if (r.MatchWord("ON"))
            {
                var which = r.ParseIdentifier(); // DELETE or UPDATE (folded to lower)
                var action = ParseAction(r);
                if (which == "delete") onDelete = action; else if (which == "update") onUpdate = action;
            }
            else if (r.MatchWord("MATCH")) { r.Next(); }
            else { r.Next(); } // DEFERRABLE / INITIALLY ... — skip a token to progress
        }
        return (onDelete, onUpdate);
    }

    private static string ParseAction(TokenReader r)
    {
        if (r.MatchWord("CASCADE")) return "CASCADE";
        if (r.MatchWord("RESTRICT")) return "RESTRICT";
        if (r.MatchWord("NO")) { r.MatchWord("ACTION"); return "NO ACTION"; }
        if (r.MatchWord("SET"))
        {
            if (r.MatchWord("NULL")) return "SET NULL";
            if (r.MatchWord("DEFAULT")) return "SET DEFAULT";
        }
        return "NO ACTION";
    }

    private static void SkipBalancedParens(TokenReader r)
    {
        r.ExpectSymbol('(');
        var depth = 1;
        while (!r.Eof && depth > 0)
        {
            var t = r.Next();
            if (t.IsSymbol('(')) depth++;
            else if (t.IsSymbol(')')) depth--;
        }
    }

    private static string CaptureBalancedParens(TokenReader r)
    {
        var toks = new List<Token> { r.Cur! };
        r.ExpectSymbol('(');
        var depth = 1;
        while (!r.Eof && depth > 0)
        {
            var t = r.Next();
            toks.Add(t);
            if (t.IsSymbol('(')) depth++;
            else if (t.IsSymbol(')')) depth--;
        }
        return Token.Render(toks);
    }

    private static void SkipToElementEnd(TokenReader r)
    {
        var depth = 0;
        while (!r.Eof)
        {
            var t = r.Cur!;
            if (t.IsSymbol('(')) { depth++; r.Next(); continue; }
            if (t.IsSymbol(')')) { if (depth == 0) break; depth--; r.Next(); continue; }
            if (depth == 0 && t.IsSymbol(',')) break;
            r.Next();
        }
    }

    private static string CaptureToElementEnd(TokenReader r)
    {
        var toks = new List<Token>();
        var depth = 0;
        while (!r.Eof)
        {
            var t = r.Cur!;
            if (t.IsSymbol('(')) { depth++; toks.Add(r.Next()); continue; }
            if (t.IsSymbol(')')) { if (depth == 0) break; depth--; toks.Add(r.Next()); continue; }
            if (depth == 0 && t.IsSymbol(',')) break;
            toks.Add(r.Next());
        }
        return Token.Render(toks);
    }

    private static void EnsureSchema(DatabaseModel model, string schema)
    {
        if (!model.HasSchema(schema))
            model.Schemas.Add(new SchemaDefinition(schema));
    }
}

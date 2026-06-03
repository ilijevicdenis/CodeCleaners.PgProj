using System;
using System.Collections.Generic;
using System.Linq;
using PgProj.Core.Ast;
using PgProj.Core.Model;

namespace PgProj.Core.Parsing;

/// <summary>
/// Parses PostgreSQL DDL into the PgProj <see cref="SqlScript"/> AST (our own lexer + recursive
/// descent — no external dependency). Functions (header + classified body) and tables (columns +
/// CHECK/DEFAULT expressions) are fully structured for static analysis; the long tail is captured
/// as <see cref="RawStatement"/>. This is the tree the analyzer and tree-walker run on.
/// </summary>
public sealed class AstParser
{
    private static readonly HashSet<string> ConstraintKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "NOT", "NULL", "DEFAULT", "PRIMARY", "UNIQUE", "REFERENCES",
        "CHECK", "CONSTRAINT", "GENERATED", "COLLATE",
    };

    private static readonly HashSet<string> FunctionClauseKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "RETURNS", "LANGUAGE", "TRANSFORM", "WINDOW", "IMMUTABLE", "STABLE", "VOLATILE",
        "LEAKPROOF", "CALLED", "STRICT", "SECURITY", "PARALLEL", "COST", "ROWS", "SUPPORT",
        "SET", "AS", "BEGIN",
    };

    private readonly string _defaultSchema;
    public List<string> Diagnostics { get; } = new();

    public AstParser(string defaultSchema = "public") => _defaultSchema = defaultSchema;

    public SqlScript Parse(string sql)
    {
        var statements = new List<SqlStatement>();
        foreach (var stmt in SplitStatements(Tokenizer.Tokenize(sql)))
        {
            try
            {
                var node = ParseStatement(stmt);
                if (node is not null) statements.Add(node);
            }
            catch (ParseException ex)
            {
                Diagnostics.Add($"{ex.Message}  (near: {Token.Render(stmt.Take(8).ToList())}…)");
            }
        }
        return new SqlScript { Statements = statements };
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

    private SqlStatement? ParseStatement(List<Token> tokens)
    {
        var r = new TokenReader(tokens);
        if (r.IsWord("COMMENT")) return ParseComment(tokens);
        if (!r.MatchWord("CREATE")) return null;

        var materialized = false;
        var unique = false;
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

        if (r.Cur is not { Kind: TokenKind.Word } kindTok) return null;
        switch (kindTok.Value.ToUpperInvariant())
        {
            case "SCHEMA": r.Next(); return ParseSchema(r);
            case "TABLE": r.Next(); return ParseTable(r, tokens);
            case "VIEW": r.Next(); return ParseView(r, materialized, tokens);
            case "FUNCTION":
            case "PROCEDURE": r.Next(); return ParseFunction(r, tokens, kindTok.Value.ToUpperInvariant() == "PROCEDURE");
            // INDEX/SEQUENCE and the long tail are captured raw for the analysis tree.
            default: return ParseRaw(tokens, r);
        }
    }

    // ---- schema -------------------------------------------------------------------------

    private SqlStatement ParseSchema(TokenReader r)
    {
        var ifNotExists = SkipIfNotExists(r);
        var name = r.ParseIdentifier();
        return new CreateSchemaStatement { Name = name, IfNotExists = ifNotExists };
    }

    // ---- table --------------------------------------------------------------------------

    private SqlStatement ParseTable(TokenReader r, List<Token> raw)
    {
        SkipIfNotExists(r);
        var (schema, name) = ParseQualifiedName(r);
        if (!r.IsSymbol('('))
            return new RawStatement { Kind = ObjectKind.Table, Schema = schema, Name = name, Identity = $"table:{schema}.{name}", BodyText = Token.Render(raw) };

        var columns = new List<ColumnNode>();
        var constraints = new List<TableConstraintNode>();
        r.ExpectSymbol('(');
        while (!r.Eof && !r.IsSymbol(')'))
        {
            ParseTableElement(r, columns, constraints);
            if (!r.MatchSymbol(',')) break;
        }
        r.ExpectSymbol(')');

        var trailing = new List<Token>();
        while (!r.Eof) trailing.Add(r.Next());

        return new CreateTableStatement
        {
            Schema = schema, Name = name,
            Columns = columns, Constraints = constraints,
            TrailingOptions = trailing.Count > 0 ? Token.Render(trailing) : null,
        };
    }

    private void ParseTableElement(TokenReader r, List<ColumnNode> columns, List<TableConstraintNode> constraints)
    {
        string? cname = null;
        if (r.IsWord("CONSTRAINT")) { r.Next(); cname = r.ParseIdentifier(); }

        if (r.IsWord("PRIMARY") || r.IsWord("UNIQUE") || r.IsWord("FOREIGN") || r.IsWord("CHECK") || r.IsWord("EXCLUDE"))
        {
            constraints.Add(ParseTableConstraint(r, cname));
            return;
        }
        columns.Add(ParseColumn(r));
    }

    private TableConstraintNode ParseTableConstraint(TokenReader r, string? name)
    {
        if (r.MatchWord("PRIMARY")) { r.MatchWord("KEY"); return new PrimaryKeyConstraintNode { Name = name, Columns = ParseColumnList(r) }; }
        if (r.MatchWord("UNIQUE")) return new UniqueConstraintNode { Name = name, Columns = ParseColumnList(r) };
        if (r.MatchWord("FOREIGN"))
        {
            r.MatchWord("KEY");
            var cols = ParseColumnList(r);
            r.MatchWord("REFERENCES");
            var (rs, rt) = ParseQualifiedName(r);
            var refCols = r.IsSymbol('(') ? ParseColumnList(r) : new List<string>();
            var (od, ou) = ParseReferentialActions(r);
            return new ForeignKeyConstraintNode { Name = name, Columns = cols, RefSchema = rs, RefTable = rt, RefColumns = refCols, OnDelete = od, OnUpdate = ou };
        }
        if (r.MatchWord("CHECK"))
        {
            var inner = CaptureParenTokens(r);
            return new CheckConstraintNode { Name = name, Expression = ExpressionParser.Parse(inner) };
        }
        // EXCLUDE etc.
        var rest = CaptureToElementEnd(r);
        return new RawConstraintNode { Name = name, Text = (name is null ? "" : $"CONSTRAINT {name} ") + "EXCLUDE " + rest };
    }

    private ColumnNode ParseColumn(TokenReader r)
    {
        var colName = r.ParseIdentifier();
        var rawType = ParseRawType(r);
        var lower = rawType.Trim().ToLowerInvariant();
        var isSerial = lower is "serial" or "serial4" or "bigserial" or "serial8" or "smallserial" or "serial2";
        var type = new TypeName { Raw = rawType, Normalized = TypeNormalizer.Normalize(rawType), IsSerial = isSerial };
        var cons = new List<ColumnConstraintNode>();

        while (!r.Eof && !r.IsSymbol(',') && !r.IsSymbol(')'))
        {
            if (r.MatchWord("CONSTRAINT")) { r.ParseIdentifier(); continue; }
            if (r.MatchWord("NOT")) { r.MatchWord("NULL"); cons.Add(new NotNullConstraintNode()); continue; }
            if (r.MatchWord("NULL")) { cons.Add(new NullConstraintNode()); continue; }
            if (r.MatchWord("DEFAULT")) { var toks = CaptureDefaultTokens(r); cons.Add(new DefaultConstraintNode { Expression = ExpressionParser.Parse(toks), RawText = Token.Render(toks) }); continue; }
            if (r.MatchWord("PRIMARY")) { r.MatchWord("KEY"); cons.Add(new InlinePrimaryKeyNode()); continue; }
            if (r.MatchWord("UNIQUE")) { cons.Add(new InlineUniqueNode()); continue; }
            if (r.MatchWord("REFERENCES"))
            {
                var (rs, rt) = ParseQualifiedName(r);
                var refCols = r.IsSymbol('(') ? ParseColumnList(r) : new List<string>();
                var (od, ou) = ParseReferentialActions(r);
                cons.Add(new InlineReferencesNode { RefSchema = rs, RefTable = rt, RefColumns = refCols, OnDelete = od, OnUpdate = ou });
                continue;
            }
            if (r.MatchWord("GENERATED"))
            {
                string kind = "BY DEFAULT";
                if (r.MatchWord("ALWAYS")) kind = "ALWAYS";
                else if (r.MatchWord("BY")) { r.MatchWord("DEFAULT"); kind = "BY DEFAULT"; }
                r.MatchWord("AS");
                if (r.MatchWord("IDENTITY")) { if (r.IsSymbol('(')) SkipBalancedParens(r); cons.Add(new IdentityConstraintNode { Kind = kind }); }
                else if (r.IsSymbol('(')) { var toks = CaptureParenTokensWithParens(r); r.MatchWord("STORED"); cons.Add(new GeneratedConstraintNode { Expression = ExpressionParser.Parse(Inner(toks)) }); }
                continue;
            }
            if (r.MatchWord("CHECK")) { var inner = CaptureParenTokens(r); cons.Add(new CheckColumnConstraintNode { Expression = ExpressionParser.Parse(inner) }); continue; }
            if (r.MatchWord("COLLATE")) { cons.Add(new CollateConstraintNode { Collation = r.ParseIdentifier() }); continue; }
            r.Next();
        }
        return new ColumnNode { Name = colName, Type = type, Constraints = cons };
    }

    // ---- view ---------------------------------------------------------------------------

    private SqlStatement ParseView(TokenReader r, bool materialized, List<Token> raw)
    {
        SkipIfNotExists(r);
        var (schema, name) = ParseQualifiedName(r);
        if (r.IsSymbol('(')) SkipBalancedParens(r);
        r.MatchWord("AS");
        var body = new List<Token>();
        while (!r.Eof) body.Add(r.Next());
        return new CreateViewStatement { Schema = schema, Name = name, Materialized = materialized, BodyText = Token.Render(body) };
    }

    // ---- function -----------------------------------------------------------------------

    private SqlStatement ParseFunction(TokenReader r, List<Token> raw, bool isProcedure)
    {
        var (schema, name) = ParseQualifiedName(r);
        var parameters = r.IsSymbol('(') ? ParseParameters(r) : new List<FunctionParameter>();

        string? returns = null, volatility = null, language = "sql", bodyText = "";
        var security = "INVOKER";
        var strict = false;
        var setClauses = new List<string>();

        while (!r.Eof)
        {
            if (r.MatchWord("RETURNS"))
            {
                if (r.IsWord("NULL")) { r.Next(); r.MatchWord("ON"); r.MatchWord("NULL"); r.MatchWord("INPUT"); strict = true; }
                else returns = CaptureUntilClause(r);
                continue;
            }
            if (r.MatchWord("LANGUAGE")) { language = r.ParseIdentifier(); continue; }
            if (r.MatchWord("SECURITY")) { security = r.MatchWord("DEFINER") ? "DEFINER" : (r.MatchWord("INVOKER") ? "INVOKER" : security); continue; }
            if (r.IsWord("IMMUTABLE") || r.IsWord("STABLE") || r.IsWord("VOLATILE")) { volatility = r.Next().Value.ToUpperInvariant(); continue; }
            if (r.MatchWord("STRICT")) { strict = true; continue; }
            if (r.MatchWord("CALLED")) { r.MatchWord("ON"); r.MatchWord("NULL"); r.MatchWord("INPUT"); continue; }
            if (r.MatchWord("SET")) { setClauses.Add(CaptureUntilClause(r)); continue; }
            if (r.MatchWord("AS")) { bodyText = ExtractBody(r); continue; }
            if (r.IsWord("LEAKPROOF") || r.IsWord("WINDOW")) { r.Next(); continue; }
            if (r.MatchWord("NOT")) { r.MatchWord("LEAKPROOF"); continue; }
            if (r.MatchWord("PARALLEL")) { r.ParseIdentifier(); continue; }
            if (r.MatchWord("COST") || r.MatchWord("ROWS")) { if (!r.Eof) r.Next(); continue; }
            r.Next();
        }

        var header = new FunctionHeader
        {
            Schema = schema, Name = name, Parameters = parameters,
            Returns = returns, Language = language, Volatility = volatility,
            Security = security, Strict = strict, SetClauses = setClauses, IsProcedure = isProcedure,
            ArgTypes = string.Join(", ", parameters.Select(p => p.Type.Normalized)),
        };
        var body = new FunctionBody { Language = language, RawText = bodyText, Statements = FunctionBodyClassifier.Classify(bodyText) };
        return new CreateFunctionStatement { Header = header, Body = body };
    }

    private List<FunctionParameter> ParseParameters(TokenReader r)
    {
        var tokens = Inner(CaptureParenTokensWithParens(r));
        var list = new List<FunctionParameter>();
        foreach (var argTokens in SplitTopLevelCommas(tokens))
        {
            if (argTokens.Count == 0) continue;
            var rr = new TokenReader(argTokens);
            string? mode = null, pname = null;
            if (rr.Cur is { } m && m.Kind == TokenKind.Word && m.Value.ToUpperInvariant() is "IN" or "OUT" or "INOUT" or "VARIADIC")
                mode = rr.Next().Value.ToUpperInvariant();
            // optional name: a word followed by something that starts a type
            if (rr.Cur is { Kind: TokenKind.Word } && rr.Peek() is { } nxt && nxt.IsIdentifierLike)
                pname = rr.Next().Value;
            else if (rr.Cur is { Kind: TokenKind.QuotedIdent })
                pname = rr.Next().Value;

            var typeToks = new List<Token>();
            Expression? def = null;
            while (!rr.Eof)
            {
                if (rr.IsWord("DEFAULT")) { rr.Next(); def = ExpressionParser.Parse(Rest(rr)); break; }
                if (rr.IsSymbol('=')) { rr.Next(); def = ExpressionParser.Parse(Rest(rr)); break; }
                typeToks.Add(rr.Next());
            }
            var raw = Token.Render(typeToks);
            list.Add(new FunctionParameter { Mode = mode, Name = pname, Type = new TypeName { Raw = raw, Normalized = TypeNormalizer.Normalize(raw), IsSerial = false }, Default = def });
        }
        return list;
    }

    private static string ExtractBody(TokenReader r)
    {
        if (r.Cur is { Kind: TokenKind.DollarString } d) { r.Next(); return StripDollar(d.Value); }
        if (r.Cur is { Kind: TokenKind.String } s) { r.Next(); return s.Value; }
        return "";
    }

    private static string StripDollar(string raw)
    {
        var firstClose = raw.IndexOf('$', 1);
        if (firstClose < 0) return raw;
        var tag = raw.Substring(0, firstClose + 1);
        if (raw.Length >= 2 * tag.Length && raw.EndsWith(tag, StringComparison.Ordinal))
            return raw.Substring(tag.Length, raw.Length - 2 * tag.Length);
        return raw;
    }

    // ---- raw / comment ------------------------------------------------------------------

    private SqlStatement ParseComment(List<Token> tokens)
    {
        var r = new TokenReader(tokens);
        r.MatchWord("COMMENT"); r.MatchWord("ON");
        var target = new List<Token>();
        while (!r.Eof && !r.IsWord("IS")) target.Add(r.Next());
        var identity = $"comment:{Normalize(Token.Render(target))}";
        return new RawStatement { Kind = ObjectKind.Comment, Identity = identity, BodyText = Token.Render(tokens) };
    }

    private SqlStatement ParseRaw(List<Token> tokens, TokenReader r)
    {
        var kindWord = r.Cur?.Value.ToUpperInvariant() ?? "";
        var kind = kindWord switch
        {
            "INDEX" => ObjectKind.Statistics, // placeholder; index handled by model parser, raw here
            "TYPE" => ObjectKind.Type,
            "DOMAIN" => ObjectKind.Domain,
            "TRIGGER" => ObjectKind.Trigger,
            "POLICY" => ObjectKind.Policy,
            "EXTENSION" => ObjectKind.Extension,
            "SEQUENCE" => ObjectKind.Statistics,
            _ => ObjectKind.Statistics,
        };
        return new RawStatement { Kind = kind, Identity = Normalize(Token.Render(tokens.Take(8).ToList())), BodyText = Token.Render(tokens) };
    }

    // ---- shared helpers -----------------------------------------------------------------

    private (string, string) ParseQualifiedName(TokenReader r)
    {
        var first = r.ParseIdentifier();
        if (r.MatchSymbol('.')) return (first, r.ParseIdentifier());
        return (_defaultSchema, first);
    }

    private static bool SkipIfNotExists(TokenReader r)
    {
        if (r.MatchWord("IF")) { r.MatchWord("NOT"); r.MatchWord("EXISTS"); return true; }
        return false;
    }

    private static string ParseRawType(TokenReader r)
    {
        var collected = new List<Token>();
        var depth = 0;
        while (!r.Eof)
        {
            var t = r.Cur!;
            if (t.IsSymbol('(')) { collected.Add(r.Next()); depth++; continue; }
            if (t.IsSymbol(')')) { if (depth == 0) break; collected.Add(r.Next()); depth--; continue; }
            if (depth == 0)
            {
                if (t.IsSymbol(',')) break;
                if (collected.Count > 0 && t.Kind == TokenKind.Word && ConstraintKeywords.Contains(t.Value)) break;
            }
            collected.Add(r.Next());
        }
        if (collected.Count == 0) throw new ParseException("Expected a column data type.");
        return Token.Render(collected);
    }

    private static List<Token> CaptureDefaultTokens(TokenReader r)
    {
        var collected = new List<Token>();
        var depth = 0; var first = true;
        while (!r.Eof)
        {
            var t = r.Cur!;
            if (t.IsSymbol('(')) { collected.Add(r.Next()); depth++; first = false; continue; }
            if (t.IsSymbol(')')) { if (depth == 0) break; collected.Add(r.Next()); depth--; continue; }
            if (depth == 0 && !first)
            {
                if (t.IsSymbol(',')) break;
                if (t.Kind == TokenKind.Word && ConstraintKeywords.Contains(t.Value)) break;
            }
            collected.Add(r.Next()); first = false;
        }
        return collected;
    }

    private static List<string> ParseColumnList(TokenReader r)
    {
        var cols = new List<string>();
        r.ExpectSymbol('(');
        while (!r.Eof && !r.IsSymbol(')'))
        {
            cols.Add(r.ParseIdentifier());
            while (!r.Eof && !r.IsSymbol(',') && !r.IsSymbol(')')) r.Next();
            if (!r.MatchSymbol(',')) break;
        }
        r.ExpectSymbol(')');
        return cols;
    }

    private static (string?, string?) ParseReferentialActions(TokenReader r)
    {
        string? od = null, ou = null;
        while (r.IsWord("ON") || r.IsWord("MATCH") || r.IsWord("DEFERRABLE") || r.IsWord("NOT") || r.IsWord("INITIALLY"))
        {
            if (r.MatchWord("ON")) { var which = r.ParseIdentifier(); var a = ParseAction(r); if (which == "delete") od = a; else if (which == "update") ou = a; }
            else if (r.MatchWord("MATCH")) r.Next();
            else r.Next();
        }
        return (od, ou);
    }

    private static string ParseAction(TokenReader r)
    {
        if (r.MatchWord("CASCADE")) return "CASCADE";
        if (r.MatchWord("RESTRICT")) return "RESTRICT";
        if (r.MatchWord("NO")) { r.MatchWord("ACTION"); return "NO ACTION"; }
        if (r.MatchWord("SET")) { if (r.MatchWord("NULL")) return "SET NULL"; if (r.MatchWord("DEFAULT")) return "SET DEFAULT"; }
        return "NO ACTION";
    }

    private static void SkipBalancedParens(TokenReader r)
    {
        r.ExpectSymbol('('); var depth = 1;
        while (!r.Eof && depth > 0) { var t = r.Next(); if (t.IsSymbol('(')) depth++; else if (t.IsSymbol(')')) depth--; }
    }

    private static List<Token> CaptureParenTokensWithParens(TokenReader r)
    {
        var toks = new List<Token>();
        if (!r.IsSymbol('(')) return toks;
        toks.Add(r.Next()); var depth = 1;
        while (!r.Eof && depth > 0) { var t = r.Next(); toks.Add(t); if (t.IsSymbol('(')) depth++; else if (t.IsSymbol(')')) depth--; }
        return toks;
    }

    private static List<Token> CaptureParenTokens(TokenReader r) => Inner(CaptureParenTokensWithParens(r));

    private static List<Token> Inner(List<Token> withParens)
    {
        if (withParens.Count >= 2 && withParens[0].IsSymbol('(') && withParens[^1].IsSymbol(')'))
            return withParens.GetRange(1, withParens.Count - 2);
        return withParens;
    }

    private static string CaptureToElementEnd(TokenReader r)
    {
        var toks = new List<Token>(); var depth = 0;
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

    private static string CaptureUntilClause(TokenReader r)
    {
        var toks = new List<Token>(); var depth = 0;
        while (!r.Eof)
        {
            var t = r.Cur!;
            if (t.IsSymbol('(')) depth++;
            else if (t.IsSymbol(')')) depth = Math.Max(0, depth - 1);
            if (depth == 0 && t.Kind == TokenKind.Word && FunctionClauseKeywords.Contains(t.Value)) break;
            toks.Add(r.Next());
        }
        return Token.Render(toks);
    }

    private static IEnumerable<List<Token>> SplitTopLevelCommas(List<Token> tokens)
    {
        var cur = new List<Token>(); var depth = 0;
        foreach (var t in tokens)
        {
            if (t.IsSymbol('(') || t.IsSymbol('[')) depth++;
            else if (t.IsSymbol(')') || t.IsSymbol(']')) depth--;
            if (depth == 0 && t.IsSymbol(',')) { yield return cur; cur = new List<Token>(); continue; }
            cur.Add(t);
        }
        if (cur.Count > 0) yield return cur;
    }

    private static List<Token> Rest(TokenReader r)
    {
        var toks = new List<Token>();
        while (!r.Eof) toks.Add(r.Next());
        return toks;
    }

    private static string Normalize(string s) =>
        System.Text.RegularExpressions.Regex.Replace(s.Trim(), @"\s+", " ").ToLowerInvariant();
}

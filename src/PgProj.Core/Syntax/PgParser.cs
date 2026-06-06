using System;
using System.Collections.Generic;
using System.Linq;
using PgProj.Core.Parsing;

namespace PgProj.Core.Syntax;

/// <summary>
/// Hand-written recursive-descent parser for PostgreSQL. Every grammar production is its own
/// clearly-named method, so any rule is a breakpoint away; errors carry a real line:column.
///
/// Coverage is grown incrementally and corpus-driven. PgParser OWNS the statement kinds it has
/// implemented (currently CREATE TABLE / CREATE TABLE AS / CREATE SCHEMA); for everything else it
/// reports <see cref="ParseResult.FullyRecognized"/> = false so the caller can defer to the legacy
/// parser during migration. As kinds are added here, they stop falling back.
/// </summary>
public sealed partial class PgParser
{
    private static readonly string[] Persistence = { "GLOBAL", "LOCAL", "TEMP", "TEMPORARY", "UNLOGGED" };

    // Words that terminate a column's data-type and begin a column constraint.
    private static readonly HashSet<string> ColumnConstraintStart = new(StringComparer.OrdinalIgnoreCase)
    {
        "CONSTRAINT", "NOT", "NULL", "DEFAULT", "PRIMARY", "UNIQUE",
        "REFERENCES", "CHECK", "GENERATED", "COLLATE", "DEFERRABLE", "INITIALLY",
        "STORAGE", "COMPRESSION",
    };
    private static readonly HashSet<string> StorageModes = new(StringComparer.OrdinalIgnoreCase) { "PLAIN", "EXTERNAL", "EXTENDED", "MAIN", "DEFAULT" };
    private static readonly HashSet<string> MatchTypes = new(StringComparer.OrdinalIgnoreCase) { "FULL", "SIMPLE" };
    private static readonly HashSet<string> LikeOptions = new(StringComparer.OrdinalIgnoreCase)
    { "ALL", "COMMENTS", "COMPRESSION", "CONSTRAINTS", "DEFAULTS", "GENERATED", "IDENTITY", "INDEXES", "STATISTICS", "STORAGE" };

    public ParseResult Parse(string sql)
    {
        var result = new ParseResult();
        List<Token> tokens;
        try { tokens = OperatorLexer.Merge(Tokenizer.Tokenize(sql)); }
        catch (Exception ex) { result.Diagnostics.Add(new ParseDiagnostic("tokenize failed: " + ex.Message, 1, 1, 0)); return result; }

        foreach (var segment in SplitStatements(tokens))
        {
            if (segment.Count == 0) continue;
            var c = new TokenCursor(segment);
            var lead = ClassifyLeading(c);
            if (lead is null)
            {
                result.FullyRecognized = false;
                var u = new UnsupportedStatement { LeadingKeyword = c.Current?.Value ?? "" };
                u.SetSourceSegment(segment);   // render lazily — DeriveRaw reads it, so it materialises once there
                result.Statements.Add(u);
                continue;
            }
            try
            {
                var stmt = ParseStatement(c);
                if (!c.AtEnd)
                    throw new ParseException($"unexpected '{c.CurrentText!}' after statement", c.Here);
                stmt.SetSourceSegment(segment);   // defer Token.Render; most structured statements never read SourceText
                result.Statements.Add(stmt);
            }
            catch (ParseException pe)
            {
                result.Diagnostics.Add(ToDiagnostic(pe, sql));
            }
            catch (Exception ex)
            {
                // A parser bug must surface as a rejection, never crash the caller.
                result.Diagnostics.Add(new ParseDiagnostic("internal parser error: " + ex.Message, 1, 1, 0));
            }
        }
        return result;
    }

    // ---- statement dispatch -------------------------------------------------

    // Cached so the per-statement dispatch check doesn't allocate a 9-element string[] each call (audit §1f).
    private static readonly string[] QueryLeaders = { "SELECT", "WITH", "VALUES", "TABLE", "INSERT", "UPDATE", "DELETE", "MERGE", "TRUNCATE" };

    private static string? ClassifyLeading(TokenCursor c)
    {
        if (c.AtAnyWord(QueryLeaders)) return "QUERY";
        if (c.AtSymbol('(')) return "QUERY";   // parenthesised set-op query
        if (c.Current is { Kind: TokenKind.Word } cw && CommandKeywords.Contains(cw.Value)) return "QUERY";
        if (c.AtAnyWord("ALTER", "DROP")) return "QUERY";
        return c.AtWord("CREATE") ? "CREATE" : null;   // PgParser owns every CREATE
    }

    private SqlStatement ParseStatement(TokenCursor c)
    {
        if (c.AtWord("WITH"))
        {
            var (ctes, recursive) = ParseCteList(c);
            if (c.AtWord("INSERT")) return ParseInsert(c, ctes, recursive);
            if (c.AtWord("UPDATE")) return ParseUpdate(c, ctes, recursive);
            if (c.AtWord("DELETE")) return ParseDelete(c, ctes, recursive);
            if (c.AtWord("MERGE")) return ParseMerge(c, ctes, recursive);
            var q = ParseSelectBody(c);
            q.AddWith(ctes); q.WithRecursive = recursive;
            return new QueryStatement { Query = q };
        }
        if (c.AtAnyWord("SELECT", "VALUES", "TABLE") || c.AtSymbol('('))
            return new QueryStatement { Query = ParseSelectStatement(c) };
        if (c.AtWord("INSERT")) return ParseInsert(c, null, false);
        if (c.AtWord("UPDATE")) return ParseUpdate(c, null, false);
        if (c.AtWord("DELETE")) return ParseDelete(c, null, false);
        if (c.AtWord("MERGE")) return ParseMerge(c, null, false);
        if (c.AtWord("TRUNCATE")) return ParseTruncate(c);
        if (c.Current is { Kind: TokenKind.Word } cw && CommandKeywords.Contains(cw.Value)) return ParseCommand(c);
        if (c.AtWord("ALTER")) return ParseAlter(c);
        if (c.AtWord("DROP")) return ParseDrop(c);

        c.ExpectWord("CREATE");
        c.MatchWords("OR", "REPLACE");
        bool temp = false, unlogged = false;
        while (true)
        {
            if (c.MatchWord("GLOBAL") || c.MatchWord("LOCAL")) continue;
            if (c.MatchWord("TEMP") || c.MatchWord("TEMPORARY")) { temp = true; continue; }
            if (c.MatchWord("UNLOGGED")) { unlogged = true; continue; }
            break;
        }
        if (temp && unlogged) throw new ParseException("a table cannot be both UNLOGGED and TEMPORARY", c.Here);
        string? persistence = temp ? "TEMP" : unlogged ? "UNLOGGED" : null;
        c.MatchWord("RECURSIVE");                       // CREATE [OR REPLACE] RECURSIVE VIEW
        bool constraintTrigger = c.AtWord("CONSTRAINT") && c.Peek()?.IsWord("TRIGGER") == true;
        if (constraintTrigger) c.Advance();             // CREATE CONSTRAINT TRIGGER
        if (c.MatchWord("TRIGGER")) return ParseCreateTrigger(c, constraintTrigger);
        if (c.MatchWord("TABLE")) return ParseCreateTable(c, persistence);
        if (c.MatchWord("SCHEMA")) return ParseCreateSchema(c);
        if (c.MatchWords("MATERIALIZED", "VIEW")) return ParseCreateView(c, materialized: true);
        if (c.MatchWord("VIEW")) return ParseCreateView(c, materialized: false);
        if (c.MatchWord("SEQUENCE")) return ParseCreateSequence(c);
        if (c.MatchWord("TYPE")) return ParseCreateType(c);
        if (c.MatchWord("AGGREGATE")) return ParseCreateAggregate(c);
        if (c.MatchWord("COLLATION")) return ParseCreateCollation(c);
        if (c.MatchWords("TEXT", "SEARCH")) return ParseCreateTextSearch(c);
        if (c.AtWord("UNIQUE") || c.AtWord("INDEX")) return ParseCreateIndex(c);
        if (c.AtAnyWord("FUNCTION", "PROCEDURE")) return ParseCreateFunction(c);
        if (c.AtAnyWord("ROLE", "USER", "GROUP")) { var k = c.Advance().Value.ToUpperInvariant(); c.ExpectIdentifier(); ConsumeRest(c); return new CommandStatement { Kind = "CREATE " + k }; }
        // PUBLICATION falls through to ParseCreateGeneric → RawCreateStatement → modelled as a raw object
        // (so it deploys + extracts), not discarded as a CommandStatement.
        if (c.MatchWord("SUBSCRIPTION"))
        {
            c.ExpectIdentifier();
            c.ExpectWord("CONNECTION");
            if (c.Current is not { Kind: TokenKind.String }) throw new ParseException("expected a connection string", c.Here);
            c.Advance();
            c.ExpectWord("PUBLICATION");
            do { c.ExpectIdentifier(); } while (c.MatchSymbol(','));
            ConsumeRest(c);
            return new CommandStatement { Kind = "CREATE SUBSCRIPTION" };
        }
        return ParseCreateGeneric(c);
    }

    // ---- CREATE TABLE -------------------------------------------------------

    private SqlStatement ParseCreateTable(TokenCursor c, string? persistence)
    {
        int pos = c.Here;
        bool ifNotExists = c.MatchWords("IF", "NOT", "EXISTS");
        var (schema, name) = ParseQualifiedName(c);

        // CREATE TABLE name AS query
        if (c.AtWord("AS"))
            return ParseCreateTableAs(c, schema, name, ifNotExists, new List<string>());

        if (c.AtSymbol('('))
        {
            var inner = CaptureBalancedParens(c);

            // CREATE TABLE name (col, col, ...) AS query   — column-alias list, not column defs
            if (c.AtWord("AS"))
            {
                var aliases = ParseIdentifierList(inner);
                return ParseCreateTableAs(c, schema, name, ifNotExists, aliases);
            }

            var table = new CreateTableStatement
            { Position = pos, Schema = schema, Name = name, IfNotExists = ifNotExists, Persistence = persistence };
            ParseTableBody(inner, table);
            if (!c.AtEnd) table.TrailingText = CaptureRest(c);   // PARTITION BY / INHERITS / WITH / TABLESPACE / …
            ValidateColumnReferences(table, c);
            ValidateTableTail(table.TrailingText, persistence, pos);
            return table;
        }

        // PARTITION OF parent … / OF type … — no column list; accept the remainder verbatim.
        if (c.AtAnyWord("SELECT", "VALUES")) throw new ParseException("expected AS before the query in CREATE TABLE AS", c.Here);
        var rest = c.AtEnd ? null : CaptureRest(c);
        ValidateTableTail(rest, persistence, pos);
        return new CreateTableStatement
        { Position = pos, Schema = schema, Name = name, IfNotExists = ifNotExists, Persistence = persistence, IsPartitionOrTyped = true, TrailingText = rest };
    }

    // Validate the (already-captured) CREATE TABLE tail by re-tokenizing it — purely additive, the main
    // parse flow is untouched. Only the clear, catalog-free mistakes are reported (zero false positives).
    private void ValidateTableTail(string? tail, string? persistence, int here)
    {
        if (string.IsNullOrWhiteSpace(tail)) return;
        List<Token> toks;
        try { toks = OperatorLexer.Merge(Tokenizer.Tokenize(tail)); } catch { return; }
        var t = new TokenCursor(toks);
        bool hasPartitionBy = false, hasInherits = false, hasOf = false;

        void EmptyParensError(string what) { if (t.AtSymbol('(') && t.Peek()?.IsSymbol(')') == true) throw new ParseException($"{what} cannot be empty", here); }

        while (!t.AtEnd)
        {
            if (t.MatchWords("PARTITION", "OF")) { continue; }           // partition child — not a PARTITION BY
            if (t.MatchWords("PARTITION", "BY"))
            {
                hasPartitionBy = true;
                if (!t.AtAnyWord("RANGE", "LIST", "HASH")) throw new ParseException("PARTITION BY requires RANGE, LIST or HASH", here);
                t.Advance();
                EmptyParensError("partition key");
                continue;
            }
            if (t.MatchWords("FOR", "VALUES"))
            {
                if (t.MatchWord("IN")) EmptyParensError("partition value list");
                else if (t.MatchWord("FROM")) { EmptyParensError("FROM bound"); SkipBalanced(t); if (t.MatchWord("TO")) EmptyParensError("TO bound"); }
                continue;
            }
            if (t.MatchWords("ON", "COMMIT"))
            {
                if (persistence != "TEMP") throw new ParseException("ON COMMIT can only be used on a temporary table", here);
                if (!(t.MatchWord("DROP") || t.MatchWords("DELETE", "ROWS") || t.MatchWords("PRESERVE", "ROWS")))
                    throw new ParseException("ON COMMIT must be DROP, DELETE ROWS or PRESERVE ROWS", here);
                continue;
            }
            if (t.MatchWord("INHERITS")) { hasInherits = true; EmptyParensError("INHERITS parent list"); continue; }
            if (t.AtWord("UNLOGGED") || t.AtWord("TEMP") || t.AtWord("TEMPORARY")) throw new ParseException($"{t.CurrentText!} must appear before TABLE, not after the definition", here);
            if (t.MatchWord("OF")) { hasOf = true; continue; }
            if (t.MatchWord("WITH") && t.AtSymbol('(')) { EmptyParensError("storage parameter list"); continue; }
            if (t.MatchWord("fillfactor") && t.MatchOperator("=") && t.Current is { Kind: TokenKind.Number } n
                && long.TryParse(n.Value, out var ff) && (ff < 10 || ff > 100))
                throw new ParseException($"fillfactor must be between 10 and 100, got {ff}", here);
            if (!t.AtEnd) t.Advance();
        }

        // cross-clause combinations PostgreSQL forbids
        if (persistence == "UNLOGGED" && hasPartitionBy) throw new ParseException("partitioned tables cannot be UNLOGGED", here);
        if (hasInherits && hasPartitionBy) throw new ParseException("cannot create a partitioned table that also INHERITS", here);
        if (hasOf && hasInherits) throw new ParseException("a typed table (OF) cannot use INHERITS", here);
    }

    private static void SkipBalanced(TokenCursor t)
    {
        if (!t.AtSymbol('(')) return;
        int depth = 0;
        do { if (t.AtSymbol('(')) depth++; else if (t.AtSymbol(')')) depth--; t.Advance(); } while (!t.AtEnd && depth > 0);
    }

    private CreateTableAsStatement ParseCreateTableAs(TokenCursor c, string? schema, string name, bool ifNotExists, List<string> aliases)
    {
        c.ExpectWord("AS");
        if (c.AtEnd) throw new ParseException("expected a query after AS", c.Here);
        if (c.Current is { Kind: TokenKind.Word } qw && !c.AtAnyWord("SELECT", "VALUES", "TABLE", "EXECUTE", "WITH"))
            throw new ParseException($"CREATE TABLE AS source must be SELECT/VALUES/TABLE/EXECUTE, not \"{qw.Value}\"", c.Here);
        var stmt = new CreateTableAsStatement { Schema = schema, Name = name, IfNotExists = ifNotExists };
        stmt.ColumnAliases.AddRange(aliases);

        if (c.AtAnyWord("SELECT", "VALUES", "TABLE", "WITH"))
        {
            int mark = c.Mark();
            stmt.Source = ParseSelectStatement(c);                 // parse the query → surfaces its syntax errors
            if (c.MatchWord("WITH")) { if (c.MatchWord("NO")) { c.ExpectWord("DATA"); stmt.WithData = false; } else { c.ExpectWord("DATA"); stmt.WithData = true; } }
            if (!c.AtEnd) throw new ParseException($"unexpected '{c.CurrentText!}' after the CREATE TABLE AS query", c.Here);
            c.Reset(mark);
            stmt.QueryText = CaptureRest(c);                       // preserve the original verbatim capture
        }
        else
        {
            stmt.QueryText = CaptureRest(c);   // EXECUTE plan … (+ optional WITH [NO] DATA) — kept lenient
            var q = stmt.QueryText.TrimEnd();
            if (q.EndsWith("NO DATA", StringComparison.OrdinalIgnoreCase)) stmt.WithData = false;
            else if (q.EndsWith("WITH DATA", StringComparison.OrdinalIgnoreCase)) stmt.WithData = true;
        }
        return stmt;
    }

    private void ParseTableBody(IReadOnlyList<Token> inner, CreateTableStatement table)
    {
        var b = new TokenCursor(inner);
        if (b.AtEnd) return;                                  // CREATE TABLE x () is legal
        while (true)
        {
            ParseTableElement(b, table);
            if (!b.MatchSymbol(',')) break;
        }
        if (!b.AtEnd) throw new ParseException($"unexpected '{b.CurrentText!}' in table definition", b.Here);

        // case-sensitive: quoted "a"/"A" are distinct columns; comparing ordinally avoids any false positive
        var seenCols = new HashSet<string>(StringComparer.Ordinal);
        foreach (var col in table.Columns)
            if (!seenCols.Add(col.Name)) throw new ParseException($"column \"{col.Name}\" specified more than once", b.Here);
    }

    private void ParseTableElement(TokenCursor b, CreateTableStatement table)
    {
        string? cname = null;
        if (b.MatchWord("CONSTRAINT")) cname = b.ExpectIdentifier();

        if (b.AtAnyWord("PRIMARY", "UNIQUE", "FOREIGN", "CHECK", "EXCLUDE"))
        {
            table.Constraints.Add(ParseTableConstraint(b, cname));
            return;
        }

        // table-level NOT NULL column_name [NO INHERIT]  (PostgreSQL 18)
        if (b.AtWord("NOT"))
        {
            if (!b.MatchWords("NOT", "NULL")) throw new ParseException("expected NOT NULL", b.Here);
            var nn = new NotNullTableConstraint { Name = cname, Column = b.ExpectIdentifier() };
            if (b.MatchWords("NO", "INHERIT")) nn.NoInherit = true;
            table.Constraints.Add(nn);
            return;
        }

        if (cname is not null)
            throw new ParseException("expected a table constraint after CONSTRAINT name", b.Here);

        if (b.AtWord("LIKE"))   // LIKE source [ { INCLUDING | EXCLUDING } option ]…
        {
            b.Advance();
            ParseQualifiedName(b);
            while (b.MatchWord("INCLUDING") || b.MatchWord("EXCLUDING"))
            {
                var opt = b.ExpectIdentifier();
                if (!LikeOptions.Contains(opt)) throw new ParseException($"unrecognized LIKE option \"{opt}\"", b.Here);
            }
            table.HasLikeElement = true;
            return;
        }

        table.Columns.Add(ParseColumnDef(b));
    }

    // ---- columns ------------------------------------------------------------

    private ColumnDef ParseColumnDef(TokenCursor b)
    {
        var name = b.ExpectIdentifier();
        var type = ParseTypeName(b);
        var col = new ColumnDef { Name = name, Type = type };
        ParseColumnConstraints(b, col);
        return col;
    }

    private TypeName ParseTypeName(TokenCursor b)
    {
        // Track [start, b.Mark()) over the cursor's own token list and render that range directly, rather
        // than copying each token into a throwaway List<Token>. Token is a 24-byte struct, so that list's
        // backing array was 3x heavier than a list of references — and ParseTypeName runs for every column
        // of every table (the hottest capture site). The renderer is identical, so Text is byte-for-byte.
        int start = b.Mark();
        int depth = 0;
        while (!b.AtEnd)
        {
            var t = b.Current!.Value;
            if (depth == 0)
            {
                if (t.IsSymbol(',')) break;
                if (t.Kind == TokenKind.Word && ColumnConstraintStart.Contains(t.Value)) break;
            }
            if (t.IsSymbol('(') || t.IsSymbol('[')) depth++;
            else if (t.IsSymbol(')') || t.IsSymbol(']')) depth = Math.Max(0, depth - 1);
            b.Advance();
        }
        if (b.Mark() == start) throw new ParseException("expected a column data type", b.Here);
        var tn = new TypeName { Text = b.RenderRange(start, b.Mark()) };
        ValidateTypeModifiers(tn.Text, b);
        return tn;
    }

    private static readonly HashSet<string> NoModifierTypes = new(StringComparer.OrdinalIgnoreCase)
    { "int", "integer", "int4", "int2", "smallint", "bigint", "int8", "serial", "serial4", "serial2", "smallserial", "bigserial", "serial8" };
    private static readonly HashSet<string> IntervalRanges = new(StringComparer.OrdinalIgnoreCase)
    { "year to month", "day to hour", "day to minute", "day to second", "hour to minute", "hour to second", "minute to second" };

    // Validate the numeric type modifiers PostgreSQL rejects at parse time. Conservative: only known type
    // families are checked, and any non-integer / unparseable modifier is left alone (no false positives).
    private static void ValidateTypeModifiers(string typeText, TokenCursor at)
    {
        var s = typeText.Trim();
        int lp = s.IndexOf('(');
        var baseName = (lp >= 0 ? s[..lp] : s).Trim().ToLowerInvariant();

        if (lp >= 0 && NoModifierTypes.Contains(baseName))
            throw new ParseException($"type \"{baseName}\" does not accept a length/precision modifier", at.Here);

        // INTERVAL field-range qualifier (e.g. INTERVAL MONTH TO DAY is invalid)
        if (baseName.StartsWith("interval", StringComparison.OrdinalIgnoreCase) && baseName.Contains(" to "))
        {
            var q = baseName["interval".Length..].Trim();
            if (!IntervalRanges.Contains(q)) throw new ParseException($"invalid INTERVAL field qualifier: {q}", at.Here);
        }

        if (lp < 0) return;
        int rp = s.IndexOf(')', lp);
        if (rp <= lp) return;
        var parts = s[(lp + 1)..rp].Split(',');
        var nums = new List<long>();
        foreach (var p in parts) { if (!long.TryParse(p.Trim(), out var v)) return; nums.Add(v); }   // non-integer modifier → not ours to judge
        if (nums.Count == 0) return;

        switch (baseName)
        {
            case "numeric" or "decimal":
                if (nums.Count > 2) throw new ParseException("NUMERIC takes at most precision and scale", at.Here);
                if (nums[0] < 1 || nums[0] > 1000) throw new ParseException($"NUMERIC precision {nums[0]} must be between 1 and 1000", at.Here);
                if (nums.Count == 2 && (nums[1] < -1000 || nums[1] > 1000)) throw new ParseException($"NUMERIC scale {nums[1]} must be between -1000 and 1000", at.Here);
                break;
            case "varchar" or "character varying" or "char" or "character" or "bpchar" or "bit" or "varbit" or "bit varying":
                if (nums.Count == 1 && nums[0] < 1) throw new ParseException($"length for type {baseName} must be at least 1", at.Here);
                break;
            case "float":
                if (nums.Count == 1 && (nums[0] < 1 || nums[0] > 53)) throw new ParseException($"FLOAT precision {nums[0]} must be between 1 and 53", at.Here);
                break;
            case "timestamp" or "timestamptz" or "time" or "timetz" or "interval":
                // precision > 6 is only a warning in PostgreSQL (clamped); a negative precision is an error.
                if (nums.Count == 1 && nums[0] < 0) throw new ParseException($"{baseName} precision must not be negative", at.Here);
                break;
        }
    }

    private void ParseColumnConstraints(TokenCursor b, ColumnDef col)
    {
        while (!b.AtEnd && !b.AtSymbol(','))
        {
            string? cname = null;
            if (b.MatchWord("CONSTRAINT")) cname = b.ExpectIdentifier();

            ColumnConstraint? made = ParseOneColumnConstraint(b);
            if (made is null)
            {
                if (cname is not null) throw new ParseException("expected a column constraint after CONSTRAINT name", b.Here);
                break;
            }
            made.Name = cname;
            col.Constraints.Add(made);
        }

        // reject contradictory / duplicate column constraints — pure structure, no catalog
        int defaults = 0, generated = 0; bool notNull = false, nullable = false;
        foreach (var cc in col.Constraints)
        {
            if (cc is DefaultConstraint) defaults++;
            else if (cc is GeneratedStored or GeneratedIdentity) generated++;
            else if (cc is NotNullConstraint) notNull = true;
            else if (cc is NullConstraint) nullable = true;
        }
        if (notNull && nullable) throw new ParseException($"conflicting NULL / NOT NULL declarations for column \"{col.Name}\"", b.Here);
        if (defaults > 1) throw new ParseException($"multiple default values specified for column \"{col.Name}\"", b.Here);
        if (generated > 1) throw new ParseException($"multiple generation clauses specified for column \"{col.Name}\"", b.Here);
        if (defaults > 0 && generated > 0) throw new ParseException($"both default and generation expression specified for column \"{col.Name}\"", b.Here);

        // Type-aware checks for KNOWN built-in types only (a domain/custom type → skipped, so zero-FP).
        var baseType = ColumnBaseType(col.Type.Text);
        if (IsTypeKeyword(baseType))
        {
            if (col.Constraints.Any(c => c is CollateConstraint) && !CollatableTypes.Contains(baseType))
                throw new ParseException($"collations are not supported for type \"{baseType}\"", b.Here);
            if (col.Constraints.Any(c => c is GeneratedIdentity) && !IdentityTypes.Contains(baseType))
                throw new ParseException($"identity column \"{col.Name}\" must be an integer type, not \"{baseType}\"", b.Here);
        }
    }

    private static readonly HashSet<string> CollatableTypes = new(StringComparer.OrdinalIgnoreCase)
    { "text", "varchar", "char", "character", "bpchar", "name" };
    private static readonly HashSet<string> IdentityTypes = new(StringComparer.OrdinalIgnoreCase)
    { "smallint", "int2", "int", "int4", "integer", "bigint", "int8", "numeric", "decimal" };

    private static string ColumnBaseType(string text)
    {
        var s = text.Trim().ToLowerInvariant();
        var p = s.IndexOf('(');
        if (p >= 0) s = s[..p];
        return s.Replace("[]", "").Trim();
    }

    private ColumnConstraint? ParseOneColumnConstraint(TokenCursor b)
    {
        if (b.MatchWords("NOT", "NULL")) { var nn = new NotNullConstraint(); b.MatchWords("NO", "INHERIT"); return nn; }
        if (b.MatchWord("NULL")) return new NullConstraint();
        if (b.MatchWord("DEFAULT")) return new DefaultConstraint { Expression = CaptureExpression(b) };
        if (b.MatchWord("COLLATE")) { var (cs, cn) = ParseQualifiedName(b); return new CollateConstraint { Collation = cs is null ? cn : $"{cs}.{cn}" }; }
        if (b.MatchWord("STORAGE")) { var v = b.ExpectIdentifier(); if (!StorageModes.Contains(v)) throw new ParseException($"invalid storage mode \"{v}\"", b.Here); return new StorageOption { Kind = "STORAGE", Value = v }; }
        if (b.MatchWord("COMPRESSION")) return new StorageOption { Kind = "COMPRESSION", Value = b.ExpectIdentifier() };

        if (b.MatchWords("PRIMARY", "KEY"))
        {
            var pk = new InlinePrimaryKey();
            ParseIndexTrailing(b, pk.Include, pk.Deferrability);
            return pk;
        }
        if (b.MatchWord("UNIQUE"))
        {
            var u = new InlineUnique();
            if (b.MatchWords("NULLS", "NOT", "DISTINCT")) u.NullsNotDistinct = true;
            else b.MatchWords("NULLS", "DISTINCT");
            ParseIndexTrailing(b, u.Include, u.Deferrability);
            return u;
        }
        if (b.MatchWord("REFERENCES")) return ParseInlineReferences(b);

        if (b.MatchWord("CHECK"))
        {
            var chk = new InlineCheck { Expression = ParseParenExpression(b) };
            ParseCheckTrailing(b, valid: v => chk.NotValid = v, noInherit: v => chk.NoInherit = v);
            return chk;
        }
        if (b.MatchWord("GENERATED"))
        {
            if (b.MatchWords("ALWAYS", "AS"))
            {
                if (b.AtSymbol('('))
                {
                    var expr = ParseParenExpression(b);
                    if (!b.MatchWord("STORED")) b.MatchWord("VIRTUAL");   // PG18: omitted defaults to VIRTUAL
                    return new GeneratedStored { Expression = expr };
                }
                b.ExpectWord("IDENTITY");
                if (b.AtSymbol('(')) b.SkipBalancedParens();
                return new GeneratedIdentity { Kind = "ALWAYS" };
            }
            b.MatchWords("BY", "DEFAULT");
            b.ExpectWord("AS");
            b.ExpectWord("IDENTITY");
            if (b.AtSymbol('(')) b.SkipBalancedParens();
            return new GeneratedIdentity { Kind = "BY DEFAULT" };
        }

        return null;
    }

    private InlineReferences ParseInlineReferences(TokenCursor b)
    {
        var (rs, rt) = ParseQualifiedName(b);
        var node = new InlineReferences { RefSchema = rs, RefTable = rt };
        if (b.AtSymbol('(')) node.RefColumns.AddRange(ParseColumnNameList(b));
        while (true)
        {
            if (b.MatchWord("MATCH")) { var m = b.ExpectIdentifier(); if (!MatchTypes.Contains(m)) throw new ParseException($"invalid MATCH type \"{m}\" (expected FULL or SIMPLE)", b.Here); node.Match = m; continue; }
            if (b.MatchWords("ON", "DELETE")) { node.OnDelete = ParseRefAction(b, allowColumns: true); continue; }
            if (b.MatchWords("ON", "UPDATE")) { node.OnUpdate = ParseRefAction(b, allowColumns: false); continue; }
            if (b.LookaheadWords("NOT", "VALID")) { b.MatchWords("NOT", "VALID"); node.NotValid = true; continue; }
            if (TryEnforced(b)) continue;
            if (TryDeferrable(b, node.Deferrability)) continue;
            break;
        }
        ValidateDeferrability(node.Deferrability, b);
        return node;
    }

    // ---- table constraints --------------------------------------------------

    private TableConstraint ParseTableConstraint(TokenCursor b, string? name)
    {
        if (b.MatchWords("PRIMARY", "KEY"))
        {
            var pk = new PrimaryKeyConstraint { Name = name };
            if (IsUsingExistingIndex(b)) { b.MatchWords("USING", "INDEX"); b.ExpectIdentifier(); while (TryDeferrable(b, pk.Deferrability)) { } return pk; }
            pk.Columns.AddRange(ParseColumnNameList(b));
            ParseIndexTrailing(b, pk.Include, pk.Deferrability);
            return pk;
        }
        if (b.MatchWord("UNIQUE"))
        {
            var u = new UniqueConstraint { Name = name };
            if (b.MatchWords("NULLS", "NOT", "DISTINCT")) u.NullsNotDistinct = true;
            else b.MatchWords("NULLS", "DISTINCT");
            if (IsUsingExistingIndex(b)) { b.MatchWords("USING", "INDEX"); b.ExpectIdentifier(); while (TryDeferrable(b, u.Deferrability)) { } return u; }
            u.Columns.AddRange(ParseColumnNameList(b));
            ParseIndexTrailing(b, u.Include, u.Deferrability);
            return u;
        }
        if (b.MatchWords("FOREIGN", "KEY"))
        {
            var fk = new ForeignKeyConstraint { Name = name };
            fk.Columns.AddRange(ParseColumnNameList(b));
            b.ExpectWord("REFERENCES");
            var (rs, rt) = ParseQualifiedName(b);
            var fk2 = new ForeignKeyConstraint { Name = name, RefSchema = rs, RefTable = rt };
            fk2.Columns.AddRange(fk.Columns);
            if (b.AtSymbol('(')) fk2.RefColumns.AddRange(ParseColumnNameList(b));
            while (true)
            {
                if (b.MatchWord("MATCH")) { fk2.Match = b.ExpectIdentifier(); continue; }
                if (b.MatchWords("ON", "DELETE")) { fk2.OnDelete = ParseRefAction(b, allowColumns: true); continue; }
                if (b.MatchWords("ON", "UPDATE")) { fk2.OnUpdate = ParseRefAction(b, allowColumns: false); continue; }
                if (b.LookaheadWords("NOT", "VALID")) { b.MatchWords("NOT", "VALID"); fk2.NotValid = true; continue; }
                if (TryEnforced(b)) continue;
                if (TryDeferrable(b, fk2.Deferrability)) continue;
                break;
            }
            ValidateDeferrability(fk2.Deferrability, b);
            ValidateSetColumnsSubset(fk2, b);
            return fk2;
        }
        if (b.MatchWord("CHECK"))
        {
            var chk = new CheckConstraint { Name = name, Expression = ParseParenExpression(b) };
            ParseCheckTrailing(b, valid: v => chk.NotValid = v, noInherit: v => chk.NoInherit = v);
            return chk;
        }
        if (b.MatchWord("EXCLUDE"))
            return new ExcludeConstraint { Name = name, RawText = CaptureToElementEnd(b) };

        throw new ParseException($"expected a table constraint but found {Render(b.Current)}", b.Here);
    }

    // ALTER TABLE ADD [CONSTRAINT n] {PRIMARY KEY|UNIQUE} USING INDEX existing_index — no column list.
    // Distinguish from the CREATE-TABLE "USING INDEX TABLESPACE x" trailing form (which has columns first).
    private static bool IsUsingExistingIndex(TokenCursor b)
        => b.AtWord("USING") && b.Peek()?.IsWord("INDEX") == true && b.Peek(2)?.IsWord("TABLESPACE") != true;

    /// <summary>INCLUDE (cols), WITH (params), USING INDEX TABLESPACE, then deferrability — shared by PK/UNIQUE.</summary>
    private void ParseIndexTrailing(TokenCursor b, List<string> include, Deferrability defer)
    {
        if (b.MatchWord("INCLUDE")) include.AddRange(ParseColumnNameList(b));
        if (b.MatchWord("WITH") && b.AtSymbol('(')) b.SkipBalancedParens();
        if (b.MatchWords("USING", "INDEX", "TABLESPACE")) b.ExpectIdentifier();
        while (TryDeferrable(b, defer)) { }
        ValidateDeferrability(defer, b);
    }

    /// <summary>CHECK trailing: NO INHERIT / NOT VALID / [NOT] ENFORCED. CHECK is never deferrable.</summary>
    private void ParseCheckTrailing(TokenCursor b, Action<bool> valid, Action<bool> noInherit)
    {
        while (true)
        {
            if (b.MatchWords("NO", "INHERIT")) { noInherit(true); continue; }
            if (b.LookaheadWords("NOT", "VALID")) { b.MatchWords("NOT", "VALID"); valid(true); continue; }
            if (TryEnforced(b)) continue;
            break;
        }
    }

    private static bool TryDeferrable(TokenCursor b, Deferrability d)
    {
        if (b.LookaheadWords("NOT", "DEFERRABLE")) { b.MatchWords("NOT", "DEFERRABLE"); SetDeferrable(d, false, b); return true; }
        if (b.MatchWord("DEFERRABLE")) { SetDeferrable(d, true, b); return true; }
        if (b.MatchWords("INITIALLY", "DEFERRED")) { SetInitially(d, true, b); return true; }
        if (b.MatchWords("INITIALLY", "IMMEDIATE")) { SetInitially(d, false, b); return true; }
        return false;
    }

    private static void SetDeferrable(Deferrability d, bool v, TokenCursor b)
    {
        if (d.Deferrable.HasValue) throw new ParseException("conflicting or redundant DEFERRABLE options", b.Here);
        d.Deferrable = v;
    }

    private static void SetInitially(Deferrability d, bool v, TokenCursor b)
    {
        if (d.InitiallyDeferred.HasValue) throw new ParseException("conflicting or redundant INITIALLY options", b.Here);
        d.InitiallyDeferred = v;
    }

    private static bool TryEnforced(TokenCursor b)
    {
        if (b.LookaheadWords("NOT", "ENFORCED")) { b.MatchWords("NOT", "ENFORCED"); return true; }
        if (b.MatchWord("ENFORCED")) return true;
        return false;
    }

    /// <summary>PostgreSQL: a constraint declared INITIALLY DEFERRED must be DEFERRABLE.</summary>
    private static void ValidateDeferrability(Deferrability d, TokenCursor b)
    {
        if (d.Deferrable == false && d.InitiallyDeferred == true)
            throw new ParseException("constraint declared INITIALLY DEFERRED must be DEFERRABLE", b.Here);
    }

    private RefAction ParseRefAction(TokenCursor b, bool allowColumns)
    {
        if (b.MatchWord("CASCADE")) return new RefAction { Action = "CASCADE" };
        if (b.MatchWord("RESTRICT")) return new RefAction { Action = "RESTRICT" };
        if (b.MatchWords("NO", "ACTION")) return new RefAction { Action = "NO ACTION" };
        if (b.MatchWords("SET", "NULL")) return ParseSetAction(b, "SET NULL", allowColumns);
        if (b.MatchWords("SET", "DEFAULT")) return ParseSetAction(b, "SET DEFAULT", allowColumns);
        throw new ParseException($"expected a referential action but found {Render(b.Current)}", b.Here);
    }

    private RefAction ParseSetAction(TokenCursor b, string action, bool allowColumns)
    {
        var a = new RefAction { Action = action };
        if (b.AtSymbol('('))
        {
            if (!allowColumns) throw new ParseException("a column list with SET NULL/SET DEFAULT is only allowed for ON DELETE", b.Here);
            a.Columns.AddRange(ParseColumnNameList(b));
        }
        return a;
    }

    /// <summary>
    /// Every column named by a PK / UNIQUE / FK-local / NOT NULL / INCLUDE clause must be a column
    /// the table defines. Skipped when columns come from elsewhere (LIKE, INHERITS, OF type).
    /// </summary>
    private static void ValidateColumnReferences(CreateTableStatement t, TokenCursor c)
    {
        if (t.IsPartitionOrTyped || t.HasLikeElement || t.Columns.Count == 0) return;
        if (t.TrailingText is not null && t.TrailingText.Contains("INHERITS", StringComparison.OrdinalIgnoreCase)) return;

        var defined = new HashSet<string>(t.Columns.Select(x => x.Name), StringComparer.OrdinalIgnoreCase);
        void Check(IEnumerable<string> cols)
        {
            foreach (var col in cols)
                if (!defined.Contains(col))
                    throw new ParseException($"column \"{col}\" named in a constraint does not exist", c.Here);
        }
        foreach (var con in t.Constraints)
        {
            switch (con)
            {
                case PrimaryKeyConstraint pk: Check(pk.Columns); Check(pk.Include); break;
                case UniqueConstraint u: Check(u.Columns); Check(u.Include); break;
                case ForeignKeyConstraint fk: Check(fk.Columns); break;
                case NotNullTableConstraint nn: Check(new[] { nn.Column }); break;
            }
        }
    }

    /// <summary>The ON DELETE SET NULL/DEFAULT column list must be a subset of the FK columns.</summary>
    private static void ValidateSetColumnsSubset(ForeignKeyConstraint fk, TokenCursor b)
    {
        if (fk.OnDelete is { Columns.Count: > 0 })
            foreach (var c in fk.OnDelete.Columns)
                if (!fk.Columns.Contains(c, StringComparer.OrdinalIgnoreCase))
                    throw new ParseException($"column \"{c}\" referenced in ON DELETE SET is not part of the foreign key", b.Here);
    }

    // ---- CREATE SCHEMA ------------------------------------------------------

    private CreateSchemaStatement ParseCreateSchema(TokenCursor c)
    {
        int pos = c.Here;
        bool ifNotExists = c.MatchWords("IF", "NOT", "EXISTS");
        string? name = null, auth = null;
        if (c.MatchWord("AUTHORIZATION")) auth = ParseRoleSpec(c);
        else
        {
            name = c.ExpectIdentifier();
            if (c.MatchWord("AUTHORIZATION")) auth = ParseRoleSpec(c);
        }
        if (!c.AtEnd) CaptureRest(c);    // optional inline schema elements — accepted, not modelled here
        return new CreateSchemaStatement { Position = pos, Name = name, IfNotExists = ifNotExists, Authorization = auth };
    }

    private static string ParseRoleSpec(TokenCursor c)
    {
        if (c.MatchWords("CURRENT", "USER")) return "CURRENT_USER";
        return c.ExpectIdentifier();   // CURRENT_USER/SESSION_USER tokenise as single words too
    }

    // ---- small shared helpers ----------------------------------------------

    private (string?, string) ParseQualifiedName(TokenCursor c)
    {
        var first = c.ExpectIdentifier();
        if (c.MatchSymbol('.')) return (first, c.ExpectIdentifier());
        return (null, first);
    }

    private List<string> ParseColumnNameList(TokenCursor b)
    {
        var cols = new List<string>();
        b.ExpectSymbol('(');
        if (b.AtSymbol(')')) throw new ParseException("empty column list", b.Here);
        while (true)
        {
            cols.Add(b.ExpectIdentifier());
            if (!b.MatchSymbol(',')) break;
        }
        b.ExpectSymbol(')');
        return cols;
    }

    private static List<string> ParseIdentifierList(IReadOnlyList<Token> inner)
    {
        var b = new TokenCursor(inner);
        var ids = new List<string>();
        while (true)
        {
            var id = b.ExpectIdentifier();
            if (ids.Contains(id, StringComparer.OrdinalIgnoreCase))
                throw new ParseException($"column \"{id}\" specified more than once", b.Here);
            ids.Add(id);
            if (!b.MatchSymbol(',')) break;
        }
        if (!b.AtEnd) throw new ParseException($"unexpected '{b.CurrentText!}' in column-alias list", b.Here);
        return ids;
    }

    /// <summary>
    /// Capture a balanced (...) and return the inner tokens as a read-only window over the cursor's own
    /// token list (outer parens consumed) — no copy. The run is contiguous, so a <see cref="TokenSegment"/>
    /// view replaces the old per-call <c>List&lt;Token&gt;</c>; callers either re-parse it through a fresh
    /// cursor or render it, both of which only read.
    /// </summary>
    private static IReadOnlyList<Token> CaptureBalancedParens(TokenCursor c)
    {
        c.ExpectSymbol('(');
        int start = c.Mark();
        int depth = 1;
        while (!c.AtEnd)
        {
            var t = c.Advance();
            // c.Mark() now points just past this token; the closing ')' is excluded from the window.
            if (t.IsSymbol(')')) { if (--depth == 0) return c.Segment(start, c.Mark() - 1); }
            else if (t.IsSymbol('(')) depth++;
        }
        throw new ParseException("unbalanced '('", c.Here);
    }

    /// <summary>A parenthesised expression as raw text (CHECK / GENERATED AS); parens consumed.</summary>
    private static string ParseParenExpression(TokenCursor b)
    {
        if (!b.AtSymbol('(')) throw new ParseException("expected '('", b.Here);
        return Token.Render(CaptureBalancedParens(b));
    }

    /// <summary>A scalar expression (DEFAULT value) up to a top-level comma or column-constraint keyword.</summary>
    private static string CaptureExpression(TokenCursor b)
    {
        int start = b.Mark();
        int depth = 0;
        while (!b.AtEnd)
        {
            var t = b.Current!.Value;
            // Stop at a top-level comma or a column-constraint keyword, but only AFTER consuming the
            // first token — so a literal default like NULL/TRUE/FALSE is taken as the value.
            if (depth == 0 && b.Mark() > start)
            {
                if (t.IsSymbol(',')) break;
                if (t.Kind == TokenKind.Word && ColumnConstraintStart.Contains(t.Value)) break;
            }
            if (depth == 0 && b.Mark() == start && t.IsSymbol(',')) break;
            if (t.IsSymbol('(') || t.IsSymbol('[')) depth++;
            else if (t.IsSymbol(')') || t.IsSymbol(']')) depth = Math.Max(0, depth - 1);
            b.Advance();
        }
        if (b.Mark() == start) throw new ParseException("expected an expression", b.Here);
        return b.RenderRange(start, b.Mark());
    }

    private static string CaptureToElementEnd(TokenCursor b)
    {
        int start = b.Mark();
        int depth = 0;
        while (!b.AtEnd)
        {
            var t = b.Current!.Value;
            if (depth == 0 && t.IsSymbol(',')) break;
            if (t.IsSymbol('(') || t.IsSymbol('[')) depth++;
            else if (t.IsSymbol(')') || t.IsSymbol(']')) depth = Math.Max(0, depth - 1);
            b.Advance();
        }
        return b.RenderRange(start, b.Mark());
    }

    private static string CaptureRest(TokenCursor c)
    {
        int start = c.Mark();
        while (!c.AtEnd) c.Advance();
        return c.RenderRange(start, c.Mark());
    }

    private static string Render(Token? t) => t is null ? "end of input" : $"'{t.Value}'";

    // ---- statement splitting + diagnostics ---------------------------------

    // Yields a read-only window per top-level-';'-delimited statement instead of copying each
    // statement's tokens into a fresh List<Token>. Those lists' Token[] backing arrays (churned by
    // doubling growth) were ~7 MB/op on the All bucket; a window has none. Segments are read-only
    // (consumed via TokenCursor + lazy render), so a view is sufficient. (AllocProbe-driven.)
    private static IEnumerable<TokenSegment> SplitStatements(IReadOnlyList<Token> tokens)
    {
        int depth = 0, start = 0;
        for (int i = 0; i < tokens.Count; i++)
        {
            var t = tokens[i];
            if (t.IsSymbol('(')) depth++;
            else if (t.IsSymbol(')')) depth = Math.Max(0, depth - 1);
            if (t.IsSymbol(';') && depth == 0)
            {
                if (i > start) yield return new TokenSegment(tokens, start, i - start);  // exclude the ';'
                start = i + 1;
            }
        }
        if (tokens.Count > start) yield return new TokenSegment(tokens, start, tokens.Count - start);
    }

    private static ParseDiagnostic ToDiagnostic(ParseException pe, string sql)
    {
        int line = 1, col = 1;
        int limit = Math.Min(pe.Offset, sql.Length);
        for (int i = 0; i < limit; i++)
        {
            if (sql[i] == '\n') { line++; col = 1; }
            else col++;
        }
        return new ParseDiagnostic(pe.Message, line, col, pe.Offset);
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using PgProj.Core.Syntax;

namespace PgProj.Core.Analysis;

/// <summary>
/// Static safety analysis over the PgParser AST (replaces the legacy SqlAnalyzer). Catches the
/// high-value lints a database project should not deploy without a look:
///   PG001 SECURITY DEFINER function without SET search_path (privilege-escalation risk)
///   PG002 dynamic SQL (EXECUTE) in a function body
///   PG003 UPDATE/DELETE without a WHERE clause (whole-table mutation)
///   PG004 schema mutation (CREATE/ALTER/DROP) inside a function body
///   PG005 function without a declared volatility (defaults to VOLATILE)
///   PG006 table without a PRIMARY KEY
///   PG007 SELECT * in a view body (brittle to column changes)
///   PG008 numeric/decimal column without precision/scale
///   PG009 LIMIT without ORDER BY (non-deterministic result)
///   PG010 blank-padded char(n) column (use text/varchar)
///   PG011 timestamp WITHOUT time zone column (use timestamptz)
///   PG012 serial column (use GENERATED ... AS IDENTITY)
///   PG013 money column (locale-dependent; use numeric)
///   PG015 identifier with uppercase letters (case-fold footgun / forced quoting)
///   PG016 identifier longer than 63 bytes (silently truncated by PostgreSQL)
///   PG017 json column (no indexing/dedup; prefer jsonb)
///   PG020 EXCEPTION WHEN OTHERS in a PL/pgSQL body (swallows every error)
///   PG021 SELECT ... INTO without STRICT in a PL/pgSQL body (no-row/multi-row goes silent)
/// Complements the semantic analyzer (catalog/type/structural). Function-body checks are textual
/// because PgParser keeps bodies verbatim; everything else is AST-precise.
/// </summary>
/// <remarks>
/// Findings are configurable (EP-ANALYSIS+): an <see cref="AnalysisConfig"/> can disable a rule or
/// override its severity, layered CLI &gt; sidecar &gt; the rule defaults below. The default-free
/// <see cref="Analyze(ParseResult)"/> overload keeps the original all-rules-on behaviour for callers
/// (and the model layer) that don't carry a config.
/// </remarks>
public sealed class PgAnalyzer
{
    /// <summary>Every rule this analyzer can emit, with its natural (default) severity and a one-line title.</summary>
    public static readonly IReadOnlyList<RuleInfo> RuleDefaults = new[]
    {
        new RuleInfo("PG001", DiagnosticSeverity.Warning, "SECURITY DEFINER without SET search_path"),
        new RuleInfo("PG002", DiagnosticSeverity.Info,    "Dynamic SQL (EXECUTE) in a function body"),
        new RuleInfo("PG003", DiagnosticSeverity.Warning, "UPDATE/DELETE without a WHERE clause"),
        new RuleInfo("PG004", DiagnosticSeverity.Warning, "Schema mutation inside a function body"),
        new RuleInfo("PG005", DiagnosticSeverity.Info,    "Function without a declared volatility"),
        new RuleInfo("PG006", DiagnosticSeverity.Info,    "Table without a primary key"),
        new RuleInfo("PG007", DiagnosticSeverity.Info,    "SELECT * in a view body"),
        new RuleInfo("PG008", DiagnosticSeverity.Info,    "numeric/decimal column without precision/scale"),
        new RuleInfo("PG009", DiagnosticSeverity.Info,    "LIMIT without ORDER BY"),
        new RuleInfo("PG010", DiagnosticSeverity.Info,    "Blank-padded char(n) column"),
        new RuleInfo("PG011", DiagnosticSeverity.Info,    "timestamp without time zone column"),
        new RuleInfo("PG012", DiagnosticSeverity.Info,    "serial column instead of an identity column"),
        new RuleInfo("PG013", DiagnosticSeverity.Info,    "money column"),
        new RuleInfo("PG015", DiagnosticSeverity.Info,    "Identifier with uppercase letters (case-fold/quoting footgun)"),
        new RuleInfo("PG016", DiagnosticSeverity.Warning, "Identifier longer than 63 bytes (silently truncated)"),
        new RuleInfo("PG017", DiagnosticSeverity.Info,    "json column (prefer jsonb)"),
        new RuleInfo("PG020", DiagnosticSeverity.Warning, "EXCEPTION WHEN OTHERS in a function body"),
        new RuleInfo("PG021", DiagnosticSeverity.Warning, "SELECT ... INTO without STRICT in a function body"),
    };

    private static readonly Dictionary<string, RuleInfo> ById =
        RuleDefaults.ToDictionary(r => r.Id, StringComparer.OrdinalIgnoreCase);

    /// <summary>The number of distinct rules the analyzer knows about.</summary>
    public static int RuleCount => RuleDefaults.Count;

    /// <summary>The known rule ids, in declaration order (for usage/error messages).</summary>
    public static IEnumerable<string> RuleIds => RuleDefaults.Select(r => r.Id);

    /// <summary>True when <paramref name="ruleId"/> is a rule this analyzer can emit.</summary>
    public static bool IsKnownRule(string ruleId) => ruleId is not null && ById.ContainsKey(ruleId);

    /// <summary>The natural default severity of a known rule, or <see cref="DiagnosticSeverity.Warning"/>.</summary>
    public static DiagnosticSeverity DefaultSeverityOf(string ruleId) =>
        ById.TryGetValue(ruleId, out var r) ? r.DefaultSeverity : DiagnosticSeverity.Warning;

    private readonly AnalysisConfig _config;

    /// <summary>Creates an analyzer with all rules at their defaults (backward-compatible).</summary>
    public PgAnalyzer() : this(AnalysisConfig.Empty) { }

    /// <summary>Creates an analyzer that honours <paramref name="config"/> (rule enable/severity overrides).</summary>
    public PgAnalyzer(AnalysisConfig? config) => _config = config ?? AnalysisConfig.Empty;

    public IReadOnlyList<Diagnostic> Analyze(ParseResult result)
    {
        var diags = new List<Diagnostic>();
        foreach (var stmt in result.Statements)
        {
            switch (stmt)
            {
                case CreateFunctionStatement f: AnalyzeFunction(f, diags); break;
                case CreateTableStatement t: AnalyzeTable(t, diags); break;
                case CreateViewStatement v: CheckSelectStar(v, diags); break;
                case QueryStatement q: CheckLimit(q.Query, $"{q.Query.From?.Relations.Count}", diags); break;
                case UpdateStatement u when u.Where is null && u.WhereCurrentOf is null:
                    Emit(diags, "PG003", "UPDATE without a WHERE clause mutates every row.", Q(u.Schema, u.Table)); break;
                case DeleteStatement d when d.Where is null && d.WhereCurrentOf is null:
                    Emit(diags, "PG003", "DELETE without a WHERE clause removes every row.", Q(d.Schema, d.Table)); break;
            }
        }
        return diags;
    }

    private void AnalyzeFunction(CreateFunctionStatement f, List<Diagnostic> diags)
    {
        var sig = $"{f.Schema ?? "public"}.{f.Name}";
        var full = (f.SourceText ?? "").ToLowerInvariant();

        // header-level checks run over the whole statement
        if (!Regex.IsMatch(full, @"\b(immutable|stable|volatile)\b"))
            Emit(diags, "PG005", "No volatility declared; defaults to VOLATILE, which the planner cannot optimize or inline.", sig);
        if (Regex.IsMatch(full, @"\bsecurity\s+definer\b") && !Regex.IsMatch(full, @"\bsearch_path\b"))
            Emit(diags, "PG001", "SECURITY DEFINER without SET search_path is a privilege-escalation risk.", sig);

        // body-level checks run only over the routine body (the dollar-quoted block), not the header
        var bm = Regex.Match(f.SourceText ?? "", @"\$(\w*)\$(.*)\$\1\$", RegexOptions.Singleline);
        var body = bm.Success ? bm.Groups[2].Value.ToLowerInvariant() : "";
        if (Regex.IsMatch(body, @"\bexecute\b"))
            Emit(diags, "PG002", "Dynamic SQL (EXECUTE) in a function body — ensure inputs are quoted (quote_ident/format).", sig);
        // optional modifiers between the verb and the object keyword (TEMP/UNLOGGED/OR REPLACE/
        // UNIQUE/GLOBAL/LOCAL/MATERIALIZED/CONCURRENTLY) must not defeat the match (#65)
        if (Regex.IsMatch(body, @"\b(create|alter|drop)\s+(?:(?:or\s+replace|global|local|temp(?:orary)?|unlogged|unique|materialized|concurrently)\s+)*(table|view|index|sequence|schema|type|function)\b"))
            Emit(diags, "PG004", "Schema mutation (CREATE/ALTER/DROP) inside a function body.", sig);
        if (Regex.IsMatch(body, @"\bwhen\s+others\b"))
            Emit(diags, "PG020", "EXCEPTION WHEN OTHERS swallows every error (incl. cancellation/out-of-memory) and hides the real failure — catch specific SQLSTATEs, or re-RAISE.", sig);
        if (HasUnstrictSelectInto(body))
            Emit(diags, "PG021", "SELECT ... INTO without STRICT silently leaves the target unset on no row and takes an arbitrary row on many — use INTO STRICT (or check FOUND/ROW_COUNT).", sig);
    }

    /// <summary>
    /// True when the PL/pgSQL body has a <c>SELECT ... INTO target</c> that is not <c>INTO STRICT</c>.
    /// Examined per <c>;</c>-delimited segment so an unrelated <c>INSERT INTO</c> in another statement
    /// doesn't poison the match: a segment counts only if it is a SELECT (not <c>INSERT/MERGE ... INTO</c>),
    /// and the INTO target is a variable list — not <c>STRICT</c>, and not the table-creating
    /// <c>SELECT ... INTO [TEMP|UNLOGGED|TABLE] name</c> form. <paramref name="body"/> is already lower-cased.
    /// </summary>
    private static bool HasUnstrictSelectInto(string body)
    {
        foreach (var seg in body.Split(';'))
        {
            if (!Regex.IsMatch(seg, @"\bselect\b")) continue;
            if (Regex.IsMatch(seg, @"\b(insert|merge)\s+into\b")) continue;
            if (Regex.IsMatch(seg, @"\binto\s+(?!strict\b|temp\b|temporary\b|unlogged\b|table\b)\S"))
                return true;
        }
        return false;
    }

    // numeric/decimal with no precision/scale (optionally an array), case-insensitive: "numeric", "decimal",
    // "numeric []" — but NOT "numeric(10,2)". Such a column stores arbitrary precision, almost always a mistake.
    private static readonly Regex BareNumeric = new(@"^(numeric|decimal)\s*(\[\s*\])?$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // PG010 — char / character (optionally (n), optionally an array), but NOT "character varying" and NOT
    // the internal one-byte "char" (its type text keeps the quotes, so the anchor never matches it).
    private static readonly Regex BlankPaddedChar = new(@"^char(acter)?\s*(\(\s*\d+\s*\))?\s*(\[\s*\])?$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // PG011 — timestamp / timestamp(p), bare or with an explicit WITHOUT TIME ZONE; "with time zone" never matches.
    private static readonly Regex TimestampNoTz = new(@"^timestamp\s*(\(\s*\d+\s*\))?\s*(without\s+time\s+zone)?\s*(\[\s*\])?$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // PG012 — the serial pseudo-types (all six spellings).
    private static readonly Regex SerialType = new(@"^(smallserial|bigserial|serial[248]?)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // PG013 — money (optionally an array).
    private static readonly Regex MoneyType = new(@"^money\s*(\[\s*\])?$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // PG017 — json (optionally an array), but NOT jsonb. The text type keeps raw bytes (no canonicalization,
    // no dedup of keys, no operators/indexing), so jsonb is almost always wanted.
    private static readonly Regex JsonType = new(@"^json\s*(\[\s*\])?$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private void AnalyzeTable(CreateTableStatement t, List<Diagnostic> diags)
    {
        var target = Q(t.Schema, t.Name);

        // PG006 — a real table with no PRIMARY KEY. Skip the forms whose key comes from elsewhere: PARTITION
        // OF / OF type (no column list of their own) and LIKE-element tables (the source may add the PK).
        if (!t.IsPartitionOrTyped && !t.HasLikeElement && !HasPrimaryKey(t))
            Emit(diags, "PG006",
                "Table has no PRIMARY KEY — rows can't be uniquely identified (affects updates, logical replication, and tooling).",
                target);

        // Identifier hygiene on the table name itself (PG015 casing, PG016 length).
        CheckIdentifier(t.Name, "Table", target, diags);

        // Per-column type lints. A column has exactly one type, so the checks are mutually exclusive.
        foreach (var c in t.Columns)
        {
            CheckIdentifier(c.Name, "Column", target, diags);
            var type = (c.Type.Text ?? "").Trim();
            if (BareNumeric.IsMatch(type))
                Emit(diags, "PG008",
                    $"Column \"{c.Name}\" is {type} without precision/scale — specify numeric(p, s) (or a fixed-width type).",
                    target);
            else if (BlankPaddedChar.IsMatch(type))
                Emit(diags, "PG010",
                    $"Column \"{c.Name}\" is {type} — blank-padded char wastes space and surprises on comparison; use text or varchar.",
                    target);
            else if (TimestampNoTz.IsMatch(type))
                Emit(diags, "PG011",
                    $"Column \"{c.Name}\" is {type} — timestamp WITHOUT time zone loses the UTC instant; use timestamptz.",
                    target);
            else if (SerialType.IsMatch(type))
                Emit(diags, "PG012",
                    $"Column \"{c.Name}\" is {type} — prefer GENERATED ALWAYS AS IDENTITY (owned, ALTERable, no implicit sequence grants).",
                    target);
            else if (MoneyType.IsMatch(type))
                Emit(diags, "PG013",
                    $"Column \"{c.Name}\" is {type} — money is locale-dependent (lc_monetary) and loses precision on division; use numeric.",
                    target);
            else if (JsonType.IsMatch(type))
                Emit(diags, "PG017",
                    $"Column \"{c.Name}\" is {type} — json stores raw text (no key dedup, no operators/GIN indexing); prefer jsonb.",
                    target);
        }
    }

    /// PostgreSQL's NAMEDATALEN is 64, so identifiers are silently truncated to 63 bytes (UTF-8).
    private const int MaxIdentifierBytes = 63;

    /// <summary>
    /// PG015/PG016 identifier hygiene for one name (a table or column). PG015 flags an uppercase letter:
    /// an unquoted mixed-case identifier is folded to lower-case by the server (you wrote <c>Customer</c>,
    /// you get <c>customer</c>), and a quoted one forces double-quotes on every later reference — both are
    /// avoidable by using lower_snake_case. PG016 flags a name PostgreSQL would silently truncate.
    /// </summary>
    private void CheckIdentifier(string? name, string role, string target, List<Diagnostic> diags)
    {
        if (string.IsNullOrEmpty(name)) return;

        if (name.Any(char.IsUpper))
            Emit(diags, "PG015",
                $"{role} identifier \"{name}\" has uppercase letters — unquoted it folds to lower case, quoted it forces quoting everywhere; prefer lower_snake_case.",
                target);

        if (System.Text.Encoding.UTF8.GetByteCount(name) > MaxIdentifierBytes)
            Emit(diags, "PG016",
                $"{role} identifier \"{name}\" exceeds {MaxIdentifierBytes} bytes — PostgreSQL silently truncates it, which can collide with another object.",
                target);
    }

    private static bool HasPrimaryKey(CreateTableStatement t) =>
        t.Constraints.Exists(k => k is PrimaryKeyConstraint)
        || t.Columns.Exists(c => c.Constraints.Exists(k => k is InlinePrimaryKey));

    private void CheckSelectStar(CreateViewStatement v, List<Diagnostic> diags)
    {
        if (Regex.IsMatch(v.BodyText, @"\bselect\b[^;]*\*", RegexOptions.IgnoreCase))
            Emit(diags, "PG007", "SELECT * in a view body is brittle to underlying column changes.", Q(v.Schema, v.Name));

        // PG009 inside view bodies — the realistic home of LIMIT in a declarative schema; a bare
        // top-level SELECT (the only place this fired before #65) almost never occurs in a project.
        if (Regex.IsMatch(v.BodyText, @"\blimit\b", RegexOptions.IgnoreCase)
            && !Regex.IsMatch(v.BodyText, @"\border\s+by\b", RegexOptions.IgnoreCase))
            Emit(diags, "PG009", "LIMIT without ORDER BY returns a non-deterministic subset.", Q(v.Schema, v.Name));
    }

    private void CheckLimit(SelectQuery? q, string target, List<Diagnostic> diags)
    {
        if (q is null) return;
        if (q.Limit is not null && q.OrderBy.Count == 0 && q.SetOp is null)
            Emit(diags, "PG009", "LIMIT without ORDER BY returns a non-deterministic subset.", "query");
    }

    /// <summary>
    /// Records a finding for <paramref name="ruleId"/> unless the config disabled it, applying the
    /// configured severity override (else the rule's default severity). Central choke-point so every
    /// rule honours the config without each call site re-deriving severity.
    /// </summary>
    private void Emit(List<Diagnostic> diags, string ruleId, string message, string target)
    {
        if (!_config.IsEnabled(ruleId)) return;
        var severity = _config.EffectiveSeverity(ruleId, DefaultSeverityOf(ruleId));
        diags.Add(new Diagnostic(ruleId, severity, message, target));
    }

    private static string Q(string? schema, string name) => schema is null ? name : $"{schema}.{name}";
}

/// <summary>Static metadata for an analysis rule: its id, default severity, and a short title (for SARIF rule descriptors).</summary>
public sealed record RuleInfo(string Id, DiagnosticSeverity DefaultSeverity, string Title);

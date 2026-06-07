using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using PgProj.Core.Model;
using PgProj.Core.Syntax;
using Diag = PgProj.Core.Diagnostics.Diagnostic;
using DiagSeverity = PgProj.Core.Analysis.DiagnosticSeverity;
using RelatedLocation = PgProj.Core.Diagnostics.RelatedLocation;

namespace PgProj.Core.Semantics.Binding;

/// <summary>
/// Phase 5 — type-aware semantic validation over the Typed Semantic Model (#47). It CONSUMES the
/// <see cref="Binder"/> / <see cref="BoundView"/> / <see cref="BoundQuery"/> the binder produces — every
/// reference carries a resolved <see cref="SymbolEntry"/> and every expression a <see cref="ResolvedType"/> —
/// and turns the gaps the binder left (an unresolved column on a resolved relation, a function call that
/// matched no overload, a comparison of incompatible types, a trigger whose function does not return
/// <c>trigger</c>) into unified compiler-style <see cref="Diag"/>s with file/line and, where a definition is
/// involved, a <see cref="RelatedLocation"/> pointing at it.
///
/// <para>It is ADDITIVE and CONSERVATIVE, mirroring <see cref="SemanticAnalyzer"/>: it only emits an error
/// it can PROVE against resolved symbols/types — a column is flagged only when its table resolved (so the
/// table exists and we know its full column set), a function only when its name has overloads in a managed
/// schema and every argument type is known, a type mismatch only when both operand types are concrete and
/// known incompatible. Anything unknown / external / unmanaged is left alone, so valid SQL (including the
/// PG18 corpus) is never rejected.</para>
/// </summary>
public sealed class SemanticValidator
{
    private readonly Catalog _catalog;
    private readonly SymbolTable _symbols;
    private readonly Binder _binder;

    // file -> source text, for offset -> (line, col) and for definition related locations.
    private readonly Dictionary<string, string> _sources = new(StringComparer.OrdinalIgnoreCase);
    // symbol key (relation/function FQN, lower) -> its definition source location.
    private readonly Dictionary<string, (string File, int Offset)> _definitions = new(StringComparer.OrdinalIgnoreCase);

    private readonly List<Diag> _diags = new();
    private string _file = "";
    private string _text = "";

    public SemanticValidator(Catalog catalog)
    {
        _catalog = catalog;
        _symbols = catalog.Symbols;
        _binder = new Binder(catalog);
    }

    /// <summary>Register a file's source text + parse so definition related-locations can be resolved across files.</summary>
    public void IndexFile(string file, string text, ParseResult parsed)
    {
        _sources[file] = text;
        foreach (var stmt in parsed.Statements)
            RecordDefinition(stmt, file);
    }

    private void RecordDefinition(SqlStatement stmt, string file)
    {
        void Def(string? schema, string name) =>
            _definitions[$"{schema ?? _catalog.DefaultSchema}.{name}".ToLowerInvariant()] = (file, stmt.Position);

        switch (stmt)
        {
            case CreateTableStatement t: Def(t.Schema, t.Name); break;
            case CreateViewStatement v: Def(v.Schema, v.Name); break;
            case CreateSequenceStatement q: Def(q.Schema, q.Name); break;
            case CreateFunctionStatement f: Def(f.Schema, f.Name); break;   // bare name key (overload-agnostic locator)
        }
    }

    /// <summary>
    /// Validate one file's statements (already indexed via <see cref="IndexFile"/> for cross-file related
    /// locations). Returns the unified diagnostics found, each anchored at the offending statement's source
    /// position. DB-free: it reads only the catalog/symbol table and the bound model.
    /// </summary>
    public IReadOnlyList<Diag> Validate(string file, string text, ParseResult parsed)
    {
        _file = file;
        _text = text;
        if (!_sources.ContainsKey(file)) { _sources[file] = text; foreach (var s in parsed.Statements) RecordDefinition(s, file); }

        int startIndex = _diags.Count;   // so this call returns only the findings it adds (one validator, many files)

        // A RENAME/ALTER mid-file changes names/columns we cannot track — match the base analyzer and skip
        // (relation/column resolution would be unreliable, risking false positives).
        bool hasAlterOrRename = parsed.Statements.OfType<AlterStatement>().Any();

        foreach (var stmt in parsed.Statements)
        {
            switch (stmt)
            {
                case CreateViewStatement v when !hasAlterOrRename:
                    ValidateView(v);
                    break;
                case RawCreateStatement { ObjectKind: "TRIGGER" } trg when !hasAlterOrRename:
                    ValidateTrigger(trg);
                    break;
                case CreateTableStatement t when !hasAlterOrRename:
                    ValidateTableConstraints(t);
                    break;
                case QueryStatement q when !hasAlterOrRename:
                    ValidateQuery(_binder.BindQuery(q.Query), stmt.Position);
                    break;
            }
        }
        return _diags.GetRange(startIndex, _diags.Count - startIndex);
    }

    /// <summary>All diagnostics accumulated across every <see cref="Validate"/> call on this instance.</summary>
    public IReadOnlyList<Diag> All => _diags;

    // ---- VIEW validity -------------------------------------------------------

    /// <summary>
    /// Every column/object a view's body references must resolve against its FROM. Consumes the binder's
    /// <see cref="BoundView"/>: a <see cref="BoundColumnRef"/> that did NOT resolve, but whose qualifier
    /// names a resolved in-scope relation, is a bad column on an existing table — the acceptance error, with
    /// a related location pointing at that table's definition.
    /// </summary>
    private void ValidateView(CreateViewStatement view)
    {
        var bound = _binder.BindView(view);
        if (bound.View is null) return;                 // the view's own schema isn't managed — skip (conservative)
        ValidateQuery(bound.Body, view.Position);
    }

    private void ValidateQuery(BoundQuery query, int anchor)
    {
        foreach (var item in query.SelectItems) ValidateExpr(item, query, anchor);
    }

    // ---- TRIGGER validity ----------------------------------------------------

    /// <summary>
    /// A CREATE TRIGGER's EXECUTE FUNCTION must resolve to a function returning <c>trigger</c> (or
    /// <c>event_trigger</c>), and its ON-table must exist. Both checks are PROVE-first: the function is only
    /// flagged when it resolved AND its return type is known AND it is not a trigger type; the table only
    /// when it is schema-qualified into a managed schema and absent (mirrors <see cref="SemanticAnalyzer"/>).
    /// </summary>
    private void ValidateTrigger(RawCreateStatement trg)
    {
        if (trg.Trigger is not { } td) return;           // not a parsed CREATE TRIGGER with captured detail

        // Target table existence — only when qualified into a managed (non-external, non-system) schema.
        if (td.OnSchema is { } onSchema && td.OnTable is { } onTable
            && _catalog.SchemaManaged(onSchema) && !_catalog.SchemaIsExternal(onSchema)
            && !_catalog.HasRelation(onSchema, onTable))
        {
            Add(trg.Position,
                $"trigger \"{trg.Name}\" refers to table \"{onSchema}.{onTable}\" which does not exist",
                Related($"{onSchema}.{onTable}"));
        }

        // Target function: resolve the overload(s) by name (search_path-aware). Trigger functions take no
        // declared args, so a name match is the right lookup. Only error when the function RESOLVED and its
        // return type is KNOWN and is not a trigger return type — never on an unresolved / unknown-return fn.
        var fnName = td.FunctionName;
        if (fnName is null) return;
        var overloads = td.FunctionSchema is { } fs
            ? _symbols.FunctionOverloads(fs, fnName)
            : _catalog.SearchPath.Schemas.SelectMany(s => _symbols.FunctionOverloads(s, fnName)).Distinct().ToList();
        if (overloads.Count == 0) return;               // function not known here — don't guess (external/extension)

        // Every known overload's return type is concrete and none is a trigger type → certainly wrong.
        var withReturn = overloads.Where(o => o.ReturnType is not null).ToList();
        if (withReturn.Count == 0) return;              // return type wasn't captured — conservative
        if (withReturn.Any(o => IsTriggerReturn(o.ReturnType!))) return;   // a trigger-returning overload exists → fine

        var rt = withReturn[0].ReturnType!;
        Add(trg.Position,
            $"function \"{QualFn(td.FunctionSchema, fnName)}\" must return type trigger (returns \"{rt}\")",
            Related(withReturn[0].Fqn));
    }

    private static bool IsTriggerReturn(string returnType) =>
        returnType.Equals("trigger", StringComparison.OrdinalIgnoreCase)
        || returnType.Equals("event_trigger", StringComparison.OrdinalIgnoreCase)
        || returnType.Equals("pg_catalog.trigger", StringComparison.OrdinalIgnoreCase);

    // ---- CONSTRAINT validity (CHECK / DEFAULT) -------------------------------

    /// <summary>
    /// CHECK / DEFAULT expressions on a CREATE TABLE must reference columns that exist on that very table
    /// (the one case where the full column set is always known) and use type-correct comparison operators.
    /// The columns of the table being created form the binding scope; an unqualified ref to a name not in
    /// that set is a bad column, and a comparison of two concrete incompatible types is a type error.
    /// </summary>
    private void ValidateTableConstraints(CreateTableStatement t)
    {
        if (t.IsPartitionOrTyped || t.HasLikeElement) return;   // unknown / inherited columns → don't guess
        if (t.TrailingText is { } tr && tr.Contains("inherits", StringComparison.OrdinalIgnoreCase)) return;

        // The synthetic scope: this table's own columns, with their declared types.
        var cols = t.Columns.Select(c => new BoundResultColumn
        {
            Name = c.Name,
            Type = ResolveType(TypeNormalizer.Normalize(c.Type.Text)),
        }).ToList();
        var tableName = t.Name;
        var scope = new ConstraintScope(tableName, cols);

        foreach (var col in t.Columns)
            foreach (var cc in col.Constraints)
            {
                switch (cc)
                {
                    case InlineCheck chk: ValidateConstraintExpr(chk.Expression, scope, t.Position); break;
                    case DefaultConstraint def: ValidateConstraintExpr(def.Expression, scope, t.Position); break;
                    case GeneratedStored gen: ValidateConstraintExpr(gen.Expression, scope, t.Position); break;
                }
            }
        foreach (var tc in t.Constraints)
            if (tc is CheckConstraint chk) ValidateConstraintExpr(chk.Expression, scope, t.Position);
    }

    private void ValidateConstraintExpr(string exprText, ConstraintScope scope, int anchor)
    {
        var expr = ParseExpr(exprText);
        if (expr is null) return;                       // unparseable fragment — never invent an error
        var bound = BindConstraintExpr(expr, scope);
        ValidateBoundConstraint(bound, scope, anchor);
    }

    // ---- expression validation (type safety + overload resolution) -----------

    private void ValidateExpr(BoundExpr e, BoundQuery scope, int anchor)
    {
        switch (e)
        {
            case BoundColumnRef cr:
                ValidateColumnRef(cr, scope, anchor);
                break;
            case BoundFuncCall fc:
                ValidateFuncCall(fc, anchor);
                foreach (var a in fc.Args) ValidateExpr(a, scope, anchor);
                break;
            case BoundBinary b:
                ValidateComparison(b, anchor);
                ValidateExpr(b.Left, scope, anchor);
                ValidateExpr(b.Right, scope, anchor);
                break;
            case BoundCast c:
                ValidateExpr(c.Operand, scope, anchor);
                break;
            case BoundExpression x:
                foreach (var k in x.Children) ValidateExpr(k, scope, anchor);
                break;
        }
    }

    /// <summary>
    /// An unresolved column whose qualifier names a RESOLVED in-scope relation is a bad column on an existing
    /// table — emit it with a related location at the table's definition. Unqualified / qualifier-unknown
    /// refs are left alone (they may bind to an outer scope, a function-in-FROM, an unmanaged relation).
    /// </summary>
    private void ValidateColumnRef(BoundColumnRef cr, BoundQuery scope, int anchor)
    {
        if (cr.IsResolved) return;
        if (cr.Parts.Count == 0) return;
        string colName = cr.Parts[^1];
        string? qualifier = cr.Parts.Count >= 2 ? cr.Parts[^2] : null;

        // Find the in-scope source the qualifier names (or the sole source for a bare column).
        BoundRangeVar? src = qualifier is not null
            ? scope.Sources.FirstOrDefault(s => s.Name.Equals(qualifier, StringComparison.OrdinalIgnoreCase)
                                             || s.Symbol?.Name.Equals(qualifier, StringComparison.OrdinalIgnoreCase) == true)
            : scope.Sources.Count == 1 ? scope.Sources[0] : null;

        // Prove the error: the source must be a RESOLVED relation (so we know its full column set), it must
        // have a known column list, and the column must genuinely be absent from it.
        if (src is null || src.Symbol is null) return;          // qualifier not an in-scope managed relation → skip
        if (src.Symbol.IsExternal) return;                      // external relation — its columns aren't authoritative here
        if (src.Columns.Count == 0) return;                     // columns unknown → don't guess
        if (src.Columns.Any(c => c.Name.Equals(colName, StringComparison.OrdinalIgnoreCase))) return;

        Add(anchor,
            $"column \"{colName}\" does not exist on relation \"{src.Symbol.Fqn}\"",
            Related(src.Symbol.Fqn));
    }

    /// <summary>
    /// Surface the binder's overload resolution gap: a call that did NOT resolve, but whose name HAS
    /// overloads in a managed schema and whose argument types are ALL known, matched no overload (or is
    /// ambiguous). Conservative: an unknown arg type, a built-in name, or a name with no managed overloads
    /// is skipped (the binder + <see cref="SemanticAnalyzer"/> arity checks already cover built-ins).
    /// </summary>
    private void ValidateFuncCall(BoundFuncCall fc, int anchor)
    {
        if (fc.IsResolved) return;
        if (fc.Name.Count == 0) return;
        string name = fc.Name[^1];
        string? schema = fc.Name.Count >= 2 ? fc.Name[^2] : null;

        // External / system schema or built-in: leave alone.
        if (schema is not null && (!_catalog.SchemaManaged(schema) || _catalog.SchemaIsExternal(schema))) return;

        var overloads = schema is not null
            ? _symbols.FunctionOverloads(schema, name)
            : _catalog.SearchPath.Schemas
                .Where(s => !_catalog.SchemaIsExternal(s))
                .SelectMany(s => _symbols.FunctionOverloads(s, name)).Distinct().ToList();
        if (overloads.Count == 0) return;               // no managed overload of this name → built-in / external

        // Only error when every argument type is known (else the no-match could be our inference gap).
        if (fc.Args.Any(a => a.Type.IsUnknown)) return;
        var argSig = string.Join(",", fc.Args.Select(a => a.Type.Name));

        // A single overload that the binder declined to bind means the inferred signature didn't match it.
        // With multiple overloads and no exact match, it is a genuine no-match (the binder only auto-binds a
        // sole overload). Either way: no overload accepts these argument types.
        Add(anchor,
            $"no function matches \"{QualFn(schema, name)}({argSig})\" — candidate signature{(overloads.Count == 1 ? "" : "s")}: "
                + string.Join(", ", overloads.Select(o => $"({o.Signature?.ArgTypes})")),
            overloads.Select(o => Related1(o.Fqn)).Where(r => r is not null).Select(r => r!).ToArray());
    }

    /// <summary>
    /// A comparison whose two operands are both concrete, known, and from incompatible type families is a
    /// type error (e.g. integer = text). Only fires when both sides are non-unknown built-in scalar types in
    /// different families — never on an unknown side, a user-defined type, or same-family operands.
    /// </summary>
    private void ValidateComparison(BoundBinary b, int anchor)
    {
        if (!IsComparison(b.Op)) return;
        var lt = b.Left.Type;
        var rt = b.Right.Type;
        if (lt.IsUnknown || rt.IsUnknown) return;
        if (lt.Symbol is not null || rt.Symbol is not null) return;     // user-defined type/domain — could have an operator/cast
        var lf = Family(lt.Name);
        var rf = Family(rt.Name);
        if (lf == TypeFamily.Unknown || rf == TypeFamily.Unknown) return;
        if (lf == rf) return;
        // string<->number and boolean<->anything are the unambiguous compile-time mismatches PG rejects
        // without an explicit cast; date/time families are intentionally left out (implicit casts vary).
        if (!IncompatibleFamilies(lf, rf)) return;

        Add(anchor, $"operator does not exist: {lt.Name} {b.Op} {rt.Name}");
    }

    // ---- constraint-scope binding (a CREATE TABLE's own columns) --------------

    private sealed class ConstraintScope
    {
        public string Table { get; }
        public IReadOnlyList<BoundResultColumn> Columns { get; }
        public ConstraintScope(string table, IReadOnlyList<BoundResultColumn> columns) { Table = table; Columns = columns; }

        public BoundResultColumn? Find(string name) =>
            Columns.FirstOrDefault(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
    }

    private BoundExpr BindConstraintExpr(Expr e, ConstraintScope scope)
    {
        switch (e)
        {
            case LiteralExpr lit:
                return new BoundLiteral { Syntax = lit, LiteralKind = lit.Kind, Text = lit.Text, Type = LiteralType(lit) };
            case ColumnRef col:
            {
                var parts = col.NameParts;
                if (parts.Count == 0) return new BoundColumnRef { Syntax = col, Parts = parts };
                var hit = scope.Find(parts[^1]);
                return new BoundColumnRef { Syntax = col, Parts = parts, Type = hit?.Type ?? ResolvedType.Unknown };
            }
            case CastExpr c:
                return new BoundCast { Syntax = c, Operand = BindConstraintExpr(c.Operand, scope), TargetTypeText = c.TypeText, Type = ResolveType(TypeNormalizer.Normalize(c.TypeText)) };
            case BinaryExpr b:
            {
                var l = BindConstraintExpr(b.Left, scope);
                var r = BindConstraintExpr(b.Right, scope);
                return new BoundBinary { Syntax = b, Op = b.Op, Left = l, Right = r, Type = IsComparison(b.Op) ? ResolvedType.Boolean : ResolvedType.Unknown };
            }
            case UnaryExpr u:
            {
                var inner = BindConstraintExpr(u.Operand, scope);
                return new BoundExpression { Syntax = u, Children = new[] { inner }, Type = inner.Type };
            }
            default:
            {
                var kids = ConstraintChildren(e).Select(k => BindConstraintExpr(k, scope)).ToList();
                return new BoundExpression { Syntax = e, Children = kids };
            }
        }
    }

    private void ValidateBoundConstraint(BoundExpr e, ConstraintScope scope, int anchor)
    {
        switch (e)
        {
            case BoundColumnRef cr when cr.Parts.Count > 0:
            {
                string colName = cr.Parts[^1];
                // a qualified ref must qualify THIS table; if it qualifies another we can't be sure → skip
                if (cr.Parts.Count >= 2 && !cr.Parts[^2].Equals(scope.Table, StringComparison.OrdinalIgnoreCase)) break;
                if (scope.Find(colName) is null)
                    Add(anchor, $"column \"{colName}\" referenced in check/default does not exist on relation \"{scope.Table}\"");
                break;
            }
            case BoundBinary b:
                ValidateComparison(b, anchor);
                ValidateBoundConstraint(b.Left, scope, anchor);
                ValidateBoundConstraint(b.Right, scope, anchor);
                break;
            case BoundCast c:
                ValidateBoundConstraint(c.Operand, scope, anchor);
                break;
            case BoundExpression x:
                foreach (var k in x.Children) ValidateBoundConstraint(k, scope, anchor);
                break;
        }
    }

    private static IEnumerable<Expr> ConstraintChildren(Expr e) => e switch
    {
        PostfixExpr p => new[] { p.Operand },
        CollateExpr cl => new[] { cl.Operand },
        FuncCallExpr f => f.Args,
        CaseExpr cs => (cs.Operand is null ? Enumerable.Empty<Expr>() : new[] { cs.Operand })
            .Concat(cs.Branches.SelectMany(br => new[] { br.When, br.Then }))
            .Concat(cs.Else is null ? Enumerable.Empty<Expr>() : new[] { cs.Else }),
        BetweenExpr bt => new[] { bt.Operand, bt.Low, bt.High },
        InExpr inx => new[] { inx.Operand }.Concat(inx.List ?? Enumerable.Empty<Expr>()),
        IsCheckExpr isc => isc.Other is null ? new[] { isc.Operand } : new[] { isc.Operand, isc.Other },
        PatternMatchExpr pm => new[] { pm.Operand, pm.Pattern },
        RowExpr r => r.Items,
        ArrayExpr ar => ar.Elements,
        _ => Enumerable.Empty<Expr>(),
    };

    // ---- type helpers --------------------------------------------------------

    private ResolvedType ResolveType(string? normalized)
    {
        if (string.IsNullOrEmpty(normalized)) return ResolvedType.Unknown;
        var baseName = BaseTypeName(normalized!);
        var sym = _symbols.ResolveUnqualified(baseName, SymbolKind.Type, _catalog.SearchPath);
        return sym is not null ? ResolvedType.OfSymbol(sym) : ResolvedType.Of(normalized!);
    }

    private static ResolvedType LiteralType(LiteralExpr lit) => lit.Kind switch
    {
        "number" => lit.Text.Contains('.') || lit.Text.Contains('e') || lit.Text.Contains('E') ? ResolvedType.Numeric : ResolvedType.Bigint,
        "string" => ResolvedType.Text,
        "bool" => ResolvedType.Boolean,
        _ => ResolvedType.Unknown,
    };

    private static bool IsComparison(string op) =>
        op is "=" or "<>" or "!=" or "<" or ">" or "<=" or ">=";

    private enum TypeFamily { Unknown, Number, String, Boolean }

    private static TypeFamily Family(string typeName)
    {
        var b = BaseTypeName(typeName).ToLowerInvariant();
        return b switch
        {
            "smallint" or "integer" or "bigint" or "numeric" or "decimal" or "real"
                or "double precision" or "float" or "money" => TypeFamily.Number,
            "text" or "character varying" or "character" or "char" or "varchar" or "bpchar" or "name" or "citext" => TypeFamily.String,
            "boolean" or "bool" => TypeFamily.Boolean,
            _ => TypeFamily.Unknown,
        };
    }

    // Families PG will not implicitly compare without a cast. number<->string and boolean<->{number,string}.
    private static bool IncompatibleFamilies(TypeFamily a, TypeFamily b) =>
        (a == TypeFamily.Number && b == TypeFamily.String) || (a == TypeFamily.String && b == TypeFamily.Number)
        || (a == TypeFamily.Boolean && b != TypeFamily.Boolean) || (b == TypeFamily.Boolean && a != TypeFamily.Boolean);

    private static string BaseTypeName(string normalized)
    {
        var s = normalized;
        var paren = s.IndexOf('(');
        if (paren >= 0) s = s[..paren];
        while (s.EndsWith("[]", StringComparison.Ordinal)) s = s[..^2];
        return s.Trim();
    }

    private static string QualFn(string? schema, string name) => schema is null ? name : $"{schema}.{name}";

    // ---- diagnostics emission -------------------------------------------------

    private void Add(int offset, string message, params RelatedLocation[] related)
    {
        var (line, col) = OffsetToLineColumn(_text, offset);
        _diags.Add(new Diag
        {
            Severity = DiagSeverity.Error,
            Code = "SEM",
            Message = message,
            File = _file,
            Line = line,
            Column = col,
            Related = related is { Length: > 0 } ? related : Array.Empty<RelatedLocation>(),
        });
    }

    /// <summary>A related location at the definition of the relation/function with FQN <paramref name="fqn"/>,
    /// or empty when its source position isn't known (cross-file/unindexed) — never a bogus location.</summary>
    private RelatedLocation[] Related(string fqn)
    {
        var r = Related1(fqn);
        return r is null ? Array.Empty<RelatedLocation>() : new[] { r };
    }

    private RelatedLocation? Related1(string fqn)
    {
        if (!_definitions.TryGetValue(fqn.ToLowerInvariant(), out var d)) return null;
        var text = _sources.TryGetValue(d.File, out var t) ? t : _text;
        var (line, col) = OffsetToLineColumn(text, d.Offset);
        return new RelatedLocation(d.File, line, col, "defined here");
    }

    private static (int Line, int Column) OffsetToLineColumn(string text, int offset)
    {
        int line = 1, col = 1;
        int limit = Math.Min(offset, text.Length);
        for (int i = 0; i < limit; i++)
        {
            if (text[i] == '\n') { line++; col = 1; }
            else col++;
        }
        return (line, col);
    }

    private static Expr? ParseExpr(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        try
        {
            // Wrap the fragment in a trivial SELECT so the standalone-query path parses it as one expression.
            var parsed = new PgParser().Parse($"SELECT {text};");
            if (parsed.Diagnostics.Count != 0) return null;
            var q = parsed.Statements.OfType<QueryStatement>().FirstOrDefault()?.Query;
            var items = q?.Items;
            return items is { Count: 1 } ? items[0].Expr : null;
        }
        catch { return null; }
    }
}

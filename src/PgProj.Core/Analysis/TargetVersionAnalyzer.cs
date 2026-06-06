using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using PgProj.Core.Project;
using PgProj.Core.Syntax;

namespace PgProj.Core.Analysis;

/// <summary>
/// Target-platform enforcement (EP-TARGET): flags syntax newer than the project's
/// <c>TargetPostgresVersion</c>. It walks the PgParser AST (and, for view/function bodies that the
/// parser keeps verbatim, their captured source text) and reports a <c>PGV###</c> diagnostic for every
/// construct whose minimum PostgreSQL major version — per <see cref="PgVersionCapabilities"/> — exceeds
/// the target. With no target set the analyzer is a no-op (returns nothing), so default behavior is
/// unchanged. This is a SEPARATE analyzer from <see cref="PgAnalyzer"/> (EP-ANALYSIS+ owns that file);
/// the build/validate gate runs both.
/// </summary>
public sealed class TargetVersionAnalyzer
{
    /// <summary>Number of version-gating rules (for the CLI banner alongside <c>PgAnalyzer.RuleCount</c>).</summary>
    public static int RuleCount => PgVersionCapabilities.RuleCount;

    private readonly int _targetMajor;
    private readonly string _file;
    private readonly string? _text;

    private TargetVersionAnalyzer(int targetMajor, string file, string? text)
    {
        _targetMajor = targetMajor;
        _file = file;
        _text = text;
    }

    /// <summary>
    /// Analyze one already-parsed file against a target version. <paramref name="targetVersion"/> is the
    /// project's <c>TargetPostgresVersion</c> (e.g. "16", "17", "PostgreSQL 18"); when null/blank/unparseable
    /// the gate is disabled and no findings are produced. <paramref name="sourceText"/> is the file's raw SQL,
    /// used to resolve each finding's line:column and to scan verbatim view/function bodies.
    /// </summary>
    public static IReadOnlyList<Diagnostic> Analyze(
        ParseResult result, string? targetVersion, string fileName = "", string? sourceText = null)
    {
        var major = ParseMajorVersion(targetVersion);
        if (major is null) return System.Array.Empty<Diagnostic>();
        return new TargetVersionAnalyzer(major.Value, fileName, sourceText).Run(result);
    }

    /// <summary>
    /// Runs the version gate over an entire project: parses every included .sql file and reports findings
    /// for syntax newer than the project's <c>TargetPostgresVersion</c>, anchored to <c>file:line:col</c>
    /// (project-relative). When the project has no target set the result is empty (gate disabled). This is
    /// the same pass the CLI build/validate gate uses, exposed at Core level so it is independently testable
    /// and reusable by an editor/host. Unreadable files are skipped (they surface as build diagnostics).
    /// </summary>
    public static IReadOnlyList<Diagnostic> AnalyzeProject(DatabaseProject project)
    {
        if (ParseMajorVersion(project.TargetPostgresVersion) is null)
            return System.Array.Empty<Diagnostic>();

        var findings = new List<Diagnostic>();
        foreach (var file in project.ResolveSqlFiles())
        {
            string text;
            try { text = File.ReadAllText(file); }
            catch { continue; }
            var rel = Path.GetRelativePath(project.ProjectDirectory, file).Replace('\\', '/');
            var parsed = new PgParser().Parse(text);
            findings.AddRange(Analyze(parsed, project.TargetPostgresVersion, rel, text));
        }
        return findings;
    }

    /// <summary>True when the project targets a version that some used syntax exceeds (the gate's verdict).</summary>
    public static bool ProjectExceedsTarget(DatabaseProject project) => AnalyzeProject(project).Count > 0;

    /// <summary>Extracts the leading PostgreSQL major version from a target string, or null if none/unset.</summary>
    public static int? ParseMajorVersion(string? targetVersion)
    {
        if (string.IsNullOrWhiteSpace(targetVersion)) return null;
        var m = Regex.Match(targetVersion, @"\d+");
        return m.Success && int.TryParse(m.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) && v > 0
            ? v
            : null;
    }

    private List<Diagnostic> Run(ParseResult result)
    {
        var diags = new List<Diagnostic>();
        foreach (var stmt in result.Statements)
            Visit(stmt, diags);
        return diags;
    }

    // ---- statement dispatch --------------------------------------------------------------------

    private void Visit(SqlStatement stmt, List<Diagnostic> diags)
    {
        switch (stmt)
        {
            case MergeStatement merge:
                VisitMerge(merge, diags);
                break;
            case CreateTableStatement table:
                VisitTable(table, diags);
                break;
            case CreateViewStatement view:
                // The parser keeps a view body verbatim (no expression tree), so scan its text.
                ScanText(view.BodyText, view.Position, diags);
                break;
            case CreateFunctionStatement fn:
                // Likewise function bodies are verbatim; scan the routine body text.
                ScanFunctionBody(fn, diags);
                break;
            case QueryStatement q:
                VisitQuery(q.Query, q.Position, diags);
                break;
            case InsertStatement ins:
                VisitDml(ins, diags);
                if (ins.Source is not null) VisitQuery(ins.Source, ins.Position, diags);
                break;
            case UpdateStatement upd:
                VisitDml(upd, diags);
                WalkSetClauses(upd.Set, upd.Position, diags);
                if (upd.Where is not null) WalkExpr(upd.Where, upd.Position, diags);
                if (upd.From is not null) VisitFrom(upd.From, upd.Position, diags);
                break;
            case DeleteStatement del:
                VisitDml(del, diags);
                if (del.Where is not null) WalkExpr(del.Where, del.Position, diags);
                if (del.Using is not null) VisitFrom(del.Using, del.Position, diags);
                break;
            case CreateTableAsStatement cta when cta.Source is not null:
                VisitQuery(cta.Source, cta.Position, diags);
                break;
        }
    }

    private void VisitMerge(MergeStatement merge, List<Diagnostic> diags)
    {
        Flag(PgVersionCapabilities.MergeStatement, merge.Position, diags);

        if (merge.ReturningStar || merge.Returning.Count > 0)
            Flag(PgVersionCapabilities.MergeReturning, merge.Position, diags);

        foreach (var when in merge.Whens)
        {
            if (when.By is not null)
            {
                Flag(PgVersionCapabilities.MergeByGuard, merge.Position, diags);
                break;
            }
        }

        // MERGE also carries CTEs, an ON condition, and per-branch expressions worth walking.
        foreach (var cte in merge.With) VisitQuery(cte.Query, merge.Position, diags);
        if (merge.On is not null) WalkExpr(merge.On, merge.Position, diags);
        foreach (var when in merge.Whens)
        {
            if (when.And is not null) WalkExpr(when.And, merge.Position, diags);
            WalkSetClauses(when.Set, merge.Position, diags);
            foreach (var v in when.InsertValues) WalkExpr(v, merge.Position, diags);
        }
        foreach (var item in merge.Returning) WalkExpr(item.Expr, merge.Position, diags);
    }

    private void VisitTable(CreateTableStatement table, List<Diagnostic> diags)
    {
        foreach (var col in table.Columns)
            foreach (var con in col.Constraints)
                if (con is InlineUnique { NullsNotDistinct: true })
                    Flag(PgVersionCapabilities.NullsNotDistinct, table.Position, diags);

        foreach (var con in table.Constraints)
            if (con is UniqueConstraint { NullsNotDistinct: true })
                Flag(PgVersionCapabilities.NullsNotDistinct, table.Position, diags);
    }

    private void VisitDml(DmlStatement dml, List<Diagnostic> diags)
    {
        foreach (var cte in dml.With) VisitQuery(cte.Query, dml.Position, diags);
        foreach (var item in dml.Returning) WalkExpr(item.Expr, dml.Position, diags);
    }

    // ---- query / from walking ------------------------------------------------------------------

    private void VisitQuery(SelectQuery? q, int pos, List<Diagnostic> diags)
    {
        if (q is null) return;

        foreach (var cte in q.With) VisitQuery(cte.Query, pos, diags);
        foreach (var item in q.Items) WalkExpr(item.Expr, pos, diags);
        foreach (var e in q.DistinctOn) WalkExpr(e, pos, diags);
        if (q.From is not null) VisitFrom(q.From, pos, diags);
        if (q.Where is not null) WalkExpr(q.Where, pos, diags);
        foreach (var e in q.GroupBy) WalkExpr(e, pos, diags);
        if (q.Having is not null) WalkExpr(q.Having, pos, diags);
        foreach (var row in q.ValuesRows) foreach (var e in row) WalkExpr(e, pos, diags);
        foreach (var ob in q.OrderBy) WalkExpr(ob.Expr, pos, diags);

        if (q.SetOp is not null)
        {
            VisitQuery(q.SetOp.Left, pos, diags);
            VisitQuery(q.SetOp.Right, pos, diags);
        }
    }

    private void VisitFrom(FromClause from, int pos, List<Diagnostic> diags)
    {
        foreach (var rel in from.Relations) VisitTableRef(rel, pos, diags);
    }

    private void VisitTableRef(TableRef rel, int pos, List<Diagnostic> diags)
    {
        // JSON_TABLE / XMLTABLE arrive as a function-in-FROM whose name the parser preserves.
        if (rel.Function is not null) WalkExpr(rel.Function, pos, diags);
        if (rel.Subquery is not null) VisitQuery(rel.Subquery, pos, diags);
        foreach (var j in rel.Joins)
        {
            VisitTableRef(j.Right, pos, diags);
            if (j.On is not null) WalkExpr(j.On, pos, diags);
        }
    }

    private void WalkSetClauses(IEnumerable<SetClause> sets, int pos, List<Diagnostic> diags)
    {
        foreach (var s in sets)
        {
            if (s.Value is not null) WalkExpr(s.Value, pos, diags);
            foreach (var v in s.Values) WalkExpr(v, pos, diags);
            if (s.SubSelect is not null) VisitQuery(s.SubSelect, pos, diags);
        }
    }

    // ---- expression walking --------------------------------------------------------------------

    private void WalkExpr(Expr? e, int pos, List<Diagnostic> diags)
    {
        switch (e)
        {
            case null:
                return;

            case FuncCallExpr fc:
                FlagFunction(fc, pos, diags);
                foreach (var a in fc.Args) WalkExpr(a, pos, diags);
                if (fc.Filter is not null) WalkExpr(fc.Filter, pos, diags);
                break;

            case IsCheckExpr ic:
                if (ic.What.Equals("JSON", System.StringComparison.OrdinalIgnoreCase))
                    Flag(PgVersionCapabilities.IsJsonPredicate, pos, diags);
                WalkExpr(ic.Operand, pos, diags);
                WalkExpr(ic.Other, pos, diags);
                break;

            case UnaryExpr u: WalkExpr(u.Operand, pos, diags); break;
            case PostfixExpr p: WalkExpr(p.Operand, pos, diags); break;
            case BinaryExpr b: WalkExpr(b.Left, pos, diags); WalkExpr(b.Right, pos, diags); break;
            case CastExpr cst: WalkExpr(cst.Operand, pos, diags); break;
            case CollateExpr cl: WalkExpr(cl.Operand, pos, diags); break;
            case SubscriptExpr sub: WalkExpr(sub.Operand, pos, diags); break;
            case FieldAccessExpr fa: WalkExpr(fa.Operand, pos, diags); break;
            case RowExpr r: foreach (var i in r.Items) WalkExpr(i, pos, diags); break;
            case ArrayExpr arr:
                foreach (var i in arr.Elements) WalkExpr(i, pos, diags);
                if (arr.Subquery is not null) VisitQuery(arr.Subquery, pos, diags);
                break;
            case SubqueryExpr sq: VisitQuery(sq.Query, pos, diags); break;
            case ExistsExpr ex: VisitQuery(ex.Query, pos, diags); break;
            case CaseExpr ce:
                WalkExpr(ce.Operand, pos, diags);
                foreach (var (w, t) in ce.Branches) { WalkExpr(w, pos, diags); WalkExpr(t, pos, diags); }
                WalkExpr(ce.Else, pos, diags);
                break;
            case BetweenExpr bt:
                WalkExpr(bt.Operand, pos, diags); WalkExpr(bt.Low, pos, diags); WalkExpr(bt.High, pos, diags);
                break;
            case InExpr ie:
                WalkExpr(ie.Operand, pos, diags);
                if (ie.List is not null) foreach (var i in ie.List) WalkExpr(i, pos, diags);
                if (ie.Subquery is not null) VisitQuery(ie.Subquery, pos, diags);
                break;
            case QuantifiedExpr qe:
                WalkExpr(qe.Left, pos, diags); WalkExpr(qe.Array, pos, diags);
                if (qe.Subquery is not null) VisitQuery(qe.Subquery, pos, diags);
                break;
            case PatternMatchExpr pm:
                WalkExpr(pm.Operand, pos, diags); WalkExpr(pm.Pattern, pos, diags); WalkExpr(pm.Escape, pos, diags);
                break;
        }
    }

    private void FlagFunction(FuncCallExpr fc, int pos, List<Diagnostic> diags)
    {
        if (fc.Name.Count == 0) return;
        var name = fc.Name[fc.Name.Count - 1].ToLowerInvariant();
        switch (name)
        {
            case "json_table":
                Flag(PgVersionCapabilities.JsonTable, pos, diags);
                break;
            case "json_query":
            case "json_value":
            case "json_exists":
                Flag(PgVersionCapabilities.JsonQueryFunctions, pos, diags);
                break;
            case "json":
            case "json_scalar":
            case "json_serialize":
                Flag(PgVersionCapabilities.JsonConstructors, pos, diags);
                break;
        }
    }

    // ---- verbatim-body text scan (views + function bodies) -------------------------------------

    private void ScanFunctionBody(CreateFunctionStatement fn, List<Diagnostic> diags)
    {
        // Restrict to the dollar-quoted / string body so a function NAMED like a feature keyword in its
        // header doesn't trip the gate; mirrors PgAnalyzer's body-vs-header split.
        var src = fn.SourceText ?? "";
        var bm = Regex.Match(src, @"\$(\w*)\$(.*)\$\1\$", RegexOptions.Singleline);
        var body = bm.Success ? bm.Groups[2].Value : (fn.Body ?? "");
        ScanText(body, fn.Position, diags);
    }

    private void ScanText(string? text, int pos, List<Diagnostic> diags)
    {
        if (string.IsNullOrEmpty(text)) return;

        if (Regex.IsMatch(text, @"\bjson_table\s*\(", RegexOptions.IgnoreCase))
            Flag(PgVersionCapabilities.JsonTable, pos, diags);
        if (Regex.IsMatch(text, @"\bjson_(query|value|exists)\s*\(", RegexOptions.IgnoreCase))
            Flag(PgVersionCapabilities.JsonQueryFunctions, pos, diags);
        if (Regex.IsMatch(text, @"\b(json_scalar|json_serialize)\s*\(", RegexOptions.IgnoreCase))
            Flag(PgVersionCapabilities.JsonConstructors, pos, diags);
        // IS [NOT] JSON predicate (the scalar JSON() constructor is rarer in bodies and shares the
        // word with the type name, so we anchor on the predicate form to stay false-positive-free).
        if (Regex.IsMatch(text, @"\bis\s+(not\s+)?json\b", RegexOptions.IgnoreCase))
            Flag(PgVersionCapabilities.IsJsonPredicate, pos, diags);
    }

    // ---- emit ----------------------------------------------------------------------------------

    /// <summary>Emits a finding for a capability when it exceeds the target (deduped per rule+location).</summary>
    private void Flag(string ruleId, int position, List<Diagnostic> diags)
    {
        var cap = PgVersionCapabilities.For(ruleId);
        if (cap.MinMajorVersion <= _targetMajor) return;

        var target = Location(position);
        // De-dupe: the same feature may be reachable via both AST and body-text scan, or appear twice in
        // one statement — one finding per (rule, location) keeps the report clean.
        foreach (var d in diags)
            if (d.RuleId == ruleId && d.Target == target) return;

        diags.Add(new Diagnostic(
            ruleId,
            DiagnosticSeverity.Error,
            $"{cap.Feature} requires PostgreSQL {cap.MinMajorVersion}+, but the project targets PostgreSQL {_targetMajor}. {cap.Detail}",
            target));
    }

    /// <summary>Resolves a statement offset to a <c>file:line:col</c> anchor (falls back to file:offset).</summary>
    private string Location(int position)
    {
        var file = string.IsNullOrEmpty(_file) ? "" : _file;
        if (_text is null)
            return string.IsNullOrEmpty(file) ? $"offset {position}" : $"{file}:{position}";

        var (line, col) = LineCol(_text, position);
        return string.IsNullOrEmpty(file) ? $"{line}:{col}" : $"{file}:{line}:{col}";
    }

    /// <summary>Translate a 0-based character offset into 1-based (line, column).</summary>
    private static (int Line, int Col) LineCol(string text, int offset)
    {
        if (offset < 0) offset = 0;
        if (offset > text.Length) offset = text.Length;
        int line = 1, col = 1;
        for (var i = 0; i < offset; i++)
        {
            if (text[i] == '\n') { line++; col = 1; }
            else col++;
        }
        return (line, col);
    }
}

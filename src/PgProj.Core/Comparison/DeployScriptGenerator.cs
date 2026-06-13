using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using PgProj.Core.Comparison.Risk;
using PgProj.Core.Deployment;

namespace PgProj.Core.Comparison;

/// <summary>A pre/post-deployment script: its display name (for banners/diagnostics) and raw body.</summary>
public sealed record DeployScript(string Name, string Body);

/// <summary>
/// The pre/post-deploy scripts to splice around the schema diff. Bodies are passed through verbatim
/// (only SQLCMD-variable substitution is applied, never reformatting) so dollar-quoted function bodies
/// and embedded semicolons survive untouched.
/// </summary>
public sealed record DeployScriptBundle(DeployScript? Pre = null, DeployScript? Post = null)
{
    public bool IsEmpty => Pre is null && Post is null;
}

public sealed class DeployOptions
{
    /// <summary>Wrap the whole script in BEGIN/COMMIT so a failed step rolls everything back.</summary>
    public bool WrapInTransaction { get; init; } = true;

    /// <summary>Emit a leading comment banner describing the plan (and the resolved variable map).</summary>
    public bool IncludeHeader { get; init; } = true;

    /// <summary>Pre/post-deployment scripts to splice around the schema diff (EP-DEPLOYSCRIPTS).</summary>
    public DeployScriptBundle? Scripts { get; init; }

    /// <summary>
    /// Resolved SQLCMD variables (EP-VARS). When set, <c>$(Name)</c> tokens in the pre/post scripts are
    /// substituted and the resolved map is echoed into the header. Unresolved tokens throw.
    /// </summary>
    public SqlCmdVariableResolver? Variables { get; init; }

    // ---- Phase-18 publish options (issue #58) --------------------------------------------------
    // The block-on-data-loss gate is ENFORCED here (see DeployScriptGenerator.Guard). The remaining
    // options below define the surface only — threading them through script GENERATION is Phase 14 /
    // issue #56's job; they are stored so a profile can round-trip them now. Each default reproduces
    // today's behaviour exactly.

    /// <summary>
    /// Refuse to generate a script when it contains a possible-data-loss change (risk level
    /// <see cref="RiskLevel.DataLoss"/> or higher). Defaults to <c>false</c> (today's behaviour: the script
    /// is always produced). Wired to the Phase-12 risk analyzer (#54).
    /// </summary>
    public bool BlockOnPossibleDataLoss { get; init; }

    /// <summary>Drop objects present in the target but absent from the source. Mirrors the comparer option; off by default.</summary>
    public bool DropObjectsNotInSource { get; init; }

    /// <summary>Drop constraints/indexes present in the target but absent from the source. Off by default.</summary>
    public bool DropConstraintsAndIndexesNotInSource { get; init; }

    /// <summary>Prefer ALTER over drop+recreate when both express the change. On by default (today's behaviour).</summary>
    public bool PreferAlterOverRecreate { get; init; } = true;

    /// <summary>Only recreate an object when an in-place ALTER cannot express the change. On by default.</summary>
    public bool RecreateOnlyWhenNecessary { get; init; } = true;

    /// <summary>Emit idempotent <c>IF [NOT] EXISTS</c> guards where the dialect allows. Off by default.</summary>
    public bool IdempotentIfExists { get; init; }

    // ---- #140 DacDeployOptions-family additions ------------------------------------------------

    /// <summary>
    /// When adding a <c>NOT NULL</c> column with no declared default, synthesize a type-appropriate
    /// <c>DEFAULT</c> so the <c>ADD COLUMN</c> succeeds on a populated table (SqlPackage
    /// <c>GenerateSmartDefaults</c>). Off by default (today's behaviour: the bare <c>ADD COLUMN … NOT NULL</c>
    /// is emitted and fails on a non-empty table).
    /// </summary>
    public bool GenerateSmartDefaults { get; init; }

    /// <summary>
    /// Whether a newly added FK/CHECK constraint is validated against existing rows (SqlPackage
    /// <c>ScriptNewConstraintValidation</c>). <c>true</c> (default) = plain <c>ADD CONSTRAINT</c> (validates,
    /// taking a stronger lock); <c>false</c> = emit it <c>NOT VALID</c> (no scan of existing rows — the
    /// lock-minimizing form; pair with a later <c>VALIDATE CONSTRAINT</c> step, see #137).
    /// </summary>
    public bool ScriptNewConstraintValidation { get; init; } = true;

    /// <summary>
    /// Permit a drop-and-recreate (<see cref="RecreateRawObjectChange"/>) when an in-place ALTER cannot
    /// express the change (SqlPackage <c>AllowTableRecreation</c>). <c>true</c> (default in the generator =
    /// today's behaviour); the publish path resolves it to <c>false</c> so an object recreation surfaces as a
    /// blocked step (commented out) rather than a silent destructive rebuild.
    /// </summary>
    public bool AllowTableRecreation { get; init; } = true;

    /// <summary>
    /// Lock-minimizing deploy (#137 — the <c>PerformIndexOperationsOnline</c> analogue). When on, index
    /// create/drop become <c>CONCURRENTLY</c> and named FK/CHECK adds become <c>NOT VALID</c> + a separate
    /// <c>VALIDATE CONSTRAINT</c> pass; the concurrent/validate steps are emitted outside the BEGIN/COMMIT
    /// (PostgreSQL forbids <c>CONCURRENTLY</c> in a transaction). Off by default (today's blocking DDL).
    /// </summary>
    public bool ConcurrentIndexOperations { get; init; }

    /// <summary>
    /// Object-type tokens whose <em>DROP</em> is suppressed even when drops are otherwise enabled (SqlPackage
    /// <c>DoNotDropObjectType</c> + the granular <c>Drop*NotInSource</c> family). The object's CREATE/ALTER
    /// still emits; only its standalone DROP is filtered out. Empty (default) = drop everything that was
    /// produced. Recreations (<see cref="RecreateRawObjectChange"/>) are governed by
    /// <see cref="AllowTableRecreation"/>, not by this list.
    /// </summary>
    public IReadOnlyList<string> DoNotDropObjectTypes { get; init; } = Array.Empty<string>();

    /// <summary>Object-type tokens to EXCLUDE from generation at the profile level (e.g. <c>extension</c>). Empty = include all.</summary>
    public IReadOnlyList<string> ExcludeObjectTypes { get; init; } = Array.Empty<string>();

    /// <summary>Object-type tokens to INCLUDE exclusively; when non-empty only these are generated. Empty = include all.</summary>
    public IReadOnlyList<string> IncludeOnlyObjectTypes { get; init; } = Array.Empty<string>();

    /// <summary>Emit a <c>SET statement_timeout</c> (ms) preamble. Null = leave the server default.</summary>
    public int? StatementTimeoutMs { get; init; }

    /// <summary>Emit a <c>SET lock_timeout</c> (ms) preamble. Null = leave the server default.</summary>
    public int? LockTimeoutMs { get; init; }

    /// <summary>Verbose output (extra per-change banners/rationale) vs minimal. Off by default (today's header).</summary>
    public bool Verbose { get; init; }

    /// <summary>Target PostgreSQL major version the script is generated for. Null = the project/profile default.</summary>
    public string? TargetPostgresVersion { get; init; }

    /// <summary>
    /// How a possible-data-loss change (risk <see cref="RiskLevel.DataLoss"/>+) is emitted when it is NOT
    /// hard-blocked (<see cref="BlockOnPossibleDataLoss"/> is off). <see cref="DataLossHandling.Include"/> is
    /// the default — today's behaviour, the statement is emitted live. <see cref="DataLossHandling.Comment"/>
    /// emits it commented-out (a reviewer can uncomment); <see cref="DataLossHandling.Omit"/> drops it and
    /// leaves only a marker. (#56)
    /// </summary>
    public DataLossHandling DataLossHandling { get; init; } = DataLossHandling.Include;
}

/// <summary>How the generator emits an un-blocked possible-data-loss change (#56). Default = Include.</summary>
public enum DataLossHandling
{
    /// <summary>Emit the statement live (today's behaviour — the default).</summary>
    Include = 0,

    /// <summary>Emit the statement commented-out so a reviewer can opt in by uncommenting.</summary>
    Comment = 1,

    /// <summary>Omit the statement entirely, leaving only a skipped-marker comment.</summary>
    Omit = 2,
}

/// <summary>Thrown by the block-on-data-loss gate when a deploy would apply a possible-data-loss change.</summary>
public sealed class DataLossBlockedException : Exception
{
    /// <summary>The included changes whose risk is <see cref="RiskLevel.DataLoss"/> or higher.</summary>
    public IReadOnlyList<SelectableChange> Offending { get; }

    public DataLossBlockedException(IReadOnlyList<SelectableChange> offending)
        : base($"Deployment blocked: {offending.Count} possible-data-loss change(s) and " +
               $"BlockOnPossibleDataLoss is set. Offending: " +
               string.Join("; ", offending.Select(c => c.Description)))
        => Offending = offending;
}

/// <summary>
/// Renders an ordered list of <see cref="SchemaChange"/>s into a single deployment script.
/// Changes are grouped and sorted by <see cref="SchemaChange.Phase"/> so the result is always
/// dependency-safe. Pre/post-deploy scripts (when supplied) are spliced as <c>pre → diff → post</c>,
/// and with <see cref="DeployOptions.WrapInTransaction"/> all three live inside one BEGIN/COMMIT so a
/// failing seed rolls the whole publish back.
/// </summary>
public sealed class DeployScriptGenerator
{
    /// <summary>
    /// Generate from a computed <see cref="DeploymentPlan"/> (#55). The plan's skeleton pass runs first
    /// (flagged in the script when present), then its dependency-ordered changes. A plan with no skeleton
    /// pass and the default options produces output equivalent to the change-list overload — the planner's
    /// acyclic ordering is the stable phase order. The skeleton steps are <see cref="SchemaChange"/>s
    /// themselves (negative phase), so they fold naturally into the same emission path.
    /// </summary>
    public string Generate(DeploymentPlan plan, DeployOptions? options = null)
        => Generate(plan.AllSteps, options);

    public string Generate(IReadOnlyList<SchemaChange> changes, DeployOptions? options = null)
        => Generate(changes, options, profile: null);

    /// <summary>
    /// As <see cref="Generate(IReadOnlyList{SchemaChange}, DeployOptions)"/> but with an explicit version
    /// profile (#43/#56). When <paramref name="profile"/> is null it is resolved from
    /// <see cref="DeployOptions.TargetPostgresVersion"/> (so the default path is unchanged). Pass an explicit
    /// profile to drive version-aware DDL against a specific <see cref="Versioning.ObjectCapabilities"/> set.
    /// </summary>
    public string Generate(IReadOnlyList<SchemaChange> changes, DeployOptions? options,
        Versioning.PostgresVersionProfile? profile)
    {
        options ??= new DeployOptions();

        // Block-on-data-loss gate (#54 risk + #58 option). Enforced BEFORE any output is produced so a
        // blocked deploy yields nothing. Default-off, so existing callers are unaffected.
        GuardAgainstDataLoss(changes, options);

        // Include/exclude object-type filtering at generation (#56). Both default to empty = include all, so
        // the default path is the full change set, unchanged.
        var filtered = FilterByObjectType(changes, options);

        // Lock-minimizing rewrite (#137): index ops → CONCURRENTLY, named FK/CHECK adds → NOT VALID + a
        // VALIDATE pass. Idempotent (re-flagging an already-concurrent change is a no-op), so a caller that
        // already transformed the list (e.g. PublishService, so the phased apply matches) can leave the
        // option off here without double-transforming.
        if (options.ConcurrentIndexOperations)
            filtered = LockMinimizer.Apply(filtered);

        // The version profile that drives version-aware DDL (ALTER-vs-recreate via ObjectCapabilities, #43/#56).
        profile ??= Versioning.PostgresVersionProfile.ForTarget(options.TargetPostgresVersion);

        var ordered = filtered.OrderBy(c => c.Phase).ToList();
        var scripts = options.Scripts ?? new DeployScriptBundle();

        // Substitute SQLCMD variables in the deploy scripts up front so an unresolved token fails fast
        // (and before any header is emitted). Object-file substitution is intentionally NOT applied here
        // — default scope is deploy-scripts only (object-file substitution is a documented opt-in, gated
        // in the CLI). See SqlCmdVariableResolver remarks for the $$( escaping rule.
        var preBody = ResolveBody(scripts.Pre, options.Variables);
        var postBody = ResolveBody(scripts.Post, options.Variables);

        var sb = new StringBuilder();

        if (options.IncludeHeader)
        {
            sb.AppendLine("-- ============================================================");
            sb.AppendLine("-- PgProj deployment script");
            sb.AppendLine($"-- {ordered.Count} change(s)" +
                          (ordered.Any(c => c.IsDestructive) ? "  [contains destructive operations]" : ""));
            if (scripts.Pre is not null) sb.AppendLine($"-- pre-deploy:  {scripts.Pre.Name}");
            if (scripts.Post is not null) sb.AppendLine($"-- post-deploy: {scripts.Post.Name}");
            if (options.Variables is not null)
                foreach (var line in options.Variables.BannerLines())
                    sb.AppendLine(line);
            // Verbose header lines (#56): extra context (target version, timeouts, options) — minimal by default.
            if (options.Verbose)
                foreach (var line in VerboseHeaderLines(options, profile))
                    sb.AppendLine(line);
            sb.AppendLine("-- ============================================================");
            sb.AppendLine();
        }

        var nothingToDo = ordered.Count == 0 && scripts.IsEmpty;
        if (nothingToDo)
        {
            sb.AppendLine("-- No changes. Target already matches the source.");
            return sb.ToString();
        }

        // #137: split steps that cannot live in the deploy transaction (CONCURRENTLY index ops, separate
        // VALIDATE passes) from the rest. The transactional body keeps the BEGIN/COMMIT; the non-transactional
        // steps are emitted afterward, each applied in autocommit. With the option off, `outside` is empty and
        // the output is byte-identical to before.
        var inTxn = ordered.Where(c => !c.RunsOutsideTransaction).ToList();
        var outside = ordered.Where(c => c.RunsOutsideTransaction).ToList();

        // Only open a transaction when there is a transactional body (changes or pre/post scripts); a deploy of
        // nothing-but-concurrent-steps must not wrap an empty BEGIN/COMMIT (which would also serve no purpose).
        var wrap = options.WrapInTransaction && (inTxn.Count > 0 || preBody is not null || postBody is not null);

        if (wrap)
        {
            sb.AppendLine("BEGIN;");
            sb.AppendLine();
        }

        // Timeout preamble (#56). Null = leave the server default (today's behaviour ⇒ nothing emitted).
        AppendTimeouts(sb, options);

        // pre → schema diff → post
        AppendScriptSection(sb, "pre-deployment", scripts.Pre?.Name, preBody);

        foreach (var change in inTxn)
            AppendChange(sb, change, options, profile);

        AppendScriptSection(sb, "post-deployment", scripts.Post?.Name, postBody);

        if (wrap)
            sb.AppendLine("COMMIT;");

        if (outside.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("-- ---- non-transactional steps (run outside any BEGIN/COMMIT: CONCURRENTLY / VALIDATE) ----");
            sb.AppendLine();
            foreach (var change in outside)
                AppendChange(sb, change, options, profile);
        }

        return sb.ToString();
    }

    // ---- #56 option-aware emission helpers -------------------------------------------------------

    /// <summary>
    /// Include/exclude object-type filtering (#56). <see cref="DeployOptions.IncludeOnlyObjectTypes"/> wins
    /// when non-empty (only those types are kept); otherwise <see cref="DeployOptions.ExcludeObjectTypes"/>
    /// removes the listed types. Both empty (the default) ⇒ the change set is returned unchanged.
    /// </summary>
    private static IReadOnlyList<SchemaChange> FilterByObjectType(IReadOnlyList<SchemaChange> changes, DeployOptions options)
    {
        var include = options.IncludeOnlyObjectTypes;
        var exclude = options.ExcludeObjectTypes;
        var noDrop = options.DoNotDropObjectTypes;
        if (include.Count == 0 && exclude.Count == 0 && noDrop.Count == 0) return changes;

        var includeSet = new HashSet<string>(include, StringComparer.OrdinalIgnoreCase);
        var excludeSet = new HashSet<string>(exclude, StringComparer.OrdinalIgnoreCase);
        var noDropSet = new HashSet<string>(noDrop, StringComparer.OrdinalIgnoreCase);

        var kept = new List<SchemaChange>(changes.Count);
        foreach (var c in changes)
        {
            var type = SchemaCompareObjectType.Of(c);

            // DoNotDropObjectType / granular Drop*NotInSource (#140): suppress a standalone DROP of a listed
            // kind. A recreation (drop+create) is NOT a standalone drop — it is gated by AllowTableRecreation.
            if (noDropSet.Contains(type) && c.IsDestructive && c is not RecreateRawObjectChange)
                continue;

            if (includeSet.Count > 0) { if (includeSet.Contains(type)) kept.Add(c); }
            else if (!excludeSet.Contains(type)) kept.Add(c);
        }
        return kept;
    }

    /// <summary>The verbose extra-context header lines (#56). Empty unless <see cref="DeployOptions.Verbose"/>.</summary>
    private static IEnumerable<string> VerboseHeaderLines(DeployOptions options, Versioning.PostgresVersionProfile profile)
    {
        yield return $"-- target PostgreSQL: {profile.MajorVersion}";
        yield return $"-- prefer-ALTER: {(options.PreferAlterOverRecreate ? "on" : "off")}; " +
                     $"idempotent: {(options.IdempotentIfExists ? "on" : "off")}; " +
                     $"data-loss handling: {options.DataLossHandling.ToString().ToLowerInvariant()}";
        if (options.StatementTimeoutMs is { } st) yield return $"-- statement_timeout: {st} ms";
        if (options.LockTimeoutMs is { } lt) yield return $"-- lock_timeout: {lt} ms";
    }

    /// <summary>Emit the SET statement_timeout / SET lock_timeout preamble (#56). No-op when both are null.</summary>
    private static void AppendTimeouts(StringBuilder sb, DeployOptions options)
    {
        var any = false;
        if (options.StatementTimeoutMs is { } st) { sb.AppendLine($"SET statement_timeout = {st};"); any = true; }
        if (options.LockTimeoutMs is { } lt) { sb.AppendLine($"SET lock_timeout = {lt};"); any = true; }
        if (any) sb.AppendLine();
    }

    /// <summary>
    /// Emit one change with the #56 options applied: data-loss handling (include/comment/omit), idempotent
    /// IF [NOT] EXISTS rewriting, version-aware ALTER-vs-recreate, and verbose rationale. With default options
    /// this is byte-identical to the original <c>-- describe \n change.ToSql() \n</c> shape.
    /// </summary>
    private static void AppendChange(StringBuilder sb, SchemaChange change, DeployOptions options,
        Versioning.PostgresVersionProfile profile)
    {
        // AllowTableRecreation gate (#140): a drop-and-recreate is the riskiest expressible change. With the
        // option off, emit it commented-out so it surfaces in review rather than silently rebuilding.
        if (!options.AllowTableRecreation && change is RecreateRawObjectChange)
        {
            sb.AppendLine($"-- [blocked: object recreation; set AllowTableRecreation to apply] {change.Describe()}");
            foreach (var line in change.ToSql().Split('\n'))
                sb.AppendLine("-- " + line.TrimEnd('\r'));
            sb.AppendLine();
            return;
        }

        // Data-loss handling for un-blocked DataLoss changes (#54 risk + #56). Default = Include (live).
        if (options.DataLossHandling != DataLossHandling.Include &&
            RiskAnalyzer.Default.Classify(change).Level >= RiskLevel.DataLoss)
        {
            if (options.DataLossHandling == DataLossHandling.Omit)
            {
                sb.AppendLine($"-- [omitted: possible data loss] {change.Describe()}");
                sb.AppendLine();
                return;
            }
            // Comment: keep the statement but commented out so a reviewer can opt in.
            sb.AppendLine($"-- [commented: possible data loss] {change.Describe()}");
            foreach (var line in change.ToSql().Split('\n'))
                sb.AppendLine("-- " + line.TrimEnd('\r'));
            sb.AppendLine();
            return;
        }

        sb.AppendLine($"-- {change.Describe()}");
        if (options.Verbose)
        {
            var risk = RiskAnalyzer.Default.Classify(change);
            sb.AppendLine($"--   risk: {risk.Level}; phase: {change.Phase}" +
                          (change.RunsOutsideTransaction ? "; lock: non-blocking (outside transaction)" : ""));
        }

        // ADD COLUMN ... DEFAULT version gate (#137): PostgreSQL 11+ applies a column default as a fast
        // metadata-only change; before 11 it rewrites the whole table under ACCESS EXCLUSIVE. Warn so the
        // operator knows the lock/cost profile on an older target.
        if (change is AddColumnChange addCol && !string.IsNullOrWhiteSpace(addCol.Column.Default)
            && profile.MajorVersion is > 0 and < 11)
            sb.AppendLine($"--   [warning: ADD COLUMN with DEFAULT rewrites the table under ACCESS EXCLUSIVE on PostgreSQL {profile.MajorVersion} (< 11); 11+ is metadata-only]");

        // CONCURRENTLY can leave an INVALID index behind if the build fails — flag it so cleanup is expected.
        if (change is CreateIndexChange { Concurrent: true } cic)
            sb.AppendLine($"--   [note: a failed CONCURRENTLY build leaves an INVALID index '{cic.Index.Name}'; DROP INDEX it and retry]");

        sb.AppendLine(RenderSql(change, options, profile));
        sb.AppendLine();
    }

    /// <summary>
    /// Render a change's SQL with the #56 idempotent option applied. Idempotent rewriting adds
    /// <c>IF [NOT] EXISTS</c> to the CREATE/DROP forms that accept it where the base SQL did not already
    /// carry it; with the option off (default) the base <see cref="SchemaChange.ToSql"/> is returned verbatim.
    /// </summary>
    private static string RenderSql(SchemaChange change, DeployOptions options,
        Versioning.PostgresVersionProfile profile)
    {
        // Version-aware DDL (#43/#56): if the target version lacks an in-place ALTER path that this change
        // relies on, the in-place ALTER would fail — surface that rather than emit invalid SQL. The default
        // (latest) profile has every ALTER path, so this branch is inert on the default path.
        if (change is AlterColumnChange ac && !CanAlterColumnOn(ac, profile.ObjectCapabilities))
            return VersionFallbackComment(ac, profile);

        var sql = change.ToSql();

        // GenerateSmartDefaults (#140): a NOT NULL column add with no default would fail on a populated table.
        // Synthesize a type-appropriate DEFAULT so the ADD COLUMN succeeds (PG11+ applies it without a rewrite).
        if (options.GenerateSmartDefaults && change is AddColumnChange add
            && !add.Column.IsNullable && string.IsNullOrWhiteSpace(add.Column.Default)
            && !add.Column.IsIdentity && add.Column.GeneratedExpression is null
            && SmartDefaultFor(add.Column.DataType) is { } smart)
            sql = InjectBeforeTerminator(sql, $" DEFAULT {smart}");

        // ScriptNewConstraintValidation (#140): when off, add new FK/CHECK constraints NOT VALID so they skip
        // the existing-row scan (lock-minimizing). #137 emits the follow-up VALIDATE CONSTRAINT step.
        if (!options.ScriptNewConstraintValidation && change is AddForeignKeyChange or AddCheckConstraintChange)
            sql = InjectBeforeTerminator(sql, " NOT VALID");

        if (options.IdempotentIfExists) sql = MakeIdempotent(change, sql);
        return sql;
    }

    /// <summary>
    /// A type-appropriate non-null literal for <see cref="DeployOptions.GenerateSmartDefaults"/>. Returns null
    /// for types we cannot safely synthesize (the caller then leaves the column without a default). The value
    /// is a backfill only — it satisfies NOT NULL on existing rows; new code is expected to set real values.
    /// </summary>
    internal static string? SmartDefaultFor(string dataType)
    {
        var t = dataType.Trim().ToLowerInvariant();
        var paren = t.IndexOf('(');
        if (paren >= 0) t = t[..paren].Trim();
        t = t.Replace("[]", "").Trim();
        return t switch
        {
            "smallint" or "integer" or "int" or "int2" or "int4" or "int8" or "bigint"
                or "numeric" or "decimal" or "real" or "double precision" or "float4" or "float8"
                or "smallserial" or "serial" or "bigserial" => "0",
            "boolean" or "bool" => "false",
            "text" or "varchar" or "character varying" or "char" or "character" or "citext" or "name" => "''",
            "bytea" => "'\\x'",
            "json" or "jsonb" => "'{}'",
            "uuid" => "'00000000-0000-0000-0000-000000000000'",
            "date" => "CURRENT_DATE",
            "time" or "time without time zone" or "time with time zone" or "timetz" => "CURRENT_TIME",
            "timestamp" or "timestamp without time zone" or "timestamptz" or "timestamp with time zone" => "CURRENT_TIMESTAMP",
            "interval" => "'0'",
            _ => null,   // unknown/complex type — cannot safely synthesize a backfill default
        };
    }

    /// <summary>Insert <paramref name="text"/> immediately before the statement's final <c>;</c> (or at the end).</summary>
    private static string InjectBeforeTerminator(string sql, string text)
    {
        var i = sql.LastIndexOf(';');
        return i < 0 ? sql + text : sql[..i] + text + sql[i..];
    }

    // Which column facets actually differ, and whether the target version can ALTER them all in place.
    private static bool CanAlterColumnOn(AlterColumnChange ac, Versioning.ObjectCapabilities caps)
    {
        var typeChanged = ac.Old.DataType != ac.New.DataType;
        var nullabilityChanged = ac.Old.IsNullable != ac.New.IsNullable;
        var defaultChanged = (ac.Old.Default ?? "") != (ac.New.Default ?? "");
        return caps.CanAlterColumn(typeChanged, nullabilityChanged, defaultChanged);
    }

    private static string VersionFallbackComment(AlterColumnChange ac, Versioning.PostgresVersionProfile profile) =>
        $"-- [skipped on PostgreSQL {profile.MajorVersion}: in-place ALTER COLUMN not supported for this change] " +
        $"{ac.Describe()}; a table/column rebuild is required.";

    /// <summary>
    /// Add <c>IF [NOT] EXISTS</c> to the statements that support it and do not already carry it. Scoped to
    /// the unambiguous, dialect-safe cases: <c>CREATE TABLE</c>, <c>CREATE INDEX</c>, <c>CREATE VIEW</c>,
    /// <c>CREATE MATERIALIZED VIEW</c>, and table/column/constraint DROPs. <c>CREATE OR REPLACE</c> is already
    /// idempotent (left as-is); <c>CREATE SCHEMA/SEQUENCE</c> already emit IF NOT EXISTS unconditionally.
    /// </summary>
    private static string MakeIdempotent(SchemaChange change, string sql) => change switch
    {
        CreateTableChange       => InsertAfter(sql, "CREATE TABLE ", "IF NOT EXISTS "),
        CreateIndexChange ix    => InsertAfter(sql, ix.Index.IsUnique ? "CREATE UNIQUE INDEX " : "CREATE INDEX ", "IF NOT EXISTS "),
        AddColumnChange         => InsertAfter(sql, "ADD COLUMN ", "IF NOT EXISTS "),
        DropColumnChange        => InsertAfter(sql, "DROP COLUMN ", "IF EXISTS "),
        DropConstraintChange    => InsertAfter(sql, "DROP CONSTRAINT ", "IF EXISTS "),
        DropForeignKeyChange    => InsertAfter(sql, "DROP CONSTRAINT ", "IF EXISTS "),
        DropPrimaryKeyChange    => InsertAfter(sql, "DROP CONSTRAINT ", "IF EXISTS "),
        _ => sql, // DROP TABLE/VIEW/INDEX, CREATE SCHEMA/SEQUENCE already carry IF [NOT] EXISTS; OR REPLACE is idempotent.
    };

    // Insert <paramref name="insert"/> immediately after the first occurrence of <paramref name="anchor"/>,
    // unless the guard is already present right there (so re-running is safe and we never double-insert).
    private static string InsertAfter(string sql, string anchor, string insert)
    {
        var idx = sql.IndexOf(anchor, StringComparison.Ordinal);
        if (idx < 0) return sql;
        var at = idx + anchor.Length;
        if (sql.AsSpan(at).StartsWith(insert)) return sql; // already idempotent
        return sql[..at] + insert + sql[at..];
    }

    /// <summary>
    /// The block-on-data-loss enforcement point (#58). When <see cref="DeployOptions.BlockOnPossibleDataLoss"/>
    /// is set and any change classifies at <see cref="RiskLevel.DataLoss"/> or higher (#54), throws
    /// <see cref="DataLossBlockedException"/>. A no-op when the option is off (the default) — so behaviour is
    /// unchanged for existing callers. Public so the planner/CLI can run the same check ahead of generation.
    /// </summary>
    public static void GuardAgainstDataLoss(IReadOnlyList<SchemaChange> changes, DeployOptions options)
    {
        if (!options.BlockOnPossibleDataLoss) return;

        var offending = new List<SelectableChange>();
        var i = 0;
        foreach (var change in changes)
        {
            if (RiskAnalyzer.Default.Classify(change).Level >= RiskLevel.DataLoss)
            {
                // Wrap so the exception carries the human description; id is positional+stable hash.
                offending.Add(new SelectableChange(
                    SelectableChange.HashOf(SelectableChange.Signature(change)) + "#" + i,
                    change, included: true));
            }
            i++;
        }

        if (offending.Count > 0) throw new DataLossBlockedException(offending);
    }

    private static string? ResolveBody(DeployScript? script, SqlCmdVariableResolver? variables)
    {
        if (script is null) return null;
        return variables is null ? script.Body : variables.Substitute(script.Body, script.Name);
    }

    private static void AppendScriptSection(StringBuilder sb, string label, string? name, string? body)
    {
        if (body is null) return;
        sb.AppendLine($"-- ---- {label} script: {name} ----");
        // Verbatim pass-through: append the (already variable-substituted) body untouched so dollar-quoted
        // bodies and embedded semicolons survive. Guarantee a trailing newline and a blank separator.
        sb.AppendLine(body.TrimEnd('\r', '\n'));
        sb.AppendLine();
    }
}

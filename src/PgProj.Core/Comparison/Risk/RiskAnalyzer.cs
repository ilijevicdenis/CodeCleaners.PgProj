using System;
using System.Collections.Generic;
using PgProj.Core.Model;

namespace PgProj.Core.Comparison.Risk;

/// <summary>
/// Classifies each <see cref="SchemaChange"/> by deployment impact (<see cref="ChangeRisk"/>). It sits
/// between the diff engine and the planner (<c>Diff → Risk → Planner</c>): the UI badges the level, and
/// the Phase-18 "block on possible data loss" option gates on it.
/// </summary>
/// <remarks>
/// <para>
/// The analyzer is <b>pure</b> — it derives its verdict entirely from the change record (and, for an
/// <see cref="AlterColumnChange"/>, the field-level deltas it already carries in <c>Old</c>/<c>New</c>:
/// type, nullability, default). It does not read a live database, so it is fully DB-free and deterministic.
/// </para>
/// <para>
/// Representative mapping (see the per-kind switch):
/// add nullable column → Safe; add NOT NULL column without a default → Dangerous (fails on a non-empty
/// table); widen <c>integer→bigint</c> → Warning; narrow <c>bigint→integer</c> → DataLoss; SET NOT NULL →
/// Warning; DROP COLUMN/TABLE/SEQUENCE → DataLoss; a type change forcing a table rewrite → Dangerous (and
/// flagged as a rewrite + lock).
/// </para>
/// </remarks>
public sealed class RiskAnalyzer
{
    /// <summary>The default analyzer. Stateless, so a single shared instance is safe.</summary>
    public static RiskAnalyzer Default { get; } = new();

    /// <summary>Classifies one change.</summary>
    public ChangeRisk Classify(SchemaChange change) => change switch
    {
        // ---- additive / in-place, no data impact ------------------------------------------------
        CreateSchemaChange => Safe("Create a new schema; no effect on existing data."),
        CreateSequenceChange => Safe("Create a new sequence."),
        AlterSequenceChange => Safe("Adjust sequence options; existing rows are untouched."),
        CreateTableChange => Safe("Create a new table."),
        // A concurrent build is non-blocking (SHARE UPDATE EXCLUSIVE, reads+writes continue) but can leave an
        // INVALID index if it fails; a plain build takes a SHARE lock that blocks writes while it scans (#137).
        CreateIndexChange { Concurrent: true } => Warning("Create an index CONCURRENTLY; non-blocking (reads/writes continue), but a failed build leaves an INVALID index to clean up.", rewrite: false, exclusiveLock: false),
        CreateIndexChange => Warning("Create an index; blocks writes while it scans the table.", rewrite: false, exclusiveLock: false),
        AddForeignKeyChange { NotValid: true } => Warning("Add a foreign key NOT VALID; instant, no existing-row scan (a separate VALIDATE pass checks rows non-blockingly)."),
        AddForeignKeyChange => Warning("Add a foreign key; validates existing rows under a lock."),
        AddPrimaryKeyChange => Warning("Add a primary key; builds a unique index and may reject existing rows."),
        AddCheckConstraintChange { NotValid: true } => Warning("Add a check constraint NOT VALID; instant, no existing-row scan (a separate VALIDATE pass checks rows non-blockingly)."),
        AddCheckConstraintChange => Warning("Add a check constraint; validates existing rows."),
        ValidateConstraintChange => Safe("Validate a constraint; scans rows under a SHARE UPDATE EXCLUSIVE lock (reads/writes continue)."),
        AddRawTableConstraintChange => Warning("Add a table constraint; may validate existing rows."),
        CreateOrReplaceViewChange => Safe("Create or replace a view; no table data is touched."),
        CreateOrReplaceFunctionChange => Safe("Create or replace a function; no table data is touched."),

        AddColumnChange add => ClassifyAddColumn(add),
        AlterColumnChange alter => ClassifyAlterColumn(alter),

        // ---- drops: data / object loss ----------------------------------------------------------
        DropColumnChange => DataLoss("Drop a column; all data in that column is permanently lost."),
        DropTableChange => DataLoss("Drop a table; all rows are permanently lost."),
        DropViewChange => Warning("Drop a view; no table data is lost, but dependents break."),
        DropIndexChange => Warning("Drop an index; no data loss, but queries may slow."),
        DropPrimaryKeyChange => Dangerous("Drop a primary key; dependent foreign keys and row identity are affected."),
        DropConstraintChange => Warning("Drop a constraint; data is retained but no longer validated."),
        DropForeignKeyChange => Warning("Drop a foreign key; referential integrity is no longer enforced."),

        // ---- raw objects ------------------------------------------------------------------------
        CreateRawObjectChange => Safe("Create a new object."),
        RecreateRawObjectChange r => ClassifyRecreate(r),
        DropRawObjectChange => DataLoss("Drop an object; dependent data/objects may be lost."),

        _ => ChangeRisk.Unknown,
    };

    // ---- column add ----------------------------------------------------------------------------

    private static ChangeRisk ClassifyAddColumn(AddColumnChange add)
    {
        var col = add.Column;
        if (col.IsNullable)
            return Safe("Add a nullable column; existing rows get NULL, no rewrite.");

        // NOT NULL with no default fails outright on a non-empty table.
        if (string.IsNullOrWhiteSpace(col.Default) && !col.IsIdentity && !col.IsSerial)
            return Dangerous("Add a NOT NULL column without a default; fails if the table has rows.");

        // NOT NULL with a constant default is metadata-only on modern PG; still flag the lock.
        return Warning("Add a NOT NULL column with a default; backfills existing rows under a lock.",
            rewrite: false, exclusiveLock: true);
    }

    // ---- column alter (reads the field-level deltas off Old/New) --------------------------------

    private static ChangeRisk ClassifyAlterColumn(AlterColumnChange alter)
    {
        var oldCol = alter.Old;
        var newCol = alter.New;

        var typeChanged = !TypeEquals(oldCol.DataType, newCol.DataType);
        var becameNotNull = oldCol.IsNullable && !newCol.IsNullable;
        var becameNullable = !oldCol.IsNullable && newCol.IsNullable;

        // The type change dominates the verdict — it's the only facet that can rewrite or lose data.
        if (typeChanged)
            return ClassifyTypeChange(oldCol.DataType, newCol.DataType);

        if (becameNotNull)
            return Warning("Set NOT NULL; fails if any existing row is NULL.", rewrite: false, exclusiveLock: true);

        if (becameNullable)
            return Safe("Drop NOT NULL; relaxes the constraint, no data impact.");

        // Default-only change.
        return Safe("Change column default; existing rows are unaffected.");
    }

    private static ChangeRisk ClassifyTypeChange(string oldType, string newType)
    {
        var oldT = TypeCategory.Of(oldType);
        var newT = TypeCategory.Of(newType);

        // Integer family: width comparison is unambiguous.
        if (oldT.Family == TypeFamily.Integer && newT.Family == TypeFamily.Integer)
        {
            if (newT.Rank > oldT.Rank)
                return Warning($"Widen {oldType} → {newType}; safe but rewrites the table under a lock.",
                    rewrite: true, exclusiveLock: true);
            if (newT.Rank < oldT.Rank)
                return DataLoss($"Narrow {oldType} → {newType}; values exceeding the smaller range overflow and are lost.",
                    requiresTableRewrite: true, requiresExclusiveLock: true);
            return Warning($"Change {oldType} → {newType}; rewrites the table under a lock.", rewrite: true, exclusiveLock: true);
        }

        // Same base type, length only (e.g. varchar(100) → varchar(50) or numeric(10,2) → numeric(6,2)).
        if (oldT.Family == newT.Family && string.Equals(oldT.BaseName, newT.BaseName, StringComparison.OrdinalIgnoreCase))
        {
            var cmp = LengthCompare(oldT.PrimaryArg, newT.PrimaryArg);
            if (cmp < 0)
                return DataLoss($"Shrink {oldType} → {newType}; values longer than the new length are truncated/rejected.",
                    requiresTableRewrite: false, requiresExclusiveLock: true);
            if (cmp > 0)
                return Warning($"Grow {oldType} → {newType}; safe, no data loss.", rewrite: false, exclusiveLock: true);
            return Safe($"Change {oldType} → {newType}; no effective change to stored values.");
        }

        // Cross-family or otherwise-unknown conversion forces a rewrite and may fail/lose data.
        return Dangerous($"Convert {oldType} → {newType}; rewrites the table under a lock and may fail or lose data.",
            rewrite: true, exclusiveLock: true);
    }

    private static ChangeRisk ClassifyRecreate(RecreateRawObjectChange r) =>
        r.IsDestructive
            ? Dangerous($"Recreate {r.Def.Kind.ToString().ToLowerInvariant()} {r.Def.Name}; the drop can cascade to dependent data.")
            : Warning($"Redefine {r.Def.Kind.ToString().ToLowerInvariant()} {r.Def.Name} in place.");

    // ---- helpers -------------------------------------------------------------------------------

    private static bool TypeEquals(string a, string b) =>
        string.Equals(TypeNormalizer.Normalize(a), TypeNormalizer.Normalize(b), StringComparison.OrdinalIgnoreCase);

    // Compare two optional length/precision specs: <0 shrink, 0 equal/unknown, >0 grow.
    private static int LengthCompare(int? oldArg, int? newArg)
    {
        if (oldArg is null || newArg is null) return 0; // unknown one side (e.g. unbounded varchar) → no verdict
        return newArg.Value.CompareTo(oldArg.Value);
    }

    private static ChangeRisk Safe(string why) => new(RiskLevel.Safe, why);
    private static ChangeRisk Warning(string why, bool rewrite = false, bool exclusiveLock = false) =>
        new(RiskLevel.Warning, why, rewrite, exclusiveLock);
    private static ChangeRisk Dangerous(string why, bool rewrite = false, bool exclusiveLock = false) =>
        new(RiskLevel.Dangerous, why, rewrite, exclusiveLock);
    private static ChangeRisk DataLoss(string why, bool requiresTableRewrite = false, bool requiresExclusiveLock = false) =>
        new(RiskLevel.DataLoss, why, requiresTableRewrite, requiresExclusiveLock);

    // ---- type categorization -------------------------------------------------------------------

    private enum TypeFamily { Other, Integer, Decimal, Text, BitString }

    /// <summary>A parsed view of a canonical type spelling: family, base name, rank (for integers), and
    /// its primary length/precision argument (for text/bit/numeric width comparisons).</summary>
    private readonly record struct TypeCategory(TypeFamily Family, string BaseName, int Rank, int? PrimaryArg)
    {
        private static readonly Dictionary<string, int> IntegerRanks = new(StringComparer.OrdinalIgnoreCase)
        {
            ["smallint"] = 1,
            ["integer"] = 2,
            ["bigint"] = 3,
        };

        public static TypeCategory Of(string rawType)
        {
            var norm = TypeNormalizer.Normalize(rawType);

            // Array types never compare by width here.
            var isArray = norm.EndsWith("[]", StringComparison.Ordinal);
            if (isArray) norm = norm[..^2].TrimEnd();

            // Split base name from a parenthesised arg spec.
            string baseName = norm;
            int? primaryArg = null;
            var open = norm.IndexOf('(');
            if (open >= 0 && norm.EndsWith(")", StringComparison.Ordinal))
            {
                baseName = norm[..open].Trim();
                var inner = norm[(open + 1)..^1];
                var firstPart = inner.Split(',')[0].Trim();
                if (int.TryParse(firstPart, out var n)) primaryArg = n;
            }

            if (!isArray && IntegerRanks.TryGetValue(baseName, out var rank))
                return new TypeCategory(TypeFamily.Integer, baseName, rank, primaryArg);

            var family = baseName switch
            {
                "numeric" => TypeFamily.Decimal,
                "character" or "character varying" => TypeFamily.Text,
                "bit" or "bit varying" => TypeFamily.BitString,
                _ => TypeFamily.Other,
            };
            return new TypeCategory(isArray ? TypeFamily.Other : family, baseName, 0, isArray ? null : primaryArg);
        }
    }
}

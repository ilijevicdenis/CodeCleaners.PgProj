using System.Collections.Generic;

namespace PgProj.Core.Comparison;

/// <summary>
/// Rewrites a change list into its lock-minimizing form (issue #137 — the PostgreSQL analogue of
/// SqlPackage's <c>PerformIndexOperationsOnline</c>). Index creates/drops become <c>CONCURRENTLY</c>
/// (built without an <c>ACCESS EXCLUSIVE</c> lock, outside any transaction), and a newly added,
/// <em>named</em> FK/CHECK constraint is split into an <c>ADD … NOT VALID</c> (instant, no row scan)
/// followed by a separate <c>VALIDATE CONSTRAINT</c> pass (SHARE UPDATE EXCLUSIVE, doesn't block writes).
///
/// The transform lives on the change list — not just in the script text — so the generator (script) and
/// the <see cref="Publishing.PhasedDeployer"/> (apply) produce byte-identical SQL. Steps flagged
/// <see cref="SchemaChange.RunsOutsideTransaction"/> are emitted after <c>COMMIT</c> and applied in
/// autocommit. Unnamed FK/CHECK constraints are left validated-in-place (PostgreSQL auto-names them, so
/// there is no name to <c>VALIDATE</c> separately).
/// </summary>
public static class LockMinimizer
{
    /// <summary>Returns a new list with lock-minimizing rewrites applied. The input is not mutated.</summary>
    public static IReadOnlyList<SchemaChange> Apply(IReadOnlyList<SchemaChange> changes)
    {
        var result = new List<SchemaChange>(changes.Count);
        var validations = new List<SchemaChange>();

        foreach (var change in changes)
        {
            switch (change)
            {
                case CreateIndexChange ci when !ci.Concurrent:
                    result.Add(ci with { Concurrent = true });
                    break;

                case DropIndexChange di when !di.Concurrent:
                    result.Add(di with { Concurrent = true });
                    break;

                case AddForeignKeyChange fk when !fk.NotValid && !string.IsNullOrEmpty(fk.ForeignKey.Name):
                    result.Add(fk with { NotValid = true });
                    validations.Add(new ValidateConstraintChange(fk.Table.Schema, fk.Table.Name, fk.ForeignKey.Name!));
                    break;

                case AddCheckConstraintChange ck when !ck.NotValid && !string.IsNullOrEmpty(ck.Check.Name):
                    result.Add(ck with { NotValid = true });
                    validations.Add(new ValidateConstraintChange(ck.Schema, ck.Table, ck.Check.Name!));
                    break;

                default:
                    result.Add(change);
                    break;
            }
        }

        result.AddRange(validations);
        return result;
    }
}

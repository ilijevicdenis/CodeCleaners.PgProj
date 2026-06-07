using System;
using PgProj.Core.Model;

namespace PgProj.Core.Comparison;

/// <summary>
/// A skeleton-pass step (issue #55): a minimal "shell" form of a cycle member, emitted BEFORE the main
/// ordered pass so the rest of a hard dependency cycle can be created against the stub. The real definition
/// runs later in the ordered pass and completes (CREATE OR REPLACE) the object.
///
/// <para>It runs at the very front of the plan, so its <see cref="Phase"/> sorts ahead of everything; it is
/// never destructive (it only creates a stub). The <see cref="Describe"/> text flags it as a skeleton so a
/// reviewer sees the two-pass strategy in the generated script.</para>
/// </summary>
public sealed record SkeletonChange(string SeedKey, string StubSql, string Subject) : SchemaChange
{
    // Sorts ahead of the earliest real change (CreateSchema is phase 10). A negative phase guarantees the
    // skeleton always precedes the objects it unblocks even if a caller folds it into the main list.
    public override int Phase => -100;
    public override bool IsDestructive => false;
    public override string Describe() => $"Skeleton (cycle break): {Subject}";
    public override string ToSql() => StubSql;
}

/// <summary>
/// Builds the skeleton form of a cycle-seed change. Functions are the canonical case: a function can be
/// created with a trivial body first (so a peer that calls it resolves), then redefined with its real body
/// in the ordered pass — Postgres accepts <c>CREATE OR REPLACE FUNCTION</c> for the completion. Views get a
/// best-effort placeholder. Kinds we cannot safely stub return <see cref="CanSkeleton"/> = false, and the
/// planner falls back to phase-only ordering for that cycle.
/// </summary>
internal static class SkeletonStep
{
    /// <summary>True when this change creates an object we can emit a safe stand-in (skeleton) for.</summary>
    public static bool CanSkeleton(SchemaChange change) => change switch
    {
        CreateOrReplaceFunctionChange => true,
        CreateOrReplaceViewChange     => true,
        _ => false,
    };

    /// <summary>Build the skeleton step for a seed change (caller must have checked <see cref="CanSkeleton"/>).</summary>
    public static SkeletonChange For(SchemaChange change) => change switch
    {
        CreateOrReplaceFunctionChange f => FunctionStub(f.Function),
        CreateOrReplaceViewChange v     => ViewStub(v.View),
        _ => throw new ArgumentException($"Cannot build a skeleton for {change.GetType().Name}", nameof(change)),
    };

    // A function stub: same signature, a body that does nothing but satisfy the return type, so a peer that
    // references it resolves. The real CREATE OR REPLACE in the ordered pass swaps in the true body.
    private static SkeletonChange FunctionStub(FunctionDefinition f)
    {
        var qn = SqlEmitter.Qualified(f.Schema, f.Name);
        var key = $"{f.Schema}.{f.Name}({f.ArgTypes})".ToLowerInvariant();
        var argList = string.IsNullOrWhiteSpace(f.ArgTypes) ? "" : f.ArgTypes;
        var stub =
            $"-- skeleton stub; completed by the CREATE OR REPLACE below\n" +
            $"CREATE OR REPLACE FUNCTION {qn}({argList})\n" +
            $"RETURNS void AS $skeleton$ BEGIN END $skeleton$ LANGUAGE plpgsql;";
        return new SkeletonChange(key, stub, $"function {f.Schema}.{f.Name}");
    }

    // A view stub: a placeholder definition with no peer references, replaced by the real view later. Uses a
    // single NULL column so the CREATE succeeds without resolving the cycle; CREATE OR REPLACE later widens it.
    private static SkeletonChange ViewStub(ViewDefinition v)
    {
        var qn = SqlEmitter.Qualified(v.Schema, v.Name);
        var key = $"{v.Schema}.{v.Name}".ToLowerInvariant();
        var stub =
            $"-- skeleton stub; completed by the CREATE OR REPLACE below\n" +
            $"CREATE OR REPLACE VIEW {qn} AS SELECT NULL::int WHERE false;";
        return new SkeletonChange(key, stub, $"view {v.Schema}.{v.Name}");
    }
}

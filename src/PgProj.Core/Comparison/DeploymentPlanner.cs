using System;
using System.Collections.Generic;
using System.Linq;
using PgProj.Core.Semantics.Dependencies;

namespace PgProj.Core.Comparison;

/// <summary>
/// One concrete, ordered deployment plan: the change list in the exact order the generator/deployer must
/// apply it, plus any extra skeleton-pass steps a hard cycle forced (see <see cref="SkeletonPass"/>).
/// </summary>
/// <remarks>
/// The plan is what the script generator and the <see cref="Introspection.PhasedDeployer"/> consume instead
/// of an ad-hoc <c>OrderBy(Phase)</c>. For the common (acyclic) case <see cref="Ordered"/> is exactly the
/// stable phase order — see <see cref="DeploymentPlanner"/> for the equivalence guarantee.
/// </remarks>
public sealed class DeploymentPlan
{
    /// <summary>
    /// Skeleton-pass steps that run BEFORE <see cref="Ordered"/> to break a hard dependency cycle: a
    /// minimal "shell" form of one cycle member (e.g. a stub function body) so its peers can be created,
    /// then the real definitions in <see cref="Ordered"/> complete (CREATE OR REPLACE) them. Empty for the
    /// common acyclic case.
    /// </summary>
    public IReadOnlyList<SchemaChange> SkeletonPass { get; }

    /// <summary>The main, dependency-ordered change list (skeleton members appear here in their final form).</summary>
    public IReadOnlyList<SchemaChange> Ordered { get; }

    /// <summary>The hard cycles the planner had to break with a skeleton pass (for diagnostics/banners).</summary>
    public IReadOnlyList<DependencyCycle> BrokenCycles { get; }

    /// <summary>The all-soft cycles detected — ordering preference could not be honoured, but deploy is safe.</summary>
    public IReadOnlyList<DependencyCycle> SoftCycles { get; }

    /// <summary>True when a skeleton-then-alter pass was needed to break at least one hard cycle.</summary>
    public bool HasSkeletonPass => SkeletonPass.Count > 0;

    /// <summary>The full sequence to apply: skeleton pass first, then the ordered changes.</summary>
    public IReadOnlyList<SchemaChange> AllSteps => SkeletonPass.Concat(Ordered).ToList();

    internal DeploymentPlan(
        IReadOnlyList<SchemaChange> skeletonPass,
        IReadOnlyList<SchemaChange> ordered,
        IReadOnlyList<DependencyCycle> brokenCycles,
        IReadOnlyList<DependencyCycle> softCycles)
    {
        SkeletonPass = skeletonPass;
        Ordered = ordered;
        BrokenCycles = brokenCycles;
        SoftCycles = softCycles;
    }
}

/// <summary>
/// Phase 13 (issue #55) — turns a risk-annotated change set into a deterministic, dependency-safe
/// <see cref="DeploymentPlan"/>.
///
/// <para><b>Two-layer ordering.</b> The hand-coded per-kind <see cref="SchemaChange.Phase"/> integers stay
/// the COARSE layer: every change is first bucketed by phase (drop FKs first, create schemas/tables before
/// their dependents, add FKs once tables exist, leave drops for last). The dependency graph (issue #50)
/// then REFINES the order <em>within and across</em> phases where it has real edges — a same-phase
/// <c>A → B</c> Hard edge makes B precede A even though both sit at phase 75. The refinement is a stable
/// topological sort keyed on the existing phase, so when the graph has no relevant edge the result is
/// byte-identical to the old <c>OrderBy(Phase)</c> (the golden deploy script is unchanged).</para>
///
/// <para><b>Edge classes (issue #50).</b> <see cref="DependencyKind.Hard"/> edges constrain the topo order
/// (Postgres enforces them); <see cref="DependencyKind.Soft"/> edges are honoured as a <em>preference</em>
/// (an ordering constraint that is dropped rather than allowed to deadlock); <see cref="DependencyKind.Runtime"/>
/// edges are ignored entirely (they fire at call time, after everything already exists).</para>
///
/// <para><b>Hard cycles → skeleton-then-alter.</b> When a set of objects form a hard cycle (two mutually
/// referencing views/functions, a shell type → function → complete type loop) no single order works.
/// The planner detects the cycle (Tarjan SCC over Hard+Soft edges, reusing <see cref="CycleDetector"/>),
/// picks one member as the "skeleton seed", and emits a minimal stub form of it FIRST (the
/// <see cref="DeploymentPlan.SkeletonPass"/>) so the rest of the cycle can be created against the stub; the
/// real definitions then run in <see cref="DeploymentPlan.Ordered"/> and complete the seed (CREATE OR
/// REPLACE). An all-soft cycle is NOT broken (Postgres accepts the objects in any order) — it is recorded
/// in <see cref="DeploymentPlan.SoftCycles"/> and the topo-sort merely drops the soft back-edge.</para>
/// </summary>
public sealed class DeploymentPlanner
{
    // Reference identity for change->metadata maps: two distinct change records with equal field values
    // (e.g. two identical AddColumn changes) must stay distinct nodes, so we key on object identity, not
    // record value-equality. Typed (not ReferenceEqualityComparer.Instance, which is IEqualityComparer<object?>)
    // so the dictionaries stay non-null-key clean.
    private static readonly IEqualityComparer<SchemaChange> ReferenceComparer =
        new IdentityComparer();

    private sealed class IdentityComparer : IEqualityComparer<SchemaChange>
    {
        public bool Equals(SchemaChange? x, SchemaChange? y) => ReferenceEquals(x, y);
        public int GetHashCode(SchemaChange obj) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
    }

    /// <summary>
    /// Build a plan for <paramref name="changes"/>. With no <paramref name="graph"/> the plan is the stable
    /// phase order (today's behaviour). With a graph the topo-sort refines order on the graph's Hard/Soft
    /// edges and a hard cycle triggers a skeleton pass.
    /// </summary>
    public DeploymentPlan Plan(IReadOnlyList<SchemaChange> changes, DependencyGraph? graph = null)
    {
        if (changes.Count == 0)
            return new DeploymentPlan(Array.Empty<SchemaChange>(), Array.Empty<SchemaChange>(),
                Array.Empty<DependencyCycle>(), Array.Empty<DependencyCycle>());

        // Coarse layer: stable phase order. OrderBy is a stable sort, so two changes at the same phase keep
        // their input order — this is exactly today's DeployScriptGenerator ordering and the fall-back the
        // topo-sort starts from.
        var phaseOrdered = changes
            .Select((c, i) => (change: c, idx: i))
            .OrderBy(t => t.change.Phase)
            .ThenBy(t => t.idx)
            .Select(t => t.change)
            .ToList();

        if (graph is null)
            return new DeploymentPlan(Array.Empty<SchemaChange>(), phaseOrdered,
                Array.Empty<DependencyCycle>(), Array.Empty<DependencyCycle>());

        // Map each change to a graph node key (schema.name lowercased) when it touches a graph-tracked
        // object (relation/type/function). Changes with no key (raw comments, FK adds keyed to a table)
        // simply have no graph edges and are ordered by phase alone.
        var keyOf = new Dictionary<SchemaChange, string?>(ReferenceComparer);
        foreach (var c in phaseOrdered) keyOf[c] = ChangeKey.Of(c);

        var cycles = CycleDetector.Detect(graph);
        var hardCycles = cycles.Where(cy => cy.IsHard).ToList();
        var softCycles = cycles.Where(cy => !cy.IsHard).ToList();

        // Pick the skeleton seed key per hard cycle (deterministic: lexicographically smallest member) and,
        // if a change in this set creates a skeleton-able seed object, emit its stub form first.
        var seeds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var skeletonPass = new List<SchemaChange>();
        foreach (var cycle in hardCycles)
        {
            var seedKey = CycleSeed(cycle);
            if (seedKey is null || !seeds.Add(seedKey)) continue;

            var seedChange = phaseOrdered.FirstOrDefault(c =>
                string.Equals(keyOf[c], seedKey, StringComparison.OrdinalIgnoreCase) && SkeletonStep.CanSkeleton(c));
            if (seedChange is not null)
                skeletonPass.Add(SkeletonStep.For(seedChange));
            else
                // No change in this set builds the chosen seed (it pre-exists / not skeleton-able): don't
                // treat its in-edges as broken — fall back to ordering the cycle by phase only.
                seeds.Remove(seedKey);
        }

        var ordered = TopoSort(phaseOrdered, graph, keyOf, breakSeeds: seeds);

        return new DeploymentPlan(skeletonPass, ordered, hardCycles, softCycles);
    }

    /// <summary>The deterministic skeleton seed for a hard cycle: the lexicographically-smallest member key.</summary>
    private static string? CycleSeed(DependencyCycle cycle) =>
        cycle.Path.Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();

    /// <summary>
    /// Stable, phase-anchored topological sort. Kahn's algorithm with a ready-set ordered by
    /// (phase, original index) so that:
    ///   • a non-destructive change is only released once every Hard/Soft referent it depends on has been
    ///     emitted (dependency safety), and
    ///   • among ready changes the lowest phase / earliest original position wins (so with no binding edge
    ///     the output equals the stable phase order — the golden-script equivalence guarantee).
    /// A hard cycle is pre-broken by dropping every edge INTO a <paramref name="breakSeeds"/> node (its
    /// skeleton already ran), so the constraint graph the sort sees is acyclic; a residual soft-only cycle
    /// that would deadlock is broken by appending the leftovers in stable phase order (safe — Postgres
    /// accepts a soft-cycle's objects in any order).
    /// </summary>
    private static List<SchemaChange> TopoSort(
        List<SchemaChange> phaseOrdered,
        DependencyGraph graph,
        Dictionary<SchemaChange, string?> keyOf,
        HashSet<string> breakSeeds)
    {
        var n = phaseOrdered.Count;

        var rank = new Dictionary<SchemaChange, int>(ReferenceComparer);
        for (var i = 0; i < n; i++) rank[phaseOrdered[i]] = i;

        // Group changes by graph key so an edge key→key can connect the right change nodes. A key may map to
        // several changes (CreateTable + AddColumn on one table); an inbound edge constrains all of them,
        // which is safe — they all concern the same object.
        var byKey = new Dictionary<string, List<SchemaChange>>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in phaseOrdered)
        {
            var k = keyOf[c];
            if (k is null) continue;
            if (!byKey.TryGetValue(k, out var list)) byKey[k] = list = new List<SchemaChange>();
            list.Add(c);
        }

        // Constraint edges between CHANGES: if change X has key kx and the graph says kx depends on ky
        // (Hard or Soft), every non-destructive change that builds ky must run before X. Runtime edges are
        // skipped; DROPs are ordered by phase only (a drop must not wait on the thing it removes).
        var predecessors = new Dictionary<SchemaChange, HashSet<SchemaChange>>(ReferenceComparer);
        foreach (var c in phaseOrdered) predecessors[c] = new HashSet<SchemaChange>(ReferenceComparer);

        foreach (var x in phaseOrdered)
        {
            if (x.IsDestructive) continue;
            var kx = keyOf[x];
            if (kx is null || !graph.HasNode(kx)) continue;

            foreach (var edge in graph.OutgoingEdges(kx))
            {
                if (edge.Kind == DependencyKind.Runtime) continue;
                var ky = edge.ToKey;
                // The seed of a broken hard cycle had its skeleton emitted first, so the edge into its real
                // (final) change must be dropped — keeping it would re-introduce the cycle.
                if (breakSeeds.Contains(ky)) continue;
                if (!byKey.TryGetValue(ky, out var producers)) continue;
                foreach (var p in producers)
                {
                    if (ReferenceEquals(p, x) || p.IsDestructive) continue;
                    predecessors[x].Add(p);
                }
            }
        }

        var indegree = new Dictionary<SchemaChange, int>(ReferenceComparer);
        foreach (var kv in predecessors) indegree[kv.Key] = kv.Value.Count;
        var successors = new Dictionary<SchemaChange, List<SchemaChange>>(ReferenceComparer);
        foreach (var c in phaseOrdered) successors[c] = new List<SchemaChange>();
        foreach (var kv in predecessors)
            foreach (var p in kv.Value)
                successors[p].Add(kv.Key);

        var ready = new SortedSet<SchemaChange>(Comparer<SchemaChange>.Create((a, b) =>
        {
            if (a.Phase != b.Phase) return a.Phase.CompareTo(b.Phase);
            return rank[a].CompareTo(rank[b]);
        }));
        foreach (var c in phaseOrdered) if (indegree[c] == 0) ready.Add(c);

        var result = new List<SchemaChange>(n);
        while (ready.Count > 0)
        {
            var next = ready.Min!;
            ready.Remove(next);
            result.Add(next);
            foreach (var s in successors[next])
                if (--indegree[s] == 0) ready.Add(s);
        }

        // Residual soft-only cycle deadlocked Kahn: append leftovers in stable phase order (safe).
        if (result.Count < n)
        {
            var emitted = new HashSet<SchemaChange>(result, ReferenceComparer);
            foreach (var c in phaseOrdered)
                if (!emitted.Contains(c))
                    result.Add(c);
        }

        return result;
    }
}

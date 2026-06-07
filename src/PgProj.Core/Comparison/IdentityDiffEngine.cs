using System;
using System.Collections.Generic;
using System.Linq;
using PgProj.Core.Model;
using PgProj.Core.Model.Identity;

namespace PgProj.Core.Comparison;

/// <summary>
/// The identity-based diff layer (Phase 11, issue #53). It pairs source and target objects by their
/// name-independent <see cref="StableId"/> (computed by <see cref="ObjectIdentityComputer"/>) so that an
/// object that only moved name is recognised as a single <em>Rename</em> instead of the Drop+Create the
/// name-keyed <see cref="SchemaComparer"/> would otherwise emit.
/// <para>
/// This is a thin pre-pass deliberately kept OUT of the existing per-kind CompareX methods: the comparer
/// asks it for the set of renames up front, emits those, and records each renamed pair's OLD (target) name
/// as "already present under the new name" so the normal create/drop walk treats the renamed object as
/// matched. The classic change types keep their IDs/ordering/SQL byte-identical, and the whole pre-pass is
/// only active when <see cref="ComparerOptions.DetectRenames"/> is set — so the default greenfield diff,
/// and therefore the golden artifacts, are unchanged.
/// </para>
/// </summary>
public sealed class IdentityDiffEngine
{
    private readonly ObjectIdentityComputer _ids = new();

    /// <summary>One detected rename: the object's kind, the target's (old) qualified name and the
    /// source's (new) qualified name. Qualified names are <c>schema.name</c>.</summary>
    public readonly record struct RenamePair(string Kind, string OldQualified, string NewQualified);

    /// <summary>
    /// The result of the rename pre-pass: the rename change records to emit, plus, per object collection,
    /// the OLD qualified names the comparer should consider "already matched" (so it neither drops the old
    /// nor recreates the new). Keyed case-insensitively to mirror <see cref="DatabaseModel.NameEquals"/>.
    /// </summary>
    public sealed class RenamePlan
    {
        public List<SchemaChange> Changes { get; } = new();
        // Old (target) qualified names that have been consumed by a rename, per kind discriminator.
        public Dictionary<string, HashSet<string>> MatchedOldByKind { get; } = new(StringComparer.Ordinal);
        // New (source) qualified names that have been satisfied by a rename, per kind discriminator.
        public Dictionary<string, HashSet<string>> MatchedNewByKind { get; } = new(StringComparer.Ordinal);

        public bool OldConsumed(string kind, string qualified) =>
            MatchedOldByKind.TryGetValue(kind, out var set) && set.Contains(qualified);
        public bool NewSatisfied(string kind, string qualified) =>
            MatchedNewByKind.TryGetValue(kind, out var set) && set.Contains(qualified);

        internal void Record(string kind, string oldQ, string newQ, SchemaChange change)
        {
            Changes.Add(change);
            (MatchedOldByKind.TryGetValue(kind, out var o) ? o : MatchedOldByKind[kind] = new(StringComparer.OrdinalIgnoreCase)).Add(oldQ);
            (MatchedNewByKind.TryGetValue(kind, out var n) ? n : MatchedNewByKind[kind] = new(StringComparer.OrdinalIgnoreCase)).Add(newQ);
        }
    }

    // Kind discriminators reused as plan keys (must match ObjectIdentity.Kind values for the finely-modelled
    // kinds we rename).
    public const string KindTable = "table";
    public const string KindSequence = "sequence";
    public const string KindIndex = "index";
    public const string KindView = "view";
    public const string KindFunction = "function";

    /// <summary>
    /// Detect pure renames across the finely-modelled kinds. A rename is a (source, target) pair with the
    /// same StableId and same CanonicalHash whose qualified names differ, where the source has NO same-name
    /// target (else it's an in-place object) and the target has NO same-name source (else it's a drop). When
    /// several candidates share a StableId, pairing is deterministic: by ascending name.
    /// </summary>
    public RenamePlan DetectRenames(DatabaseModel source, DatabaseModel target)
    {
        var plan = new RenamePlan();

        DetectFor(source.Tables, target.Tables, KindTable,
            t => t.QualifiedName, t => _ids.StableIdOf(t), t => _ids.CanonicalHashOf(t),
            (src, tgt) => new RenameTableChange(src.Schema, tgt.Name, src.Name), plan,
            sameSchema: (a, b) => DatabaseModel.NameEquals(a.Schema, b.Schema));

        DetectFor(source.Sequences, target.Sequences, KindSequence,
            q => $"{q.Schema}.{q.Name}", q => _ids.StableIdOf(q), q => _ids.CanonicalHashOf(q),
            (src, tgt) => new RenameSequenceChange(src.Schema, tgt.Name, src.Name), plan,
            sameSchema: (a, b) => DatabaseModel.NameEquals(a.Schema, b.Schema));

        DetectFor(source.Indexes, target.Indexes, KindIndex,
            i => $"{i.Schema}.{i.Name}", i => _ids.StableIdOf(i), i => _ids.CanonicalHashOf(i),
            (src, tgt) => new RenameIndexChange(src.Schema, tgt.Name, src.Name), plan,
            sameSchema: (a, b) => DatabaseModel.NameEquals(a.Schema, b.Schema));

        DetectFor(source.Views, target.Views, KindView,
            v => $"{v.Schema}.{v.Name}", v => _ids.StableIdOf(v), v => _ids.CanonicalHashOf(v),
            (src, tgt) => new RenameViewChange(src.Schema, tgt.Name, src.Name, src.IsMaterialized), plan,
            sameSchema: (a, b) => DatabaseModel.NameEquals(a.Schema, b.Schema));

        // Functions: the modelled body is the full CREATE statement, so it embeds the function's OWN name —
        // a pure rename therefore changes the body (and thus the CanonicalHash). Meaning-equality is judged
        // on the body with BOTH sides' own qualified name neutralised, so "same logic, moved name" is a
        // Rename rather than a full-body replace.
        DetectFor(source.Functions, target.Functions, KindFunction,
            f => $"{f.Schema}.{f.Name}", f => _ids.StableIdOf(f), f => _ids.CanonicalHashOf(f),
            (src, tgt) => new RenameFunctionChange(src.Schema, tgt.Name, src.Name, src.ArgTypes), plan,
            sameSchema: (a, b) => DatabaseModel.NameEquals(a.Schema, b.Schema),
            meaningEqual: (a, b) => FunctionBodyWithoutName(a) == FunctionBodyWithoutName(b));

        return plan;
    }

    // Pair source/target items of one kind by StableId, classify with the pure IdentityDiff rule, and
    // record the Rename pairs. Only objects with NO same-FQN counterpart on the other side are eligible —
    // a same-name match is handled by the existing in-place comparison path, never a rename.
    private void DetectFor<T>(
        IReadOnlyList<T> source, IReadOnlyList<T> target, string kind,
        Func<T, string> qualified, Func<T, StableId> stableId, Func<T, CanonicalHash> canonicalHash,
        Func<T, T, SchemaChange> makeChange, RenamePlan plan,
        Func<T, T, bool> sameSchema,
        Func<T, T, bool>? meaningEqual = null)
    {
        // Meaning-equality defaults to CanonicalHash identity; a kind whose modelled form embeds its own name
        // (functions) supplies a name-neutralised override so a pure rename still counts as "unchanged meaning".
        meaningEqual ??= (a, b) => canonicalHash(a) == canonicalHash(b);
        if (source.Count == 0 || target.Count == 0) return;

        var srcNames = new HashSet<string>(source.Select(qualified), StringComparer.OrdinalIgnoreCase);
        var tgtNames = new HashSet<string>(target.Select(qualified), StringComparer.OrdinalIgnoreCase);

        // Eligible = present on exactly one side by name (renames move a name out of the target into the source).
        var srcOnly = source.Where(s => !tgtNames.Contains(qualified(s)))
                            .OrderBy(qualified, StringComparer.Ordinal).ToList();
        var tgtOnly = target.Where(t => !srcNames.Contains(qualified(t)))
                            .OrderBy(qualified, StringComparer.Ordinal).ToList();
        if (srcOnly.Count == 0 || tgtOnly.Count == 0) return;

        // Index the target-only candidates by StableId (FIFO within an id, mirroring the ascending-name order).
        var byStable = new Dictionary<StableId, Queue<T>>();
        foreach (var t in tgtOnly)
            (byStable.TryGetValue(stableId(t), out var q) ? q : byStable[stableId(t)] = new Queue<T>()).Enqueue(t);

        foreach (var s in srcOnly)
        {
            if (!byStable.TryGetValue(stableId(s), out var queue) || queue.Count == 0) continue;
            // Peek the next same-StableId target; only a same-schema, hash-equal pair is a pure rename
            // (a hash difference is a Rename+Alter — left to the normal path so the alteration is scripted).
            var t = queue.Peek();
            if (!sameSchema(s, t)) continue;
            if (!meaningEqual(s, t)) continue;
            queue.Dequeue();
            plan.Record(kind, qualified(t), qualified(s), makeChange(s, t));
        }
    }

    // The function's canonical body with its OWN qualified name neutralised, so two same-signature functions
    // that differ only in name canonicalize equal (the rename criterion). The name token (schema.name and
    // bare name) is replaced with a fixed placeholder; the rest of the body must match for a pure rename.
    private static string FunctionBodyWithoutName(FunctionDefinition f)
    {
        var body = Canonicalizer.NormalizeBody(f.Body);
        var qn = (f.Schema + "." + f.Name).ToLowerInvariant();
        body = body.Replace(qn, "");
        // Also fold the bare name only when it isn't a substring of another identifier — guard with simple
        // word boundaries to avoid corrupting unrelated tokens.
        body = System.Text.RegularExpressions.Regex.Replace(
            body, @"(?<![\w.])" + System.Text.RegularExpressions.Regex.Escape(f.Name.ToLowerInvariant()) + @"(?![\w])", "");
        return body;
    }
}

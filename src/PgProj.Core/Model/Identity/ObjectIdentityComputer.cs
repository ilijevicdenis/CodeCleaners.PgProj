using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using PgProj.Core.Comparison;

namespace PgProj.Core.Model.Identity;

/// <summary>
/// Derives the identity triple (<see cref="ObjectId"/>/<see cref="StableId"/>/<see cref="CanonicalHash"/>)
/// for every record in a <see cref="DatabaseModel"/>, and for individual records on demand.
/// <para>
/// The computer is the keystone of the diff engine's Rename detection (issue #42). It computes the triple
/// IDENTICALLY for a project-built model and an introspected (live) model — both go through the same
/// <see cref="Comparison.Canonicalizer"/> and <see cref="TypeNormalizer"/>, so equivalent objects produce
/// equal StableId + CanonicalHash regardless of source.
/// </para>
/// <para>
/// Identity is computed on demand and returned as a side table (<see cref="ObjectIdentity"/> values keyed
/// however the caller likes) rather than mutated onto the records — that's what keeps the serialised model
/// (ModelJson) and its JSON contract field-set byte-identical.
/// </para>
/// </summary>
public sealed class ObjectIdentityComputer
{
    // ---- kind discriminators (stable strings; never reuse/renumber — they're hash inputs) ----------
    private const string KindSchema   = "schema";
    private const string KindTable    = "table";
    private const string KindIndex    = "index";
    private const string KindView     = "view";
    private const string KindSequence = "sequence";
    private const string KindFunction = "function";

    private int _nextId; // ObjectId allocator state; ids start at 1 (0 == ObjectId.None).

    private readonly CanonicalFormOptions _options;

    /// <summary>Default computer — positional column order, current behaviour. Goldens unchanged.</summary>
    public ObjectIdentityComputer() : this(CanonicalFormOptions.Default) { }

    /// <summary>
    /// Computer with explicit canonical-form options. The only knob today is
    /// <see cref="CanonicalFormOptions.IgnoreColumnOrder"/> (Phase-18 "ignore column order"); it is
    /// OFF in <see cref="CanonicalFormOptions.Default"/> so the default ctor is fully behaviour-preserving.
    /// </summary>
    public ObjectIdentityComputer(CanonicalFormOptions options) => _options = options ?? CanonicalFormOptions.Default;

    private ObjectId NextId() => new(++_nextId);

    /// <summary>
    /// Compute the identity triple for every object in <paramref name="model"/>, assigning a fresh
    /// <see cref="ObjectId"/> to each (in a deterministic order). The returned dictionary is keyed by
    /// object reference so a caller can look up the triple for any record it holds.
    /// </summary>
    public IReadOnlyDictionary<object, ObjectIdentity> ComputeAll(DatabaseModel model)
    {
        _nextId = 0;
        var map = new Dictionary<object, ObjectIdentity>(ReferenceEqualityComparer.Instance);

        foreach (var s in model.Schemas)   map[s] = Identify(s);
        foreach (var t in model.Tables)     map[t] = Identify(t);
        foreach (var i in model.Indexes)    map[i] = Identify(i);
        foreach (var v in model.Views)      map[v] = Identify(v);
        foreach (var q in model.Sequences)  map[q] = Identify(q);
        foreach (var f in model.Functions)  map[f] = Identify(f);
        foreach (var o in model.Objects)    map[o] = Identify(o);

        return map;
    }

    // ---- per-record identity (each assigns a fresh ObjectId) ---------------------------------------

    public ObjectIdentity Identify(SchemaDefinition s) =>
        new(NextId(), StableIdOf(s), CanonicalHashOf(s), KindSchema, s.Name);

    public ObjectIdentity Identify(TableDefinition t) =>
        new(NextId(), StableIdOf(t), CanonicalHashOf(t), KindTable, t.QualifiedName);

    public ObjectIdentity Identify(IndexDefinition i) =>
        new(NextId(), StableIdOf(i), CanonicalHashOf(i), KindIndex, $"{i.Schema}.{i.Name}");

    public ObjectIdentity Identify(ViewDefinition v) =>
        new(NextId(), StableIdOf(v), CanonicalHashOf(v), KindView, $"{v.Schema}.{v.Name}");

    public ObjectIdentity Identify(SequenceDefinition q) =>
        new(NextId(), StableIdOf(q), CanonicalHashOf(q), KindSequence, $"{q.Schema}.{q.Name}");

    public ObjectIdentity Identify(FunctionDefinition f) =>
        new(NextId(), StableIdOf(f), CanonicalHashOf(f), KindFunction, $"{f.Schema}.{f.Name}");

    public ObjectIdentity Identify(RawObjectDefinition o) =>
        new(NextId(), StableIdOf(o), CanonicalHashOf(o), RawKind(o.Kind), $"{o.Schema}.{o.Name}");

    // =================================================================================================
    //  StableId — name-INDEPENDENT structural fingerprint (so a pure rename preserves it)
    // =================================================================================================

    // A schema has no name-independent structural traits — an empty namespace is indistinguishable from
    // any other. Its only identity IS its name, so a schema rename is genuinely Drop+Create. Documented.
    public StableId StableIdOf(SchemaDefinition s) =>
        StableId.From(KindSchema, N(s.Name));

    // StableId fingerprint = the table's STRUCTURAL SKELETON: ordered column (name + canonical type +
    // nullability) plus its keys (PK / unique / FK). It deliberately EXCLUDES alterable, meaning-only
    // properties — DEFAULTs, identity/serial/generated specs, checks, trailing options — because those
    // can be ALTERed in place (ALTER COLUMN SET DEFAULT, …) without the table ceasing to be the same
    // table. Keeping them out is what lets the classifier say "same StableId, different CanonicalHash →
    // Alter" for a default change instead of misreading it as Drop+Create. Column NAMES stay in (they're
    // traits of the table, not the table's own name), so a column rename is a structural change. The FQN
    // is excluded, so a pure table rename preserves the StableId.
    public StableId StableIdOf(TableDefinition t)
    {
        var fp = new StringBuilder();
        foreach (var c in OrderColumns(t.Columns))
            fp.Append(Field(N(c.Name), TypeNormalizer.Normalize(c.DataType), c.IsNullable ? "null" : "notnull"))
              .Append('|');

        if (t.PrimaryKey is { } pk)
            fp.Append("pk:").Append(string.Join(",", pk.Columns.Select(N))).Append('|');

        // Unique/FK are SETS (order-insensitive): sort their canonical signatures.
        foreach (var sig in t.Unique.Select(u => "uq:" + string.Join(",", u.Columns.Select(N))).OrderBy(x => x, StringComparer.Ordinal))
            fp.Append(sig).Append('|');
        foreach (var sig in t.ForeignKeys.Select(ForeignKeyFingerprint).OrderBy(x => x, StringComparer.Ordinal))
            fp.Append(sig).Append('|');

        return StableId.From(KindTable, fp.ToString());
    }

    // The index's own NAME is excluded; the table it's on, its columns, uniqueness, method and predicate
    // are its structural traits, so renaming the index (only) preserves the StableId.
    public StableId StableIdOf(IndexDefinition i) =>
        StableId.From(KindIndex, Field(
            N(i.Schema), N(i.Table),
            string.Join(",", i.Columns.Select(c => N(c).Replace("\"", ""))),
            i.IsUnique ? "unique" : "",
            N(i.Method ?? "btree"),
            Canonicalizer.NormalizeText(i.WhereClause ?? "")));

    // A view's structure is its query body (the name is excluded), so renaming the view preserves the id.
    public StableId StableIdOf(ViewDefinition v) =>
        StableId.From(KindView, Field(v.IsMaterialized ? "mat" : "view", Canonicalizer.NormalizeBody(v.Body)));

    // A sequence's structural traits are its options. Two distinctly-named sequences with identical options
    // share a StableId (inherent — a sequence has no other intrinsic structure); the FQN check downstream
    // still classifies an actual rename correctly.
    public StableId StableIdOf(SequenceDefinition q) =>
        StableId.From(KindSequence, Field(
            q.DataType is null ? "" : TypeNormalizer.Normalize(q.DataType),
            q.Increment?.ToString() ?? "", q.MinValue?.ToString() ?? "", q.MaxValue?.ToString() ?? "",
            q.Start?.ToString() ?? "", q.Cache?.ToString() ?? "", q.Cycle ? "cycle" : ""));

    // A function's name-independent identity is its argument-type signature (Postgres's own overload key).
    // Renaming the function (keeping the arg types) preserves the StableId; changing the signature changes it.
    public StableId StableIdOf(FunctionDefinition f) =>
        StableId.From(KindFunction, NormalizeArgTypes(f.ArgTypes));

    // Raw objects: strip the object's own (possibly quoted) name from the canonical body so a rename is
    // name-independent where the body permits it; keep the kind + table scope + the rest of the body.
    public StableId StableIdOf(RawObjectDefinition o) =>
        StableId.From(RawKind(o.Kind), Field(
            N(o.OnObject ?? ""),
            o.BodyComparable ? StripName(Canonicalizer.NormalizeRawBody(o.Body), o.Name) : N(o.Identity)));

    // =================================================================================================
    //  CanonicalHash — semantic hash of the canonical form (changes ONLY when meaning changes)
    // =================================================================================================

    // Each CanonicalHash is now simply hash(kind + canonical form): the CanonicalFormOf(...) accessors
    // ARE the single source of truth for the meaning-only text, so IProjectObject.Canonicalize() and
    // Hash() can never drift apart (issue #51, point 3). The form string is prefixed with its kind, so
    // hashing it under the kind discriminator a second time is harmless and keeps the original framing.

    public CanonicalHash CanonicalHashOf(SchemaDefinition s) => CanonicalHash.From(KindSchema, CanonicalFormOf(s));

    // The table's meaning = its full structural fingerprint (same canonical inputs as StableId). The FQN
    // is excluded here too, so a pure rename leaves CanonicalHash unchanged → engine reports Rename (via
    // the FQN check), not Alter. A real column/key/default change flips the fingerprint and the hash.
    public CanonicalHash CanonicalHashOf(TableDefinition t) => CanonicalHash.From(KindTable, CanonicalFormOf(t));

    public CanonicalHash CanonicalHashOf(IndexDefinition i) => CanonicalHash.From(KindIndex, CanonicalFormOf(i));

    public CanonicalHash CanonicalHashOf(ViewDefinition v) => CanonicalHash.From(KindView, CanonicalFormOf(v));

    public CanonicalHash CanonicalHashOf(SequenceDefinition q) => CanonicalHash.From(KindSequence, CanonicalFormOf(q));

    // The function's meaning = its canonical body (the same NormalizeBody the comparer diffs on), so a
    // cosmetic reformat of the body leaves CanonicalHash unchanged but a logic change flips it. Arg types
    // are folded in so two overloads with cosmetically-identical bodies still hash distinctly.
    public CanonicalHash CanonicalHashOf(FunctionDefinition f) => CanonicalHash.From(KindFunction, CanonicalFormOf(f));

    // Raw object meaning = its canonical DDL body (identity-only kinds fall back to the identity token,
    // mirroring the comparer's "compares by identity only" rule).
    public CanonicalHash CanonicalHashOf(RawObjectDefinition o) => CanonicalHash.From(RawKind(o.Kind), CanonicalFormOf(o));

    // ---- shared canonical-form builders -----------------------------------------------------------

    private string TableCanonicalForm(TableDefinition t)
    {
        var fp = new StringBuilder();
        foreach (var c in OrderColumns(t.Columns))
            fp.Append(Field(N(c.Name), TypeNormalizer.Normalize(c.DataType), c.IsNullable ? "null" : "notnull",
                            c.IsIdentity ? "id:" + N(c.IdentityKind ?? "") : "",
                            c.IsSerial ? "serial" : "",
                            // Generated/CHECK/default are scalar EXPRESSIONS — fold redundant outer parens and
                            // operator spacing (#51) so `(a>0)` ≡ `a > 0` in the semantic hash.
                            c.GeneratedExpression is null ? "" : "gen:" + Canonicalizer.NormalizeExpression(c.GeneratedExpression),
                            c.IsSerial ? "" : "def:" + Canonicalizer.NormalizeExpression(c.Default)))
              .Append('|');
        if (t.PrimaryKey is { } pk)
            fp.Append("pk:").Append(string.Join(",", pk.Columns.Select(N))).Append('|');
        foreach (var sig in t.Unique.Select(u => "uq:" + string.Join(",", u.Columns.Select(N))).OrderBy(x => x, StringComparer.Ordinal))
            fp.Append(sig).Append('|');
        foreach (var sig in t.Checks.Select(c => "ck:" + Canonicalizer.NormalizeExpression(c.Expression)).OrderBy(x => x, StringComparer.Ordinal))
            fp.Append(sig).Append('|');
        foreach (var sig in t.ForeignKeys.Select(ForeignKeyFingerprint).OrderBy(x => x, StringComparer.Ordinal))
            fp.Append(sig).Append('|');
        foreach (var sig in t.OtherConstraints.Select(o => "other:" + Canonicalizer.NormalizeExpression(o)).OrderBy(x => x, StringComparer.Ordinal))
            fp.Append(sig).Append('|');
        if (!string.IsNullOrWhiteSpace(t.TrailingOptions))
            fp.Append("opts:").Append(Canonicalizer.NormalizeText(t.TrailingOptions)).Append('|');
        return fp.ToString();
    }

    // ---- public canonical-form accessors (the exact strings hashed; used by IProjectObject.Canonicalize) ----
    // Returning the canonical FORM (not just its hash) lets IProjectObject.Canonicalize() expose the same
    // meaning-only string CanonicalHash is derived from, so a column/expression is never left un-normalized
    // when TypeNormalizer would otherwise be bypassed. Prefixed with the kind so forms can't collide.

    public string CanonicalFormOf(SchemaDefinition s) => Field(KindSchema, N(s.Name));
    public string CanonicalFormOf(TableDefinition t) => Field(KindTable, TableCanonicalForm(t));
    public string CanonicalFormOf(IndexDefinition i) => Field(KindIndex,
        N(i.Schema), N(i.Table), string.Join(",", i.Columns.Select(c => N(c).Replace("\"", ""))),
        i.IsUnique ? "unique" : "", N(i.Method ?? "btree"), Canonicalizer.NormalizeExpression(i.WhereClause ?? ""));
    public string CanonicalFormOf(ViewDefinition v) => Field(KindView, v.IsMaterialized ? "mat" : "view", Canonicalizer.NormalizeBody(v.Body));
    public string CanonicalFormOf(SequenceDefinition q) => Field(KindSequence,
        q.DataType is null ? "" : TypeNormalizer.Normalize(q.DataType),
        q.Increment?.ToString() ?? "", q.MinValue?.ToString() ?? "", q.MaxValue?.ToString() ?? "",
        q.Start?.ToString() ?? "", q.Cache?.ToString() ?? "", q.Cycle ? "cycle" : "");
    public string CanonicalFormOf(FunctionDefinition f) => Field(KindFunction, NormalizeArgTypes(f.ArgTypes), Canonicalizer.NormalizeBody(f.Body));
    public string CanonicalFormOf(RawObjectDefinition o) => Field(RawKind(o.Kind),
        o.BodyComparable && !RawObjectMeta.ComparesByIdentityOnly(o.Kind)
            ? Field(N(o.OnObject ?? ""), Canonicalizer.NormalizeRawBody(o.Body))
            : N(o.Identity));

    // ---- small helpers ----------------------------------------------------------------------------

    // Honor the (gated, default-off) column-order normalization: when IgnoreColumnOrder is on, fold columns
    // in a stable canonical (by lower-cased name) order so a pure reorder hashes identically. Off by default,
    // columns are taken in declaration order — positional, matching the comparer / deploy script exactly.
    private IEnumerable<ColumnDefinition> OrderColumns(IReadOnlyList<ColumnDefinition> columns) =>
        _options.IgnoreColumnOrder ? columns.OrderBy(c => N(c.Name), StringComparer.Ordinal) : columns;

    // Identifier normalization: Postgres folds unquoted identifiers to lower case, and the model compares
    // names case-insensitively (DatabaseModel.NameEquals), so identity hashing lower-cases too.
    private static string N(string s) => (s ?? string.Empty).ToLowerInvariant();

    private const char Sep = ''; // ASCII Record Separator — frames sub-fields within one fingerprint piece.

    private static string Field(params string?[] parts) => string.Join(Sep, parts.Select(p => p ?? string.Empty));

    private static string ForeignKeyFingerprint(ForeignKeyDefinition fk) =>
        "fk:" + string.Join(",", fk.Columns.Select(N))
        + "->" + N(fk.ReferencedSchema) + "." + N(fk.ReferencedTable)
        + "(" + string.Join(",", fk.ReferencedColumns.Select(N)) + ")"
        + ":" + N(fk.OnDelete ?? "") + ":" + N(fk.OnUpdate ?? "");

    // Canonicalize an argument-type list: split on commas, normalize each type, lower-case, rejoin. So
    // "INT, text" and "integer,TEXT" hash equal (project file vs catalog spelling), but order matters
    // (it's positional in Postgres).
    private static string NormalizeArgTypes(string argTypes)
    {
        if (string.IsNullOrWhiteSpace(argTypes)) return "";
        return string.Join(",", argTypes.Split(',').Select(a => TypeNormalizer.Normalize(a.Trim())));
    }

    // Remove the object's own name token (bare and double-quoted) from a normalized raw body, so a pure
    // rename of a raw object doesn't perturb its StableId where the body otherwise repeats the name.
    private static string StripName(string normalizedBody, string name)
    {
        if (string.IsNullOrEmpty(name)) return normalizedBody;
        var lname = N(name);
        return normalizedBody.Replace(lname, "").Replace("\"" + lname + "\"", "");
    }

    private static string RawKind(ObjectKind kind) => "raw:" + kind.ToString().ToLowerInvariant();
}

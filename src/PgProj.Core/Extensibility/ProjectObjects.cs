using PgProj.Core.Comparison;
using PgProj.Core.Model;
using PgProj.Core.Model.Identity;
using PgProj.Core.Versioning;

namespace PgProj.Core.Extensibility;

/// <summary>
/// Base adapter: turns one model record into an <see cref="IProjectObject"/> by delegating identity to
/// the shared #42 <see cref="ObjectIdentityComputer"/> (cached so the ObjectId is stable within the
/// registry) and the diff to the #42 <see cref="IdentityDiff"/> classifier. Subclasses supply only the
/// kind token, name, canonical basis, and DDL — the seams that genuinely vary per object kind.
/// </summary>
public abstract class ProjectObjectBase : IProjectObject
{
    protected readonly ObjectIdentityComputer Computer;
    private ObjectIdentity? _identity;

    protected ProjectObjectBase(ObjectIdentityComputer computer) => Computer = computer;

    public abstract string Kind { get; }
    public abstract string QualifiedName { get; }

    public ObjectIdentity Identity() => _identity ??= ComputeIdentity();
    protected abstract ObjectIdentity ComputeIdentity();

    public abstract string Canonicalize();
    public abstract CanonicalHash Hash();
    public abstract string GenerateSql(PostgresVersionProfile profile);

    /// <summary>Identity-level classification (Unchanged/Rename/Alter/Drop+Create); field-level deltas are #53.</summary>
    public IdentityDiffResult Diff(IProjectObject? other) =>
        other is null ? IdentityDiff.Create() : IdentityDiff.Classify(Identity(), other.Identity());
}

internal sealed class SchemaProjectObject(SchemaDefinition def, ObjectIdentityComputer computer) : ProjectObjectBase(computer)
{
    public override string Kind => "schema";
    public override string QualifiedName => def.Name;
    protected override ObjectIdentity ComputeIdentity() => Computer.Identify(def);
    public override string Canonicalize() => Canonicalizer.NormalizeText(GenerateSql(PostgresVersionProfile.Latest));
    public override CanonicalHash Hash() => Computer.CanonicalHashOf(def);
    public override string GenerateSql(PostgresVersionProfile profile) => $"CREATE SCHEMA IF NOT EXISTS {SqlEmitter.Quote(def.Name)};";
}

internal sealed class TableProjectObject(TableDefinition def, ObjectIdentityComputer computer) : ProjectObjectBase(computer)
{
    public override string Kind => "table";
    public override string QualifiedName => def.QualifiedName;
    protected override ObjectIdentity ComputeIdentity() => Computer.Identify(def);
    public override string Canonicalize() => Canonicalizer.NormalizeBody(GenerateSql(PostgresVersionProfile.Latest));
    public override CanonicalHash Hash() => Computer.CanonicalHashOf(def);
    public override string GenerateSql(PostgresVersionProfile profile) => SqlEmitter.CreateTable(def);
}

internal sealed class ViewProjectObject(ViewDefinition def, ObjectIdentityComputer computer) : ProjectObjectBase(computer)
{
    public override string Kind => "view";
    public override string QualifiedName => $"{def.Schema}.{def.Name}";
    protected override ObjectIdentity ComputeIdentity() => Computer.Identify(def);
    public override string Canonicalize() => Canonicalizer.NormalizeBody(GenerateSql(PostgresVersionProfile.Latest));
    public override CanonicalHash Hash() => Computer.CanonicalHashOf(def);
    public override string GenerateSql(PostgresVersionProfile profile) => SqlEmitter.CreateOrReplaceView(def);
}

internal sealed class IndexProjectObject(IndexDefinition def, ObjectIdentityComputer computer) : ProjectObjectBase(computer)
{
    public override string Kind => "index";
    public override string QualifiedName => $"{def.Schema}.{def.Name}";
    protected override ObjectIdentity ComputeIdentity() => Computer.Identify(def);
    public override string Canonicalize() => Canonicalizer.NormalizeBody(GenerateSql(PostgresVersionProfile.Latest));
    public override CanonicalHash Hash() => Computer.CanonicalHashOf(def);
    public override string GenerateSql(PostgresVersionProfile profile) => SqlEmitter.CreateIndex(def);
}

internal sealed class SequenceProjectObject(SequenceDefinition def, ObjectIdentityComputer computer) : ProjectObjectBase(computer)
{
    public override string Kind => "sequence";
    public override string QualifiedName => $"{def.Schema}.{def.Name}";
    protected override ObjectIdentity ComputeIdentity() => Computer.Identify(def);
    public override string Canonicalize() => Canonicalizer.NormalizeBody(GenerateSql(PostgresVersionProfile.Latest));
    public override CanonicalHash Hash() => Computer.CanonicalHashOf(def);
    public override string GenerateSql(PostgresVersionProfile profile) => SqlEmitter.CreateSequence(def);
}

internal sealed class FunctionProjectObject(FunctionDefinition def, ObjectIdentityComputer computer) : ProjectObjectBase(computer)
{
    public override string Kind => "function";
    public override string QualifiedName => $"{def.Schema}.{def.Name}";
    protected override ObjectIdentity ComputeIdentity() => Computer.Identify(def);
    public override string Canonicalize() => Canonicalizer.NormalizeBody(GenerateSql(PostgresVersionProfile.Latest));
    public override CanonicalHash Hash() => Computer.CanonicalHashOf(def);
    public override string GenerateSql(PostgresVersionProfile profile) => SqlEmitter.Function(def);
}

/// <summary>
/// Adapter for every <see cref="RawObjectDefinition"/> kind (extension, type, domain, trigger, policy,
/// operator, FDW, text-search, …) — the largest, most-extended set, and exactly the kinds the #44 "adding
/// a kind" pain targeted. One adapter covers them all because the raw mechanism is already uniform: the
/// body IS the create DDL, metadata comes from <see cref="ProjectObjectKind"/>.
/// </summary>
internal sealed class RawProjectObject(RawObjectDefinition def, ObjectIdentityComputer computer) : ProjectObjectBase(computer)
{
    public ObjectKind RawKind => def.Kind;
    public override string Kind => ProjectObjectKind.For(def.Kind).TypeToken;
    public override string QualifiedName => string.IsNullOrEmpty(def.Schema) ? def.Name : $"{def.Schema}.{def.Name}";
    protected override ObjectIdentity ComputeIdentity() => Computer.Identify(def);
    public override string Canonicalize() => Canonicalizer.NormalizeRawBody(def.Body);
    public override CanonicalHash Hash() => Computer.CanonicalHashOf(def);
    public override string GenerateSql(PostgresVersionProfile profile) => def.Body;
}

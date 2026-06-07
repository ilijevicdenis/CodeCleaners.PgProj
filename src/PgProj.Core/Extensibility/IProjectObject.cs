using System.Collections.Generic;
using PgProj.Core.Diagnostics;
using PgProj.Core.Model.Identity;
using PgProj.Core.Semantics;
using PgProj.Core.Versioning;

namespace PgProj.Core.Extensibility;

/// <summary>
/// The single extensibility contract every schema-object kind implements (issue #44). It unifies the
/// seams that were previously a manual, scattered 4+-file change per new kind — the <c>ObjectKind</c>
/// enum, a <c>ReadXxxAsync</c> in the introspection reader, <c>RawObjectMeta</c> (phase/drop/folder),
/// <c>SchemaCompareObjectType</c>, and ad-hoc <c>SqlEmitter</c> logic — behind one interface that
/// introspection, diff, and codegen drive through the <see cref="ProjectObjectRegistry"/> instead of
/// switch statements.
///
/// The contract is deliberately thin and DELEGATES to the foundations M2 already built, so wrapping an
/// existing kind changes no behavior:
/// <list type="bullet">
/// <item><see cref="Identity"/> / <see cref="Hash"/> / <see cref="Canonicalize"/> → the #42
///   <see cref="ObjectIdentityComputer"/> + <see cref="PgProj.Core.Comparison.Canonicalizer"/>.</item>
/// <item><see cref="Diff"/> → the #42 <see cref="IdentityDiff"/> classifier.</item>
/// <item><see cref="GenerateSql"/> → the existing emitters / catalog body, version-aware via the #43
///   <see cref="PostgresVersionProfile"/>.</item>
/// <item><see cref="Validate"/> → the #46 <see cref="SymbolTable"/> (reference resolution seam).</item>
/// </list>
/// </summary>
public interface IProjectObject
{
    /// <summary>The object-kind discriminator token (matches <c>SchemaCompareObjectType</c>).</summary>
    string Kind { get; }

    /// <summary>Schema-qualified display name (for diagnostics, file naming, and FQN-change detection).</summary>
    string QualifiedName { get; }

    /// <summary>The identity triple (ObjectId + StableId + CanonicalHash) — the keystone for diffing.</summary>
    ObjectIdentity Identity();

    /// <summary>The canonical form whose hash is the <see cref="CanonicalHash"/> — cosmetic-insensitive.</summary>
    string Canonicalize();

    /// <summary>The semantic hash; changes only when the object's meaning changes.</summary>
    CanonicalHash Hash();

    /// <summary>
    /// Classifies this object (source) against <paramref name="other"/> (target) using the identity
    /// triple: Unchanged / Rename / Alter / Drop+Create. Field-level deltas are a follow-on (#53).
    /// </summary>
    IdentityDiffResult Diff(IProjectObject? other);

    /// <summary>Version-aware DDL that (re)creates this object on a target running <paramref name="profile"/>.</summary>
    string GenerateSql(PostgresVersionProfile profile);

    /// <summary>
    /// Binds references against the symbol table and returns any validation diagnostics. The default
    /// implementation is a no-op seam; kinds that can resolve references override it. (Phase 5 #48 will
    /// deepen this into full type-safety / overload-resolution validation.)
    /// </summary>
    IReadOnlyList<Diagnostic> Validate(SymbolTable symbols) => System.Array.Empty<Diagnostic>();
}

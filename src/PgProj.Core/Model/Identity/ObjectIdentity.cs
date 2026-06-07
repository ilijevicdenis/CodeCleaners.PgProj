namespace PgProj.Core.Model.Identity;

/// <summary>
/// The identity triple attached to one object-model record, plus the two pieces of context the diff
/// classifier needs: the object's <see cref="Kind"/> discriminator and its current schema-qualified
/// name (<see cref="QualifiedName"/>).
/// <list type="bullet">
/// <item><see cref="ObjectId"/> — opaque handle, unique within one model instance, cheap equality.</item>
/// <item><see cref="StableId"/> — durable, name-independent structural identity (survives a rename).</item>
/// <item><see cref="CanonicalHash"/> — semantic hash of the canonical form (changes only on meaning).</item>
/// </list>
/// This is computed on demand by <see cref="ObjectIdentityComputer"/> and is deliberately NOT a member
/// of the serialised model records — keeping it out of <c>ModelJson</c> means the build artifact and the
/// JSON contract field-set stay byte-identical (issue #42 attaches identity without breaking the wire).
/// </summary>
public sealed record ObjectIdentity(
    ObjectId ObjectId,
    StableId StableId,
    CanonicalHash CanonicalHash,
    string Kind,
    string QualifiedName);

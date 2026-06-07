using System;

namespace PgProj.Core.Model.Identity;

/// <summary>
/// An opaque, cheap-equality handle for an object <em>within a single model instance</em>. It is a
/// build-time-assigned ordinal (see <see cref="ObjectIdentityComputer"/>) — NOT durable across builds
/// and NOT comparable between two different models. Use it as a dictionary key, a set member, or a
/// lightweight reference inside one diff run; use <see cref="StableId"/> when you need identity that
/// survives a rebuild or matches the same object across the project↔database boundary.
/// </summary>
/// <remarks>
/// A readonly struct wrapping an <see cref="int"/>: zero-allocation, value-equality, and a hash code
/// that's just the integer. <see cref="None"/> (value 0) is the unassigned sentinel.
/// </remarks>
public readonly struct ObjectId : IEquatable<ObjectId>
{
    /// <summary>The unassigned handle.</summary>
    public static readonly ObjectId None = default;

    /// <summary>The raw ordinal. 0 means unassigned (<see cref="None"/>); allocated ids start at 1.</summary>
    public int Value { get; }

    public ObjectId(int value) => Value = value;

    public bool IsNone => Value == 0;

    public bool Equals(ObjectId other) => Value == other.Value;
    public override bool Equals(object? obj) => obj is ObjectId o && Equals(o);
    public override int GetHashCode() => Value;
    public override string ToString() => IsNone ? "ObjectId(none)" : $"ObjectId({Value})";

    public static bool operator ==(ObjectId a, ObjectId b) => a.Equals(b);
    public static bool operator !=(ObjectId a, ObjectId b) => !a.Equals(b);
}

using System.Linq;
using PgProj.Core.Model;

namespace PgProj.Core.Comparison;

/// <summary>
/// Maps a <see cref="SchemaChange"/> to the <see cref="Semantics.Dependencies.DependencyGraph"/> node key of
/// the object it builds/alters/drops, so the <see cref="DeploymentPlanner"/> can look the change up in the
/// graph. The key format MUST match the graph builder's (<c>schema.name</c> lowercased for relations/views/
/// types; <c>schema.name(normalized,arg,types)</c> lowercased for functions).
///
/// <para>A change that does not correspond to a deploy-ordered graph node (a raw comment, an FK add — keyed to
/// its owning table, not an independent node, a sequence) returns <c>null</c>: it carries no graph edge and is
/// ordered by phase alone. That is intentional and safe — the phase layer already sequences those correctly.</para>
/// </summary>
internal static class ChangeKey
{
    public static string? Of(SchemaChange change) => change switch
    {
        CreateTableChange t            => Relation(t.Table.Schema, t.Table.Name),
        AddColumnChange a              => Relation(a.Schema, a.Table),
        AlterColumnChange a            => Relation(a.Schema, a.Table),
        ColumnOrderChange c            => Relation(c.Schema, c.Table),
        AlterTableStorageChange s      => Relation(s.Schema, s.Table),
        AddCheckConstraintChange c     => Relation(c.Schema, c.Table),
        AddRawTableConstraintChange r  => Relation(r.Schema, r.Table),
        AddPrimaryKeyChange p          => Relation(p.Schema, p.Table),
        DropTableChange d              => Relation(d.Schema, d.Name),
        DropColumnChange d             => Relation(d.Schema, d.Table),

        CreateOrReplaceViewChange v    => Relation(v.View.Schema, v.View.Name),
        RecreateMaterializedViewChange v => Relation(v.View.Schema, v.View.Name),
        DropViewChange v               => Relation(v.Schema, v.Name),

        CreateOrReplaceFunctionChange f => Function(f.Function),

        // Raw objects (types, domains, triggers, …): use the schema.name when both are present. A type the
        // graph tracks is keyed schema.name lowercased, which matches.
        CreateRawObjectChange r        => RawKey(r.Def),
        RecreateRawObjectChange r      => RawKey(r.Def),
        DropRawObjectChange r          => RawKey(r.Def),

        _ => null,
    };

    private static string Relation(string schema, string name) => $"{schema}.{name}".ToLowerInvariant();

    private static string Function(FunctionDefinition f) =>
        $"{f.Schema}.{f.Name}({NormalizeArgTypes(f.ArgTypes)})".ToLowerInvariant();

    private static string? RawKey(RawObjectDefinition def) =>
        string.IsNullOrEmpty(def.Schema) || string.IsNullOrEmpty(def.Name)
            ? null
            : $"{def.Schema}.{def.Name}".ToLowerInvariant();

    // Canonicalize an arg-type list the SAME way CatalogBuilder/DependencyGraphBuilder do, so the function
    // node key matches ("app.f(integer)").
    private static string NormalizeArgTypes(string argTypes)
    {
        if (string.IsNullOrWhiteSpace(argTypes)) return "";
        return string.Join(",", argTypes.Split(',').Select(a => TypeNormalizer.Normalize(a.Trim())));
    }
}

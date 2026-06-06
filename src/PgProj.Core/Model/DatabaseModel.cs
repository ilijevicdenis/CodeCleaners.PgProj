using System.Collections.Generic;

namespace PgProj.Core.Model;

/// <summary>
/// The in-memory representation of a Postgres database schema, whether it was produced by
/// parsing a project's .sql files or by introspecting a live server. This is the analogue of
/// SSDT's database model inside a .dacpac: the canonical thing we compare and deploy from.
/// </summary>
public sealed class DatabaseModel
{
    public List<SchemaDefinition> Schemas { get; } = new();
    public List<TableDefinition> Tables { get; } = new();
    public List<IndexDefinition> Indexes { get; } = new();
    public List<ViewDefinition> Views { get; } = new();
    public List<SequenceDefinition> Sequences { get; } = new();
    public List<FunctionDefinition> Functions { get; } = new();

    /// <summary>All object kinds handled by the generic raw-object mechanism (see <see cref="RawObjectDefinition"/>).</summary>
    public List<RawObjectDefinition> Objects { get; } = new();

    public RawObjectDefinition? FindObject(string identity) =>
        Objects.Find(o => string.Equals(o.Identity, identity, System.StringComparison.OrdinalIgnoreCase));

    // Manual index walks rather than LINQ FirstOrDefault/Any: ModelBuilder.EnsureSchema calls HasSchema
    // for every statement and the comparer calls FindTable per source table, so the per-call lambda-closure
    // + iterator allocations LINQ adds were pure gen0 churn on a hot path. Same O(n) scan, zero allocation.
    // (Tables/Schemas stay public mutable lists — callers Add/AddRange directly — so a dictionary index
    // can't be kept in sync without breaking that contract; the loop is the allocation-free win that can.)
    public TableDefinition? FindTable(string schema, string name)
    {
        for (int i = 0; i < Tables.Count; i++)
        {
            var t = Tables[i];
            if (NameEquals(t.Schema, schema) && NameEquals(t.Name, name)) return t;
        }
        return null;
    }

    public bool HasSchema(string name)
    {
        for (int i = 0; i < Schemas.Count; i++)
            if (NameEquals(Schemas[i].Name, name)) return true;
        return false;
    }

    /// <summary>
    /// Postgres folds unquoted identifiers to lower case, so identifier comparison is
    /// case-insensitive throughout the model. (Quoted-identifier case preservation is a
    /// known future refinement — see BUGS.md.)
    /// </summary>
    public static bool NameEquals(string a, string b) =>
        string.Equals(a, b, System.StringComparison.OrdinalIgnoreCase);
}

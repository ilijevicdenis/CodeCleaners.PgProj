using System.Collections.Generic;
using System.Linq;

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

    public TableDefinition? FindTable(string schema, string name) =>
        Tables.FirstOrDefault(t => NameEquals(t.Schema, schema) && NameEquals(t.Name, name));

    public bool HasSchema(string name) =>
        Schemas.Any(s => NameEquals(s.Name, name));

    /// <summary>
    /// Postgres folds unquoted identifiers to lower case, so identifier comparison is
    /// case-insensitive throughout the model. (Quoted-identifier case preservation is a
    /// known future refinement — see BUGS.md.)
    /// </summary>
    public static bool NameEquals(string a, string b) =>
        string.Equals(a, b, System.StringComparison.OrdinalIgnoreCase);
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using PgProj.Core.Comparison;
using PgProj.Core.Model;
using PgProj.Core.Parsing;
using PgProj.Core.Syntax;

namespace PgProj.Core.Contracts;

/// <summary>
/// The engine half of the graphical table designer (epic EP-DESIGNER, issue #26). It is the single
/// source of truth for the designer's model&lt;-&gt;.sql round-trip, so the webview can never drift from
/// what <c>deploy</c> emits:
///
/// <list type="bullet">
/// <item><see cref="Describe"/> parses one table's <c>.sql</c> into the structured <see cref="TableModelDto"/>
///   (reusing the production <see cref="PgParser"/> + <see cref="ModelBuilder"/>).</item>
/// <item><see cref="Emit"/> turns a <see cref="TableModelDto"/> back into <c>.sql</c> using the EXISTING
///   <see cref="SqlEmitter"/> (the same emitter the deploy engine uses) — no bespoke SQL string-building.</item>
/// </list>
///
/// Companion statements that follow the CREATE TABLE in the source file (indexes the parser folds into the
/// model are handled explicitly; everything else — standalone <c>ALTER TABLE … ADD FOREIGN KEY</c>, RLS
/// <c>ENABLE ROW LEVEL SECURITY</c>, policies, comments) are carried verbatim in
/// <see cref="TableModelDto.Companions"/> so a round-trip never silently drops them.
/// </summary>
public static class TableDesigner
{
    /// <summary>
    /// Parses <paramref name="sql"/> (the full content of a table <c>.sql</c> file) and returns the
    /// structured designer model for the requested table. When <paramref name="qualifiedName"/> is null the
    /// first table found is used; otherwise the <c>schema.name</c> (or bare <c>name</c>) table is selected.
    /// Throws when no matching table exists. The model build uses <paramref name="defaultSchema"/> for
    /// unqualified objects, exactly like the project build.
    /// </summary>
    public static TableModelDto Describe(string sql, string? qualifiedName = null, string defaultSchema = "public")
    {
        var parse = new PgParser().Parse(sql);
        var model = new ModelBuilder(defaultSchema).Build(parse);

        var table = SelectTable(model, qualifiedName)
            ?? throw new InvalidOperationException(qualifiedName is null
                ? "No CREATE TABLE statement found in the source."
                : $"Table '{qualifiedName}' not found in the source.");

        var indexes = model.Indexes
            .Where(i => DatabaseModel.NameEquals(i.Schema, table.Schema) && DatabaseModel.NameEquals(i.Table, table.Name))
            .ToList();

        // Fold standalone `ALTER TABLE <thistable> ADD … FOREIGN KEY …` statements into the table's FK list
        // (so FKs are first-class designer-editable AND always re-emitted through SqlEmitter — which also
        // makes the round-trip a fixed point: an inline FK emits as an ALTER, and that ALTER folds straight
        // back in on the next describe). Everything else stays a verbatim companion.
        var companions = CollectCompanions(parse, model, defaultSchema, table, indexes);

        return ToDto(table, indexes, companions);
    }

    /// <summary>
    /// Emits the <c>.sql</c> for a designer table, byte-for-byte through <see cref="SqlEmitter"/> using the
    /// same per-table assembly as <see cref="DdlExporter"/> (CREATE TABLE, then each FK as an
    /// <c>ALTER TABLE … ADD</c>, then each CREATE INDEX), followed by any preserved companion statements.
    /// This keeps all SQL generation inside the engine's single emitter.
    /// </summary>
    public static string Emit(TableModelDto dto)
    {
        var (table, indexes) = ToModel(dto);

        var sb = new StringBuilder();
        sb.AppendLine(SqlEmitter.CreateTable(table));
        foreach (var fk in table.ForeignKeys)
        {
            sb.AppendLine();
            sb.AppendLine(SqlEmitter.ForeignKey(table.Schema, table.Name, fk));
        }
        foreach (var ix in indexes)
        {
            sb.AppendLine();
            sb.AppendLine(SqlEmitter.CreateIndex(ix));
        }
        foreach (var companion in dto.Companions)
        {
            var body = companion.TrimEnd();
            if (body.Length == 0) continue;
            if (!body.EndsWith(";", StringComparison.Ordinal)) body += ";";
            sb.AppendLine();
            sb.AppendLine(body);
        }
        return sb.ToString();
    }

    // ---- model <-> dto -----------------------------------------------------------------------------

    /// <summary>Maps the engine <see cref="TableDefinition"/> (+ its indexes/companions) to the wire DTO.</summary>
    public static TableModelDto ToDto(TableDefinition t, IReadOnlyList<IndexDefinition> indexes, IReadOnlyList<string> companions) => new()
    {
        Schema = t.Schema,
        Name = t.Name,
        Columns = t.Columns.Select(ToDto).ToList(),
        PrimaryKey = t.PrimaryKey is { } pk ? new DesignerKeyDto { Name = pk.Name, Columns = pk.Columns.ToList() } : null,
        Unique = t.Unique.Select(u => new DesignerKeyDto { Name = u.Name, Columns = u.Columns.ToList() }).ToList(),
        ForeignKeys = t.ForeignKeys.Select(ToDto).ToList(),
        Checks = t.Checks.Select(c => new DesignerCheckDto { Name = c.Name, Expression = c.Expression }).ToList(),
        Indexes = indexes.Select(ToDto).ToList(),
        OtherConstraints = t.OtherConstraints.ToList(),
        TrailingOptions = t.TrailingOptions,
        Companions = companions.ToList(),
    };

    /// <summary>Maps the wire DTO back to a <see cref="TableDefinition"/> + its standalone indexes.</summary>
    public static (TableDefinition Table, List<IndexDefinition> Indexes) ToModel(TableModelDto dto)
    {
        var t = new TableDefinition
        {
            Schema = dto.Schema,
            Name = dto.Name,
            TrailingOptions = dto.TrailingOptions,
        };
        foreach (var c in dto.Columns) t.Columns.Add(ToModel(c));
        if (dto.PrimaryKey is { } pk) t.PrimaryKey = new PrimaryKeyDefinition(pk.Name, pk.Columns.ToList());
        foreach (var u in dto.Unique) t.Unique.Add(new UniqueConstraintDefinition(u.Name, u.Columns.ToList()));
        foreach (var fk in dto.ForeignKeys) t.ForeignKeys.Add(ToModel(fk));
        foreach (var ck in dto.Checks) t.Checks.Add(new CheckConstraintDefinition(ck.Name, ck.Expression));
        foreach (var oc in dto.OtherConstraints) t.OtherConstraints.Add(oc);

        var indexes = dto.Indexes
            .Select(ix => new IndexDefinition(ix.Name, dto.Schema, dto.Name, ix.Columns.ToList(), ix.Unique, ix.Method, ix.Where))
            .ToList();
        return (t, indexes);
    }

    private static DesignerColumnDto ToDto(ColumnDefinition c) => new()
    {
        Name = c.Name,
        DataType = c.DataType,
        Nullable = c.IsNullable,
        Default = c.Default,
        Identity = c.IsIdentity,
        IdentityKind = c.IdentityKind,
        Generated = c.GeneratedExpression,
        GeneratedStored = c.GeneratedIsStored,
        Serial = c.IsSerial,
    };

    private static ColumnDefinition ToModel(DesignerColumnDto c) => new(
        c.Name, c.DataType, c.Nullable, c.Default, c.Identity, c.IdentityKind, c.Generated, c.Serial, c.GeneratedStored);

    private static DesignerForeignKeyDto ToDto(ForeignKeyDefinition fk) => new()
    {
        Name = fk.Name,
        Columns = fk.Columns.ToList(),
        ReferencedSchema = fk.ReferencedSchema,
        ReferencedTable = fk.ReferencedTable,
        ReferencedColumns = fk.ReferencedColumns.ToList(),
        OnDelete = fk.OnDelete,
        OnUpdate = fk.OnUpdate,
    };

    private static ForeignKeyDefinition ToModel(DesignerForeignKeyDto fk) => new(
        fk.Name, fk.Columns.ToList(), fk.ReferencedSchema, fk.ReferencedTable, fk.ReferencedColumns.ToList(), fk.OnDelete, fk.OnUpdate);

    private static DesignerIndexDto ToDto(IndexDefinition ix) => new()
    {
        Name = ix.Name,
        Unique = ix.IsUnique,
        Columns = ix.Columns.ToList(),
        Method = ix.Method,
        Where = ix.WhereClause,
    };

    // ---- selection + companions --------------------------------------------------------------------

    private static TableDefinition? SelectTable(DatabaseModel model, string? qualifiedName)
    {
        if (qualifiedName is null) return model.Tables.FirstOrDefault();
        var dot = qualifiedName.IndexOf('.');
        if (dot > 0)
            return model.FindTable(qualifiedName[..dot], qualifiedName[(dot + 1)..]);
        return model.Tables.FirstOrDefault(t => DatabaseModel.NameEquals(t.Name, qualifiedName));
    }

    /// <summary>
    /// Everything in the file that is not the selected table, one of its (already-modelled) indexes, or a
    /// standalone <c>ALTER TABLE … ADD … FOREIGN KEY</c> for it (folded into the table's FK list) is a
    /// "companion": RLS enable, policies, comments, other tables, etc. Companions are kept verbatim by
    /// <c>SourceText</c> so an edit-and-save never drops them and the file round-trips losslessly. The
    /// selected table's own CREATE TABLE and its CREATE INDEX statements are excluded (re-emitted from the
    /// model). May mutate <paramref name="table"/> by appending folded foreign keys.
    /// </summary>
    private static List<string> CollectCompanions(
        ParseResult parse, DatabaseModel model, string defaultSchema,
        TableDefinition table, IReadOnlyList<IndexDefinition> tableIndexes)
    {
        var indexNames = new HashSet<string>(tableIndexes.Select(i => i.Name), StringComparer.OrdinalIgnoreCase);
        var companions = new List<string>();

        foreach (var stmt in parse.Statements)
        {
            switch (stmt)
            {
                // The selected table's own CREATE TABLE — re-emitted from the model, never a companion.
                case CreateTableStatement ct
                    when DatabaseModel.NameEquals(Sch(ct.Schema, defaultSchema), table.Schema)
                         && DatabaseModel.NameEquals(ct.Name, table.Name):
                    continue;
                // The selected table's own indexes — re-emitted from the model.
                case CreateIndexStatement ci
                    when DatabaseModel.NameEquals(Sch(ci.Schema, defaultSchema), table.Schema)
                         && DatabaseModel.NameEquals(ci.Table, table.Name)
                         && ci.Name is not null && indexNames.Contains(ci.Name):
                    continue;
                // A standalone ALTER TABLE … ADD CONSTRAINT for this table is now folded into the table model by
                // ModelBuilder (#153) — so it is already on `table` and re-emitted through SqlEmitter. Skip it
                // here (do NOT re-add — that double-counts) so the round-trip stays a fixed point.
                case AlterStatement alter
                    when alter.ObjectKind == "TABLE"
                         && DatabaseModel.NameEquals(Sch(alter.Schema, defaultSchema), table.Schema)
                         && DatabaseModel.NameEquals(alter.Name, table.Name)
                         && alter.AddedConstraints.Count > 0:
                    continue;
                default:
                    var text = stmt.SourceText?.Trim();
                    if (!string.IsNullOrEmpty(text)) companions.Add(text!);
                    continue;
            }
        }
        return companions;
    }

    private static string Sch(string? s, string defaultSchema) => string.IsNullOrEmpty(s) ? defaultSchema : s!;
}

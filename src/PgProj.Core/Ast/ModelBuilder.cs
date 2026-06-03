using System.Collections.Generic;
using System.Linq;
using PgProj.Core.Model;

namespace PgProj.Core.Ast;

/// <summary>
/// Lowers a parsed <see cref="SqlScript"/> AST into the <see cref="DatabaseModel"/> the comparer
/// and emitter consume. This is the bridge that lets a single parser (<see cref="Parsing.AstParser"/>)
/// feed both static analysis (which walks the AST) and the diff/deploy pipeline (which uses the model).
/// </summary>
public sealed class ModelBuilder
{
    public DatabaseModel Build(SqlScript script)
    {
        var model = new DatabaseModel();
        Build(script, model);
        return model;
    }

    /// <summary>Accumulates the script's objects into an existing model (multi-file builds).</summary>
    public void Build(SqlScript script, DatabaseModel model)
    {
        foreach (var stmt in script.Statements)
        {
            switch (stmt)
            {
                case CreateSchemaStatement s: EnsureSchema(model, s.Name); break;
                case CreateTableStatement s: AddTable(model, s); break;
                case CreateIndexStatement s: AddIndex(model, s); break;
                case CreateViewStatement s: AddView(model, s); break;
                case CreateSequenceStatement s: AddSequence(model, s); break;
                case CreateFunctionStatement s: AddFunction(model, s); break;
                case RawStatement s: AddRaw(model, s); break;
            }
        }
    }

    private static void AddTable(DatabaseModel model, CreateTableStatement s)
    {
        var table = new TableDefinition { Schema = s.Schema, Name = s.Name, TrailingOptions = s.TrailingOptions };

        foreach (var col in s.Columns)
        {
            var nullable = !col.Type.IsSerial; // serial implies NOT NULL
            string? def = null, idKind = null, generated = null;
            var identity = false;

            foreach (var c in col.Constraints)
            {
                switch (c)
                {
                    case NotNullConstraintNode: nullable = false; break;
                    case NullConstraintNode: nullable = true; break;
                    case DefaultConstraintNode d: def = d.RawText; break;
                    case InlinePrimaryKeyNode: table.PrimaryKey = new PrimaryKeyDefinition(null, new[] { col.Name }); nullable = false; break;
                    case InlineUniqueNode: table.Unique.Add(new UniqueConstraintDefinition(null, new[] { col.Name })); break;
                    case InlineReferencesNode r:
                        table.ForeignKeys.Add(new ForeignKeyDefinition(null, new[] { col.Name }, r.RefSchema, r.RefTable, r.RefColumns, r.OnDelete, r.OnUpdate));
                        break;
                    case IdentityConstraintNode id: identity = true; idKind = id.Kind; break;
                    case GeneratedConstraintNode g: generated = g.RawText; break;
                    case CheckColumnConstraintNode ch: table.Checks.Add(new CheckConstraintDefinition(ch.Name, ch.RawText)); break;
                }
            }

            table.Columns.Add(new ColumnDefinition(
                col.Name, col.Type.Normalized, nullable, def, identity, idKind, generated, col.Type.IsSerial));
        }

        foreach (var tc in s.Constraints)
        {
            switch (tc)
            {
                case PrimaryKeyConstraintNode pk: table.PrimaryKey = new PrimaryKeyDefinition(pk.Name, pk.Columns); break;
                case UniqueConstraintNode u: table.Unique.Add(new UniqueConstraintDefinition(u.Name, u.Columns)); break;
                case ForeignKeyConstraintNode fk:
                    table.ForeignKeys.Add(new ForeignKeyDefinition(fk.Name, fk.Columns, fk.RefSchema, fk.RefTable, fk.RefColumns, fk.OnDelete, fk.OnUpdate));
                    break;
                case CheckConstraintNode ch: table.Checks.Add(new CheckConstraintDefinition(ch.Name, ch.RawText)); break;
                case RawConstraintNode rc: table.OtherConstraints.Add(rc.Text); break;
            }
        }

        EnsureSchema(model, s.Schema);
        model.Tables.Add(table);
    }

    private static void AddIndex(DatabaseModel model, CreateIndexStatement s)
    {
        model.Indexes.Add(new IndexDefinition(s.Name, s.Schema, s.Table, s.Columns, s.Unique, s.Method, s.Where));
        EnsureSchema(model, s.Schema);
    }

    private static void AddView(DatabaseModel model, CreateViewStatement s)
    {
        model.Views.Add(new ViewDefinition(s.Schema, s.Name, s.BodyText, s.Materialized));
        EnsureSchema(model, s.Schema);
    }

    private static void AddSequence(DatabaseModel model, CreateSequenceStatement s)
    {
        model.Sequences.Add(new SequenceDefinition(s.Schema, s.Name, s.DataType, s.Increment, s.MinValue, s.MaxValue, s.Start, s.Cache, s.Cycle));
        EnsureSchema(model, s.Schema);
    }

    private static void AddFunction(DatabaseModel model, CreateFunctionStatement s)
    {
        var h = s.Header;
        var signature = $"{h.Schema}.{h.Name}({h.ArgTypes})";
        model.Functions.Add(new FunctionDefinition(h.Schema, h.Name, signature, s.RawText, h.ArgTypes));
        EnsureSchema(model, h.Schema);
    }

    private static void AddRaw(DatabaseModel model, RawStatement s)
    {
        model.Objects.Add(new RawObjectDefinition(s.Kind, s.Schema, s.Name, s.Identity, s.BodyText, s.OnObject, s.BodyComparable));
        if (!string.IsNullOrEmpty(s.Schema)) EnsureSchema(model, s.Schema);
    }

    private static void EnsureSchema(DatabaseModel model, string schema)
    {
        if (!string.IsNullOrEmpty(schema) && !model.HasSchema(schema))
            model.Schemas.Add(new SchemaDefinition(schema));
    }
}

using System.Collections.Generic;
using System.Text;
using PgProj.Core.Model;

namespace PgProj.Core.Comparison;

/// <summary>
/// One atomic step in a deployment plan. <see cref="Phase"/> drives ordering so the generated
/// script is always dependency-safe: drop foreign keys first, create schemas/tables before the
/// things that reference them, add foreign keys only once every table exists, and leave the
/// destructive drops for last.
/// </summary>
public abstract record SchemaChange
{
    public abstract int Phase { get; }
    public abstract bool IsDestructive { get; }
    public abstract string Describe();
    public abstract string ToSql();
}

public sealed record CreateSchemaChange(string Schema) : SchemaChange
{
    public override int Phase => 10;
    public override bool IsDestructive => false;
    public override string Describe() => $"Create schema {Schema}";
    public override string ToSql() => $"CREATE SCHEMA IF NOT EXISTS {SqlEmitter.Quote(Schema)};";
}

public sealed record CreateSequenceChange(SequenceDefinition Sequence) : SchemaChange
{
    public override int Phase => 20;
    public override bool IsDestructive => false;
    public override string Describe() => $"Create sequence {Sequence.Schema}.{Sequence.Name}";
    public override string ToSql() => $"CREATE SEQUENCE IF NOT EXISTS {SqlEmitter.Qualified(Sequence.Schema, Sequence.Name)};";
}

public sealed record DropForeignKeyChange(string Schema, string Table, string Name) : SchemaChange
{
    public override int Phase => 30;
    public override bool IsDestructive => true;
    public override string Describe() => $"Drop foreign key {Name} on {Schema}.{Table}";
    public override string ToSql() => $"ALTER TABLE {SqlEmitter.Qualified(Schema, Table)} DROP CONSTRAINT {SqlEmitter.Quote(Name)};";
}

public sealed record DropViewChange(string Schema, string Name) : SchemaChange
{
    public override int Phase => 35;
    public override bool IsDestructive => true;
    public override string Describe() => $"Drop view {Schema}.{Name}";
    public override string ToSql() => $"DROP VIEW IF EXISTS {SqlEmitter.Qualified(Schema, Name)};";
}

public sealed record CreateTableChange(TableDefinition Table) : SchemaChange
{
    public override int Phase => 40;
    public override bool IsDestructive => false;
    public override string Describe() => $"Create table {Table.Schema}.{Table.Name}";
    public override string ToSql() => SqlEmitter.CreateTable(Table);
}

public sealed record AddColumnChange(string Schema, string Table, ColumnDefinition Column) : SchemaChange
{
    public override int Phase => 45;
    public override bool IsDestructive => false;
    public override string Describe() => $"Add column {Column.Name} to {Schema}.{Table}";
    public override string ToSql() => $"ALTER TABLE {SqlEmitter.Qualified(Schema, Table)} ADD COLUMN {SqlEmitter.Column(Column)};";
}

public sealed record AlterColumnChange(string Schema, string Table, ColumnDefinition Old, ColumnDefinition New) : SchemaChange
{
    public override int Phase => 50;
    public override bool IsDestructive => false;
    public override string Describe() => $"Alter column {New.Name} on {Schema}.{Table}";

    public override string ToSql()
    {
        var qn = SqlEmitter.Qualified(Schema, Table);
        var col = SqlEmitter.Quote(New.Name);
        var sb = new StringBuilder();

        if (Old.DataType != New.DataType)
            sb.AppendLine($"ALTER TABLE {qn} ALTER COLUMN {col} TYPE {New.DataType};");

        if (Old.IsNullable && !New.IsNullable)
            sb.AppendLine($"ALTER TABLE {qn} ALTER COLUMN {col} SET NOT NULL;");
        else if (!Old.IsNullable && New.IsNullable)
            sb.AppendLine($"ALTER TABLE {qn} ALTER COLUMN {col} DROP NOT NULL;");

        var oldDef = Old.Default ?? string.Empty;
        var newDef = New.Default ?? string.Empty;
        if (oldDef != newDef)
        {
            sb.AppendLine(string.IsNullOrWhiteSpace(newDef)
                ? $"ALTER TABLE {qn} ALTER COLUMN {col} DROP DEFAULT;"
                : $"ALTER TABLE {qn} ALTER COLUMN {col} SET DEFAULT {New.Default};");
        }

        return sb.ToString().TrimEnd();
    }
}

public sealed record DropPrimaryKeyChange(string Schema, string Table, string Name) : SchemaChange
{
    public override int Phase => 52;
    public override bool IsDestructive => true;
    public override string Describe() => $"Drop primary key on {Schema}.{Table}";
    public override string ToSql() => $"ALTER TABLE {SqlEmitter.Qualified(Schema, Table)} DROP CONSTRAINT {SqlEmitter.Quote(Name)};";
}

public sealed record AddPrimaryKeyChange(string Schema, string Table, PrimaryKeyDefinition Pk) : SchemaChange
{
    public override int Phase => 54;
    public override bool IsDestructive => false;
    public override string Describe() => $"Add primary key on {Schema}.{Table}";

    public override string ToSql()
    {
        var prefix = string.IsNullOrEmpty(Pk.Name) ? string.Empty : $"CONSTRAINT {SqlEmitter.Quote(Pk.Name!)} ";
        return $"ALTER TABLE {SqlEmitter.Qualified(Schema, Table)} ADD {prefix}PRIMARY KEY ({SqlEmitter.Cols(Pk.Columns)});";
    }
}

public sealed record DropIndexChange(string Schema, string Name) : SchemaChange
{
    public override int Phase => 60;
    public override bool IsDestructive => true;
    public override string Describe() => $"Drop index {Schema}.{Name}";
    public override string ToSql() => $"DROP INDEX IF EXISTS {SqlEmitter.Qualified(Schema, Name)};";
}

public sealed record CreateIndexChange(IndexDefinition Index) : SchemaChange
{
    public override int Phase => 65;
    public override bool IsDestructive => false;
    public override string Describe() => $"Create index {Index.Schema}.{Index.Name}";
    public override string ToSql() => SqlEmitter.CreateIndex(Index);
}

public sealed record AddForeignKeyChange(TableDefinition Table, ForeignKeyDefinition ForeignKey) : SchemaChange
{
    public override int Phase => 70;
    public override bool IsDestructive => false;
    public override string Describe() => $"Add foreign key on {Table.Schema}.{Table.Name} -> {ForeignKey.ReferencedSchema}.{ForeignKey.ReferencedTable}";
    public override string ToSql() => SqlEmitter.ForeignKey(Table.Schema, Table.Name, ForeignKey);
}

public sealed record CreateOrReplaceViewChange(ViewDefinition View) : SchemaChange
{
    public override int Phase => 75;
    public override bool IsDestructive => false;
    public override string Describe() => $"Create or replace view {View.Schema}.{View.Name}";
    public override string ToSql() => SqlEmitter.CreateOrReplaceView(View);
}

public sealed record CreateOrReplaceFunctionChange(FunctionDefinition Function) : SchemaChange
{
    public override int Phase => 80;
    public override bool IsDestructive => false;
    public override string Describe() => $"Create or replace function {Function.Signature}";
    public override string ToSql() => SqlEmitter.Function(Function);
}

public sealed record DropColumnChange(string Schema, string Table, string Column) : SchemaChange
{
    public override int Phase => 90;
    public override bool IsDestructive => true;
    public override string Describe() => $"Drop column {Column} from {Schema}.{Table}";
    public override string ToSql() => $"ALTER TABLE {SqlEmitter.Qualified(Schema, Table)} DROP COLUMN {SqlEmitter.Quote(Column)};";
}

public sealed record DropTableChange(string Schema, string Name) : SchemaChange
{
    public override int Phase => 95;
    public override bool IsDestructive => true;
    public override string Describe() => $"Drop table {Schema}.{Name}";
    public override string ToSql() => $"DROP TABLE IF EXISTS {SqlEmitter.Qualified(Schema, Name)};";
}

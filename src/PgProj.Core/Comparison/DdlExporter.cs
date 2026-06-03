using System.Collections.Generic;
using System.Linq;
using System.Text;
using PgProj.Core.Model;

namespace PgProj.Core.Comparison;

/// <summary>
/// Serialises a <see cref="DatabaseModel"/> back into a tree of .sql files laid out like a
/// database project (Schemas/, Tables/, Views/, Functions/, Sequences/). This is what powers
/// <c>extract</c>: point it at a live server and get a buildable project back, the inverse of the
/// project build. One object per file, mirroring SSDT's extract layout.
/// </summary>
public static class DdlExporter
{
    public static IReadOnlyDictionary<string, string> ExportFiles(DatabaseModel model)
    {
        var files = new Dictionary<string, string>();

        foreach (var s in model.Schemas.Where(s => !DatabaseModel.NameEquals(s.Name, "public")))
            files[$"Schemas/{s.Name}.sql"] = $"CREATE SCHEMA IF NOT EXISTS {SqlEmitter.Quote(s.Name)};\n";

        foreach (var seq in model.Sequences)
            files[$"Sequences/{seq.Schema}.{seq.Name}.sql"] =
                $"CREATE SEQUENCE IF NOT EXISTS {SqlEmitter.Qualified(seq.Schema, seq.Name)};\n";

        foreach (var t in model.Tables)
        {
            var sb = new StringBuilder();
            sb.AppendLine(SqlEmitter.CreateTable(t));
            foreach (var fk in t.ForeignKeys)
            {
                sb.AppendLine();
                sb.AppendLine(SqlEmitter.ForeignKey(t.Schema, t.Name, fk));
            }
            foreach (var ix in model.Indexes.Where(i =>
                DatabaseModel.NameEquals(i.Schema, t.Schema) && DatabaseModel.NameEquals(i.Table, t.Name)))
            {
                sb.AppendLine();
                sb.AppendLine(SqlEmitter.CreateIndex(ix));
            }
            files[$"Tables/{t.Schema}.{t.Name}.sql"] = sb.ToString();
        }

        foreach (var v in model.Views)
            files[$"Views/{v.Schema}.{v.Name}.sql"] = SqlEmitter.CreateOrReplaceView(v) + "\n";

        foreach (var f in model.Functions)
            files[$"Functions/{f.Schema}.{f.Name}.sql"] = SqlEmitter.Function(f) + "\n";

        return files;
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using PgProj.Core.Model;

namespace PgProj.Core.Comparison;

/// <summary>
/// Serialises a <see cref="DatabaseModel"/> back into a tree of .sql files laid out like a
/// database project, using SSDT's "Schema\Object Type" folder structure: each schema is a
/// top-level folder containing per-kind subfolders (<c>app/Tables/customer.sql</c>,
/// <c>app/Views/listing.sql</c>, <c>app/Procedures/refresh.sql</c>, …); schema-less objects
/// (extensions, FDWs, casts, …) keep a root-level kind folder. This is what powers
/// <c>extract</c> and the VS Import Database dialog: point it at a live server and get a
/// buildable project back, the inverse of the project build. One object per file.
/// </summary>
public static class DdlExporter
{
    public static IReadOnlyDictionary<string, string> ExportFiles(DatabaseModel model)
    {
        var files = new Dictionary<string, string>();

        foreach (var s in model.Schemas.Where(s => !DatabaseModel.NameEquals(s.Name, "public")))
            files[SchemaUnit(s.Name)] = $"CREATE SCHEMA IF NOT EXISTS {SqlEmitter.Quote(s.Name)};\n";

        foreach (var seq in model.Sequences)
            files[SequenceUnit(seq.Schema, seq.Name)] =
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
            files[TableUnit(t.Schema, t.Name)] = sb.ToString();
        }

        foreach (var v in model.Views)
            files[ViewUnit(v.Schema, v.Name)] = SqlEmitter.CreateOrReplaceView(v) + "\n";

        foreach (var f in model.Functions)
            files[FunctionUnit(f)] = SqlEmitter.Function(f) + "\n";

        foreach (var o in model.Objects)
        {
            if (string.IsNullOrWhiteSpace(o.Body)) continue; // identity-only (existence) records
            var body = o.Body.TrimEnd();
            if (!body.EndsWith(";", StringComparison.Ordinal)) body += ";";
            files[RawUnit(o)] = body + "\n";
        }

        return files;
    }

    // ---- canonical unit paths -------------------------------------------------------------
    // The single source of truth for where an object's file unit lives. ReverseSync keys its
    // drift mapping on these same strings, so the two sides must never diverge.

    /// <summary>The schema's own CREATE SCHEMA unit, at the root of its folder.</summary>
    public static string SchemaUnit(string schema) => $"{schema}/{schema}.sql";

    public static string TableUnit(string schema, string name) => $"{schema}/Tables/{name}.sql";

    public static string ViewUnit(string schema, string name) => $"{schema}/Views/{name}.sql";

    public static string SequenceUnit(string schema, string name) => $"{schema}/Sequences/{name}.sql";

    /// <summary>Routes CREATE PROCEDURE bodies to Procedures/, everything else to Functions/.</summary>
    public static string FunctionUnit(FunctionDefinition f) =>
        $"{f.Schema}/{(IsProcedure(f) ? "Procedures" : "Functions")}/{f.Name}.sql";

    /// <summary>
    /// Schema-qualified raw objects nest under their schema folder (<c>app/Types/money.sql</c>);
    /// schema-less kinds (extensions, languages, FDWs, casts, event triggers, …) keep the
    /// root-level kind folder with the full identity-based file name.
    /// </summary>
    public static string RawUnit(RawObjectDefinition def) =>
        string.IsNullOrEmpty(def.Schema)
            ? $"{RawObjectMeta.Folder(def.Kind)}/{RawObjectMeta.FileName(def)}"
            : $"{def.Schema}/{RawObjectMeta.Folder(def.Kind)}/{RawObjectMeta.FileName(def, includeSchema: false)}";

    private static bool IsProcedure(FunctionDefinition f) => ProcedurePattern.IsMatch(f.Body ?? "");

    private static readonly Regex ProcedurePattern =
        new(@"^\s*CREATE\s+(OR\s+REPLACE\s+)?PROCEDURE\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
}

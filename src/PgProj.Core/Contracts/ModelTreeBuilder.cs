using System.Collections.Generic;
using PgProj.Core.Model;
using PgProj.Core.Project;

namespace PgProj.Core.Contracts;

/// <summary>
/// Flattens a built <see cref="DatabaseModel"/> into the editor's <see cref="ModelTreeDto"/>: one node
/// per object across <em>every</em> kind the model holds (schemas, tables + their columns, indexes,
/// views, sequences, functions, and every generic raw object — type/domain/trigger/policy/…), each
/// carrying its source anchor (file:line:col) for tree views and go-to-definition.
/// </summary>
public static class ModelTreeBuilder
{
    public static ModelTreeDto Build(DatabaseModel model, string projectName, SourcePositionIndex? positions = null)
    {
        var nodes = new List<ModelTreeNodeDto>();

        foreach (var s in model.Schemas)
            nodes.Add(Node("schema", s.Name, s.Name, s.Name, positions?.Find($"schema:{s.Name}".ToLowerInvariant())));

        foreach (var t in model.Tables)
        {
            var cols = new List<ModelTreeNodeDto>();
            foreach (var c in t.Columns)
                cols.Add(new ModelTreeNodeDto
                {
                    Kind = "column", Schema = t.Schema, Name = c.Name,
                    QualifiedName = $"{t.QualifiedName}.{c.Name} {c.DataType}",
                });
            nodes.Add(Node("table", t.Schema, t.Name, t.QualifiedName,
                positions?.Find($"table:{t.QualifiedName}".ToLowerInvariant()), cols));
        }

        foreach (var i in model.Indexes)
            nodes.Add(Node("index", i.Schema, i.Name, $"{i.Schema}.{i.Name}",
                positions?.Find($"index:{i.Schema}.{i.Name}".ToLowerInvariant())));

        foreach (var v in model.Views)
            nodes.Add(Node(v.IsMaterialized ? "materializedView" : "view", v.Schema, v.Name, $"{v.Schema}.{v.Name}",
                positions?.Find($"view:{v.Schema}.{v.Name}".ToLowerInvariant())));

        foreach (var q in model.Sequences)
            nodes.Add(Node("sequence", q.Schema, q.Name, $"{q.Schema}.{q.Name}",
                positions?.Find($"sequence:{q.Schema}.{q.Name}".ToLowerInvariant())));

        foreach (var f in model.Functions)
            nodes.Add(Node("function", f.Schema, f.Name, f.Signature,
                positions?.Find($"function:{f.Signature}".ToLowerInvariant())));

        foreach (var o in model.Objects)
            nodes.Add(Node(KindLabel(o.Kind), o.Schema, o.Name,
                string.IsNullOrEmpty(o.Schema) ? o.Name : $"{o.Schema}.{o.Name}",
                positions?.FindRaw(o.Schema, o.Name)));

        return new ModelTreeDto
        {
            Project = projectName,
            Summary = ContractMappers.SummaryOf(model),
            Nodes = nodes,
        };
    }

    private static ModelTreeNodeDto Node(string kind, string schema, string name, string qualified,
        SourcePosition? pos, IReadOnlyList<ModelTreeNodeDto>? children = null) => new()
    {
        Kind = kind,
        Schema = schema,
        Name = name,
        QualifiedName = qualified,
        File = pos?.File,
        Line = pos?.Line ?? 0,
        Col = pos?.Col ?? 0,
        Children = children ?? new List<ModelTreeNodeDto>(),
    };

    /// <summary>camelCase label for a raw object kind (e.g. <c>OperatorClass</c> → <c>operatorClass</c>).</summary>
    private static string KindLabel(ObjectKind kind)
    {
        var s = kind.ToString();
        return char.ToLowerInvariant(s[0]) + s[1..];
    }
}

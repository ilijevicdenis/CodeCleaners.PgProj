using System.Collections.Generic;
using PgProj.Core.Ast;
using PgProj.Core.Model;

namespace PgProj.Core.Parsing;

/// <summary>
/// The model-producing front door. It is now a thin facade over the single source of truth — the
/// <see cref="AstParser"/> (tokens → AST) plus <see cref="ModelBuilder"/> (AST → model). There is
/// exactly one parser in the system: static analysis walks the AST, and the diff/deploy pipeline
/// consumes the model lowered from that same AST.
/// </summary>
public sealed class SqlParser
{
    private readonly AstParser _ast;
    private readonly ModelBuilder _builder = new();

    public SqlParser(string defaultSchema = "public") => _ast = new AstParser(defaultSchema);

    /// <summary>Diagnostics accumulated across every <see cref="ParseInto"/> call on this instance.</summary>
    public List<string> Diagnostics => _ast.Diagnostics;

    public DatabaseModel Parse(string sql)
    {
        var model = new DatabaseModel();
        ParseInto(model, sql);
        return model;
    }

    public void ParseInto(DatabaseModel model, string sql) =>
        _builder.Build(_ast.Parse(sql), model);

    /// <summary>Parses to the AST directly (for analysis, without lowering to a model).</summary>
    public SqlScript ParseAst(string sql) => _ast.Parse(sql);
}

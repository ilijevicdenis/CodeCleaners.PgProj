using System;
using System.Collections.Generic;
using PgProj.Core.Ast;

namespace PgProj.Core.Parsing;

/// <summary>
/// Splits a function body (SQL or PL/pgSQL) into classified <see cref="BodyStatement"/> nodes so
/// safety rules can reason about what a function actually does: which DML it runs (and whether an
/// UPDATE/DELETE is unguarded), whether it builds dynamic SQL via EXECUTE, and whether it performs
/// schema mutation. Procedural constructs (BEGIN/IF/LOOP/assignments) are kept as opaque nodes —
/// we classify the SQL-bearing statements, not the full PL/pgSQL grammar.
/// </summary>
public static class FunctionBodyClassifier
{
    public static IReadOnlyList<BodyStatement> Classify(string body)
    {
        var result = new List<BodyStatement>();
        var tokens = Tokenizer.Tokenize(body ?? string.Empty);

        var current = new List<Token>();
        var depth = 0;
        foreach (var t in tokens)
        {
            if (t.IsSymbol('(')) depth++;
            else if (t.IsSymbol(')')) depth = Math.Max(0, depth - 1);

            if (t.IsSymbol(';') && depth == 0)
            {
                if (current.Count > 0) result.Add(ClassifyStatement(current));
                current = new List<Token>();
                continue;
            }
            current.Add(t);
        }
        if (current.Count > 0) result.Add(ClassifyStatement(current));
        return result;
    }

    private static readonly HashSet<string> Verbs = new(StringComparer.OrdinalIgnoreCase)
    {
        "SELECT", "INSERT", "UPDATE", "DELETE", "TRUNCATE", "PERFORM",
        "EXECUTE", "DROP", "ALTER", "CREATE", "GRANT", "REVOKE",
    };

    private static BodyStatement ClassifyStatement(List<Token> tokens)
    {
        var raw = Token.Render(tokens);
        var first = FirstVerb(tokens); // scans past BEGIN/DECLARE/IF/THEN/labels to the real verb

        switch (first)
        {
            case "SELECT":
            case "INSERT":
            case "UPDATE":
            case "DELETE":
            case "TRUNCATE":
            case "PERFORM":
                return new DmlStatementNode
                {
                    RawText = raw,
                    Verb = first,
                    TargetTable = ExtractTarget(tokens, first),
                    HasWhere = HasTopLevelWord(tokens, "WHERE"),
                };
            case "EXECUTE":
                return new DynamicSqlStatementNode { RawText = raw };
            case "DROP":
            case "ALTER":
            case "CREATE":
            case "GRANT":
            case "REVOKE":
                return new SchemaMutationStatementNode { RawText = raw, Verb = first };
            default:
                return new ProceduralStatementNode { RawText = raw };
        }
    }

    // The first SQL verb at paren depth 0, skipping block/procedural keywords (BEGIN, DECLARE,
    // IF/THEN/LOOP, labels, RETURN QUERY, WITH …) so "BEGIN UPDATE …" classifies as UPDATE.
    private static string FirstVerb(List<Token> tokens)
    {
        var depth = 0;
        foreach (var t in tokens)
        {
            if (t.IsSymbol('(')) { depth++; continue; }
            if (t.IsSymbol(')')) { depth = Math.Max(0, depth - 1); continue; }
            if (depth == 0 && t.Kind == TokenKind.Word && Verbs.Contains(t.Value))
                return t.Value.ToUpperInvariant();
        }
        return "";
    }

    private static bool HasTopLevelWord(List<Token> tokens, string word)
    {
        var depth = 0;
        foreach (var t in tokens)
        {
            if (t.IsSymbol('(')) depth++;
            else if (t.IsSymbol(')')) depth = Math.Max(0, depth - 1);
            else if (depth == 0 && t.IsWord(word)) return true;
        }
        return false;
    }

    private static string? ExtractTarget(List<Token> tokens, string verb)
    {
        // Best-effort: the identifier after UPDATE / DELETE FROM / INSERT INTO / TRUNCATE.
        for (var i = 0; i < tokens.Count; i++)
        {
            var up = tokens[i].Kind == TokenKind.Word ? tokens[i].Value.ToUpperInvariant() : null;
            var isAnchor = (verb == "UPDATE" && up == "UPDATE")
                        || (verb == "DELETE" && up == "FROM")
                        || (verb == "INSERT" && up == "INTO")
                        || (verb == "TRUNCATE" && up == "TRUNCATE");
            if (isAnchor && i + 1 < tokens.Count && tokens[i + 1].IsIdentifierLike)
                return tokens[i + 1].Value;
        }
        return null;
    }
}

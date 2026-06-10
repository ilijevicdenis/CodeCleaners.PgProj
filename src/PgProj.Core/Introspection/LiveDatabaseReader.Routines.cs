using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Npgsql;
using PgProj.Core.Model;

namespace PgProj.Core.Introspection;

/// <summary>Routine readers: functions, aggregates, triggers, rules, event triggers, procedural
/// languages, operators, and operator classes/families.</summary>
public sealed partial class LiveDatabaseReader
{
    private async Task<List<FunctionDefinition>> ReadFunctionsAsync(NpgsqlConnection conn, CancellationToken ct)
    {
        var sql = _q.Functions;

        var list = new List<FunctionDefinition>();
        await using var cmd = new NpgsqlCommand(sql, conn);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            var schema = r.GetString(0);
            var name = r.GetString(1);
            var args = r.IsDBNull(2) ? string.Empty : r.GetString(2);
            var def = r.GetString(3);
            var argTypes = string.Join(", ", args.Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(a => TypeNormalizer.Normalize(a.Trim())));
            list.Add(new FunctionDefinition(schema, name, $"{schema}.{name}({argTypes})", def, argTypes));
        }
        return list;
    }

    private async Task<List<RawObjectDefinition>> ReadAggregatesAsync(NpgsqlConnection conn, CancellationToken ct)
    {
        var sql = _q.Aggregates;

        var list = new List<RawObjectDefinition>();
        await using var cmd = new NpgsqlCommand(sql, conn);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            var schema = r.GetString(0);
            var name = r.GetString(1);
            var args = r.IsDBNull(2) || r.GetString(2).Length == 0 ? "*" : r.GetString(2);
            var opts = new List<string> { $"SFUNC = {r.GetString(3)}", $"STYPE = {r.GetString(4)}" };
            if (!r.IsDBNull(5)) opts.Add($"FINALFUNC = {r.GetString(5)}");
            if (!r.IsDBNull(6)) opts.Add($"COMBINEFUNC = {r.GetString(6)}");
            if (!r.IsDBNull(7)) opts.Add($"INITCOND = '{r.GetString(7).Replace("'", "''")}'");

            list.Add(MakeRaw(ObjectKind.Aggregate, schema, name, $"aggregate:{schema}.{name}({NormalizeArgs(args)})",
                $"CREATE AGGREGATE {schema}.{name} ({args}) ({string.Join(", ", opts)});"));
        }
        return list;
    }

    private static string NormalizeArgs(string args) => args.Trim();

    private async Task<List<RawObjectDefinition>> ReadTriggersAsync(NpgsqlConnection conn, CancellationToken ct)
    {
        var sql = _q.Triggers;

        var list = new List<RawObjectDefinition>();
        await using var cmd = new NpgsqlCommand(sql, conn);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            var schema = r.GetString(0);
            var on = $"{schema}.{r.GetString(1)}";
            var name = r.GetString(2);
            list.Add(MakeRaw(ObjectKind.Trigger, schema, name, $"trigger:{name} on {on}", r.GetString(3), on));
        }
        return list;
    }

    private async Task<List<RawObjectDefinition>> ReadRulesAsync(NpgsqlConnection conn, CancellationToken ct)
    {
        var sql = _q.Rules;

        var list = new List<RawObjectDefinition>();
        await using var cmd = new NpgsqlCommand(sql, conn);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            var schema = r.GetString(0);
            var on = $"{schema}.{r.GetString(1)}";
            var name = r.GetString(2);
            list.Add(MakeRaw(ObjectKind.Rule, schema, name, $"rule:{name} on {on}", r.GetString(3), on));
        }
        return list;
    }

    private async Task<List<RawObjectDefinition>> ReadEventTriggersAsync(NpgsqlConnection conn, CancellationToken ct)
    {
        var sql = _q.EventTriggers;

        var list = new List<RawObjectDefinition>();
        await using var cmd = new NpgsqlCommand(sql, conn);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            var name = r.GetString(0);
            var evt = r.GetString(1);
            var fn = $"{r.GetString(2)}.{r.GetString(3)}";
            // evttags (text[]) preserves the WHEN TAG IN order; NULL/empty = no tag filter (#104).
            var tags = r.IsDBNull(4) ? null : r.GetFieldValue<string[]>(4);
            var when = tags is { Length: > 0 }
                ? " WHEN TAG IN (" + string.Join(", ", tags.Select(t => "'" + t.Replace("'", "''") + "'")) + ")"
                : "";
            var body = $"CREATE EVENT TRIGGER {name} ON {evt}{when} EXECUTE FUNCTION {fn}();";
            // With the tags reconstructed the body now matches the parsed source under NormalizeRawBody,
            // so it is body-comparable (was identity-only while tags were dropped).
            list.Add(MakeRaw(ObjectKind.EventTrigger, "", name, $"eventtrigger:{name}", body));
        }
        return list;
    }

    // Procedural languages (#108): CREATE [TRUSTED] LANGUAGE name HANDLER … [INLINE …] [VALIDATOR …].
    private async Task<List<RawObjectDefinition>> ReadLanguagesAsync(NpgsqlConnection conn, CancellationToken ct)
    {
        var sql = _q.Languages;

        var list = new List<RawObjectDefinition>();
        await using var cmd = new NpgsqlCommand(sql, conn);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            var name = r.GetString(0);
            var trusted = !r.IsDBNull(1) && r.GetBoolean(1) ? "TRUSTED " : "";
            var body = $"CREATE {trusted}LANGUAGE {name} HANDLER {r.GetString(2)}";
            if (!r.IsDBNull(3)) body += $" INLINE {r.GetString(3)}";
            if (!r.IsDBNull(4)) body += $" VALIDATOR {r.GetString(4)}";
            list.Add(MakeRaw(ObjectKind.Language, "", name, $"language:{name}", body + ";", bodyComparable: false));
        }
        return list;
    }

    private async Task<List<RawObjectDefinition>> ReadOperatorsAsync(NpgsqlConnection conn, CancellationToken ct)
    {
        var sql = _q.Operators;

        var list = new List<RawObjectDefinition>();
        await using var cmd = new NpgsqlCommand(sql, conn);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            var schema = r.GetString(0);
            var op = r.GetString(1);
            var left = r.IsDBNull(2) ? null : r.GetString(2);
            var right = r.IsDBNull(3) ? null : r.GetString(3);

            var opts = new List<string> { $"FUNCTION = {r.GetString(4)}" };
            if (left is not null) opts.Add($"LEFTARG = {left}");
            if (right is not null) opts.Add($"RIGHTARG = {right}");
            if (!r.IsDBNull(5)) opts.Add($"COMMUTATOR = OPERATOR({r.GetString(5)})");
            if (!r.IsDBNull(6)) opts.Add($"NEGATOR = OPERATOR({r.GetString(6)})");
            if (!r.IsDBNull(7)) opts.Add($"RESTRICT = {r.GetString(7)}");
            if (!r.IsDBNull(8)) opts.Add($"JOIN = {r.GetString(8)}");
            if (!r.IsDBNull(9) && r.GetBoolean(9)) opts.Add("MERGES");
            if (!r.IsDBNull(10) && r.GetBoolean(10)) opts.Add("HASHES");

            // Name carries the DROP OPERATOR target shape: name (lefttype, righttype) with NONE for unary.
            var dropName = $"{schema}.{op} ({left ?? "NONE"}, {right ?? "NONE"})";
            var body = $"CREATE OPERATOR {schema}.{op} ({string.Join(", ", opts)});";
            list.Add(MakeRaw(ObjectKind.Operator, "", dropName, $"operator:{schema}.{op}({left},{right})", body));
        }
        return list;
    }

    // Standalone operator families only — families PostgreSQL auto-creates for a bare CREATE OPERATOR
    // CLASS (the class carries an 'a' dep on them) are skipped; that class re-creates its family itself.
    private async Task<List<RawObjectDefinition>> ReadOperatorFamiliesAsync(NpgsqlConnection conn, CancellationToken ct)
    {
        var sql = _q.OperatorFamilies;

        var list = new List<RawObjectDefinition>();
        await using var cmd = new NpgsqlCommand(sql, conn);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            var schema = r.GetString(0);
            var name = r.GetString(1);
            var method = r.GetString(2);
            list.Add(MakeRaw(ObjectKind.OperatorFamily, "", $"{schema}.{name} USING {method}",
                $"operatorfamily:{schema}.{name} using {method}",
                $"CREATE OPERATOR FAMILY {schema}.{name} USING {method};"));
        }
        return list;
    }

    private async Task<List<RawObjectDefinition>> ReadOperatorClassesAsync(NpgsqlConnection conn, CancellationToken ct)
    {
        var sql = _q.OperatorClasses;

        var headers = new List<(string Schema, string Name, bool Default, string IntType, string Method,
                                uint Family, uint OpcIntType, string FamSchema, string FamName, bool AutoFam)>();
        await using (var cmd = new NpgsqlCommand(sql, conn))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct))
                headers.Add((r.GetString(0), r.GetString(1), r.GetBoolean(2), r.GetString(3), r.GetString(4),
                             r.GetFieldValue<uint>(5), r.GetFieldValue<uint>(6), r.GetString(7), r.GetString(8), r.GetBoolean(9)));

        var list = new List<RawObjectDefinition>();
        foreach (var h in headers)
        {
            var members = new List<string>();

            var amopSql = _q.OperatorClassAmOps;
            await using (var oc = new NpgsqlCommand(amopSql, conn))
            {
                oc.Parameters.AddWithValue("fam", NpgsqlTypes.NpgsqlDbType.Oid, h.Family);
                oc.Parameters.AddWithValue("t", NpgsqlTypes.NpgsqlDbType.Oid, h.OpcIntType);
                await using var or = await oc.ExecuteReaderAsync(ct);
                while (await or.ReadAsync(ct))
                {
                    var opr = or.GetString(1);
                    var paren = opr.IndexOf('(');                       // "<(integer,integer)" -> "< (integer,integer)"
                    var named = paren > 0 ? opr[..paren] + " " + opr[paren..] : opr;
                    var orderBy = ReadChar(or, 2, 's') == 'o' && !or.IsDBNull(3)
                        ? $" FOR ORDER BY {or.GetString(3)}" : "";
                    members.Add($"OPERATOR {or.GetInt32(0)} {named}{orderBy}");
                }
            }

            var amprocSql = _q.OperatorClassAmProcs;
            await using (var pc = new NpgsqlCommand(amprocSql, conn))
            {
                pc.Parameters.AddWithValue("fam", NpgsqlTypes.NpgsqlDbType.Oid, h.Family);
                pc.Parameters.AddWithValue("t", NpgsqlTypes.NpgsqlDbType.Oid, h.OpcIntType);
                await using var pr = await pc.ExecuteReaderAsync(ct);
                while (await pr.ReadAsync(ct))
                    members.Add($"FUNCTION {pr.GetInt32(0)} {pr.GetString(1)}");
            }

            var header = $"CREATE OPERATOR CLASS {h.Schema}.{h.Name}{(h.Default ? " DEFAULT" : "")} " +
                         $"FOR TYPE {h.IntType} USING {h.Method}" +
                         (h.AutoFam ? "" : $" FAMILY {h.FamSchema}.{h.FamName}") + " AS\n    ";
            var body = header + string.Join(",\n    ", members) + ";";
            list.Add(MakeRaw(ObjectKind.OperatorClass, "", $"{h.Schema}.{h.Name} USING {h.Method}",
                $"operatorclass:{h.Schema}.{h.Name} using {h.Method}", body));
        }
        return list;
    }
}

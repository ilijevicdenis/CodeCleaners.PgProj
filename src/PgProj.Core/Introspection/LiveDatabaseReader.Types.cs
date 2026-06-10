using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Npgsql;
using PgProj.Core.Model;

namespace PgProj.Core.Introspection;

/// <summary>Type-system readers: enum/composite/range/shell types, domains, collations, casts,
/// and conversions.</summary>
public sealed partial class LiveDatabaseReader
{
    private async Task<List<RawObjectDefinition>> ReadEnumTypesAsync(NpgsqlConnection conn, CancellationToken ct)
    {
        var sql = _q.EnumTypes;

        var labels = new Dictionary<(string, string), List<string>>();
        await using (var cmd = new NpgsqlCommand(sql, conn))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct))
            {
                var key = (r.GetString(0), r.GetString(1));
                if (!labels.TryGetValue(key, out var l)) labels[key] = l = new List<string>();
                l.Add(r.GetString(2));
            }

        return labels.Select(kv =>
        {
            var ((schema, name), vals) = kv;
            var literals = string.Join(", ", vals.Select(v => "'" + v.Replace("'", "''") + "'"));
            return MakeRaw(ObjectKind.Type, schema, name, $"type:{schema}.{name}",
                $"CREATE TYPE {schema}.{name} AS ENUM ({literals});");
        }).ToList();
    }

    private async Task<List<RawObjectDefinition>> ReadCompositeTypesAsync(NpgsqlConnection conn, CancellationToken ct)
    {
        var sql = _q.CompositeTypes;

        var attrs = new Dictionary<(string, string), List<string>>();
        await using (var cmd = new NpgsqlCommand(sql, conn))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct))
            {
                var key = (r.GetString(0), r.GetString(1));
                if (!attrs.TryGetValue(key, out var l)) attrs[key] = l = new List<string>();
                l.Add($"{r.GetString(2)} {r.GetString(3)}");
            }

        return attrs.Select(kv =>
        {
            var ((schema, name), cols) = kv;
            return MakeRaw(ObjectKind.Type, schema, name, $"type:{schema}.{name}",
                $"CREATE TYPE {schema}.{name} AS ({string.Join(", ", cols)});");
        }).ToList();
    }

    private async Task<List<RawObjectDefinition>> ReadRangeTypesAsync(NpgsqlConnection conn, CancellationToken ct)
    {
        var sql = _q.RangeTypes;

        var list = new List<RawObjectDefinition>();
        await using var cmd = new NpgsqlCommand(sql, conn);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            var schema = r.GetString(0);
            var name = r.GetString(1);
            list.Add(MakeRaw(ObjectKind.Type, schema, name, $"type:{schema}.{name}",
                $"CREATE TYPE {schema}.{name} AS RANGE (SUBTYPE = {r.GetString(2)});"));
        }
        return list;
    }

    // Shell types: a bare `CREATE TYPE name;` (typisdefined = false). Defined enum/composite/range/base
    // types are excluded by the typisdefined filter.
    private async Task<List<RawObjectDefinition>> ReadShellTypesAsync(NpgsqlConnection conn, CancellationToken ct)
    {
        var sql = _q.ShellTypes;

        var list = new List<RawObjectDefinition>();
        await using var cmd = new NpgsqlCommand(sql, conn);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            var schema = r.GetString(0);
            var name = r.GetString(1);
            list.Add(MakeRaw(ObjectKind.Type, schema, name, $"type:{schema}.{name}", $"CREATE TYPE {schema}.{name};"));
        }
        return list;
    }

    private async Task<List<RawObjectDefinition>> ReadCollationsAsync(NpgsqlConnection conn, CancellationToken ct)
    {
        var sql = _q.Collations;

        var list = new List<RawObjectDefinition>();
        await using var cmd = new NpgsqlCommand(sql, conn);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            var schema = r.GetString(0);
            var name = r.GetString(1);
            var provider = ReadChar(r, 2, 'd') switch { 'i' => "icu", 'c' => "libc", 'b' => "builtin", _ => null };
            var deterministic = !r.IsDBNull(3) && r.GetBoolean(3);
            var collate = r.IsDBNull(4) ? null : r.GetString(4);
            var ctype = r.IsDBNull(5) ? null : r.GetString(5);
            var locale = r.IsDBNull(6) ? null : r.GetString(6);

            var opts = new List<string>();
            if (provider is not null) opts.Add($"PROVIDER = {provider}");
            var loc = locale ?? (collate is not null && collate == ctype ? collate : null);
            if (loc is not null) opts.Add($"LOCALE = '{loc.Replace("'", "''")}'");
            else
            {
                if (collate is not null) opts.Add($"LC_COLLATE = '{collate.Replace("'", "''")}'");
                if (ctype is not null) opts.Add($"LC_CTYPE = '{ctype.Replace("'", "''")}'");
            }
            if (!deterministic) opts.Add("DETERMINISTIC = false");

            list.Add(MakeRaw(ObjectKind.Collation, schema, name, $"collation:{schema}.{name}",
                $"CREATE COLLATION {schema}.{name} ({string.Join(", ", opts)});"));
        }
        return list;
    }

    private async Task<List<RawObjectDefinition>> ReadDomainsAsync(NpgsqlConnection conn, CancellationToken ct)
    {
        var sql = _q.Domains;

        var list = new List<RawObjectDefinition>();
        await using var cmd = new NpgsqlCommand(sql, conn);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            var schema = r.GetString(0);
            var name = r.GetString(1);
            var baseType = r.GetString(2);
            var notNull = r.GetBoolean(3);
            var def = r.IsDBNull(4) ? null : r.GetString(4);
            var checks = r.IsDBNull(5) ? null : r.GetString(5);

            var body = $"CREATE DOMAIN {schema}.{name} AS {baseType}";
            if (notNull) body += " NOT NULL";
            if (!string.IsNullOrWhiteSpace(def)) body += $" DEFAULT {def}";
            if (!string.IsNullOrWhiteSpace(checks)) body += $" {checks}";
            list.Add(MakeRaw(ObjectKind.Domain, schema, name, $"domain:{schema}.{name}", body + ";"));
        }
        return list;
    }

    private async Task<List<RawObjectDefinition>> ReadCastsAsync(NpgsqlConnection conn, CancellationToken ct)
    {
        // User casts only: those touching a user-schema type or function (built-in casts are excluded).
        var sql = _q.Casts;

        var list = new List<RawObjectDefinition>();
        await using var cmd = new NpgsqlCommand(sql, conn);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            var src = r.GetString(0);
            var tgt = r.GetString(1);
            var method = ReadChar(r, 4, 'f');
            var with = method switch
            {
                'b' => "WITHOUT FUNCTION",
                'i' => "WITH INOUT",
                _ => $"WITH FUNCTION {r.GetString(2)}",
            };
            var context = ReadChar(r, 3, 'e') switch { 'a' => " AS ASSIGNMENT", 'i' => " AS IMPLICIT", _ => "" };
            var name = $"({src} AS {tgt})";   // also the DROP CAST target shape
            var body = $"CREATE CAST {name} {with}{context};";
            list.Add(MakeRaw(ObjectKind.Cast, "", name, $"cast:{src}->{tgt}", body));
        }
        return list;
    }

    private async Task<List<RawObjectDefinition>> ReadConversionsAsync(NpgsqlConnection conn, CancellationToken ct)
    {
        var sql = _q.Conversions;

        var list = new List<RawObjectDefinition>();
        await using var cmd = new NpgsqlCommand(sql, conn);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            var schema = r.GetString(0);
            var name = r.GetString(1);
            var keyword = !r.IsDBNull(5) && r.GetBoolean(5) ? "CREATE DEFAULT CONVERSION" : "CREATE CONVERSION";
            var body = $"{keyword} {schema}.{name} FOR '{r.GetString(2)}' TO '{r.GetString(3)}' FROM {r.GetString(4)};";
            list.Add(MakeRaw(ObjectKind.Conversion, schema, name, $"conversion:{schema}.{name}", body));
        }
        return list;
    }
}

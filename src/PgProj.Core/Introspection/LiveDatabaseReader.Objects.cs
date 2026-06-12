using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Npgsql;
using PgProj.Core.Comparison;
using PgProj.Core.Model;

namespace PgProj.Core.Introspection;

/// <summary>Remaining object-kind readers: extensions, comments, policies, FDW/servers/user mappings,
/// extended statistics, text search, and publications.</summary>
public sealed partial class LiveDatabaseReader
{
    private async Task<List<RawObjectDefinition>> ReadExtensionsAsync(NpgsqlConnection conn, CancellationToken ct)
    {
        var list = new List<RawObjectDefinition>();
        await using var cmd = new NpgsqlCommand(_q.Extensions, conn);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            var name = r.GetString(0);
            list.Add(MakeRaw(ObjectKind.Extension, "", name, $"extension:{name}",
                $"CREATE EXTENSION IF NOT EXISTS {SqlEmitter.Quote(name)};"));
        }
        return list;
    }

    private async Task<List<RawObjectDefinition>> ReadCommentsAsync(NpgsqlConnection conn, CancellationToken ct)
    {
        // The query (issue #61) returns a uniform (target, description) per comment across ALL object classes
        // — relation/column/schema/function/procedure/type/domain/trigger — where `target` is the exact
        // `<KIND> <name>` a hand-written COMMENT ON would carry. The comparer pairs comments on their
        // canonical body (RawObjectMeta.ComparisonKey), so the identity here is informational; we still build
        // it in the parser's `comment:<normalized target>` shape for readability/extract file naming.
        var sql = _q.Comments;

        var list = new List<RawObjectDefinition>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using var cmd = new NpgsqlCommand(sql, conn);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            var target = r.GetString(0);
            if (r.IsDBNull(1)) continue;
            var desc = r.GetString(1).Replace("'", "''");
            var identity = $"comment:{target.ToLowerInvariant()}";
            if (!seen.Add(identity)) continue; // de-dup the two schema-comment branches (shared vs local catalog)
            list.Add(MakeRaw(ObjectKind.Comment, "", "", identity, $"COMMENT ON {target} IS '{desc}';"));
        }
        return list;
    }

    private async Task<List<RawObjectDefinition>> ReadPoliciesAsync(NpgsqlConnection conn, CancellationToken ct)
    {
        var sql = _q.Policies;

        var list = new List<RawObjectDefinition>();
        await using var cmd = new NpgsqlCommand(sql, conn);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            var schema = r.GetString(0);
            var on = $"{schema}.{r.GetString(1)}";
            var name = r.GetString(2);
            var cmdLetter = ReadChar(r, 3, '*');
            var permissive = !r.IsDBNull(4) && r.GetBoolean(4);
            var usingExpr = r.IsDBNull(5) ? null : r.GetString(5);
            var checkExpr = r.IsDBNull(6) ? null : r.GetString(6);

            // Roles the policy applies to (#103). PUBLIC is polroles {0} → reconstructed as TO PUBLIC.
            var roles = r.IsDBNull(7) ? Array.Empty<string>() : r.GetFieldValue<string[]>(7);

            var forCmd = cmdLetter switch { 'r' => "SELECT", 'a' => "INSERT", 'w' => "UPDATE", 'd' => "DELETE", _ => "ALL" };
            var body = $"CREATE POLICY {name} ON {on} AS {(permissive ? "PERMISSIVE" : "RESTRICTIVE")} FOR {forCmd}";
            if (roles.Length > 0)
                body += " TO " + string.Join(", ", roles.Select(role => role.Equals("public", StringComparison.OrdinalIgnoreCase) ? "PUBLIC" : SqlEmitter.Quote(role)));
            if (!string.IsNullOrWhiteSpace(usingExpr)) body += $" USING ({usingExpr})";
            if (!string.IsNullOrWhiteSpace(checkExpr)) body += $" WITH CHECK ({checkExpr})";
            // TO PUBLIC is the policy default, so a source that writes it and one that omits it both map to
            // polroles {0}; NormalizeRawBody can't reconcile that, so policies stay identity-only (not
            // body-compared) to avoid phantom diffs — the reconstructed roles are for extract fidelity.
            list.Add(MakeRaw(ObjectKind.Policy, schema, name, $"policy:{name} on {on}", body + ";", on, bodyComparable: false));
        }
        return list;
    }

    // Expression extended statistics (stxexprs set): full DDL via pg_get_statisticsobjdef (#110), replacing
    // the former existence-only handling. Column-only stats are read by ReadStatisticsAsync (stxexprs NULL),
    // so the two are mutually exclusive — no double-read.
    private async Task<List<RawObjectDefinition>> ReadExpressionStatisticsAsync(NpgsqlConnection conn, CancellationToken ct)
    {
        var sql = _q.ExpressionStatistics;

        var list = new List<RawObjectDefinition>();
        await using var cmd = new NpgsqlCommand(sql, conn);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            var schema = r.GetString(0);
            var name = r.GetString(1);
            list.Add(MakeRaw(ObjectKind.Statistics, schema, name, $"statistics:{schema}.{name}", r.GetString(2) + ";"));
        }
        return list;
    }

    private async Task<List<RawObjectDefinition>> ReadTextSearchDictionariesAsync(NpgsqlConnection conn, CancellationToken ct)
    {
        var sql = _q.TextSearchDictionaries;

        var list = new List<RawObjectDefinition>();
        await using var cmd = new NpgsqlCommand(sql, conn);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            var schema = r.GetString(0);
            var name = r.GetString(1);
            var opts = $"TEMPLATE = {r.GetString(2)}";
            if (!r.IsDBNull(3) && r.GetString(3).Length > 0) opts += $", {r.GetString(3)}";
            var body = $"CREATE TEXT SEARCH DICTIONARY {schema}.{name} ({opts});";
            list.Add(MakeRaw(ObjectKind.TextSearchDictionary, schema, name, $"textsearchdictionary:{schema}.{name}", body));
        }
        return list;
    }

    private async Task<List<RawObjectDefinition>> ReadTextSearchConfigurationsAsync(NpgsqlConnection conn, CancellationToken ct)
    {
        // Pass 1: the configurations (and their parser). Read fully before issuing per-config map queries.
        var cfgSql = _q.TextSearchConfigurations;

        var configs = new List<(string Schema, string Name, uint Oid, uint Parser, string ParserName)>();
        await using (var cmd = new NpgsqlCommand(cfgSql, conn))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct))
                configs.Add((r.GetString(0), r.GetString(1), r.GetFieldValue<uint>(2), r.GetFieldValue<uint>(3), r.GetString(4)));

        var list = new List<RawObjectDefinition>();
        foreach (var c in configs)
        {
            var sb = new System.Text.StringBuilder();
            sb.Append($"CREATE TEXT SEARCH CONFIGURATION {c.Schema}.{c.Name} (PARSER = {c.ParserName});");

            // Pass 2: token-type → dictionary-list mappings, one ADD MAPPING per token type.
            var mapSql = _q.TextSearchConfigurationMap;
            await using var mc = new NpgsqlCommand(mapSql, conn);
            mc.Parameters.AddWithValue("parser", NpgsqlTypes.NpgsqlDbType.Oid, c.Parser);
            mc.Parameters.AddWithValue("cfg", NpgsqlTypes.NpgsqlDbType.Oid, c.Oid);
            await using (var mr = await mc.ExecuteReaderAsync(ct))
                while (await mr.ReadAsync(ct))
                    sb.Append($"\nALTER TEXT SEARCH CONFIGURATION {c.Schema}.{c.Name} ADD MAPPING FOR {mr.GetString(0)} WITH {mr.GetString(1)};");

            list.Add(MakeRaw(ObjectKind.TextSearchConfiguration, c.Schema, c.Name,
                $"textsearchconfiguration:{c.Schema}.{c.Name}", sb.ToString()));
        }
        return list;
    }

    private async Task<List<RawObjectDefinition>> ReadTextSearchParsersAsync(NpgsqlConnection conn, CancellationToken ct)
    {
        var sql = _q.TextSearchParsers;

        var list = new List<RawObjectDefinition>();
        await using var cmd = new NpgsqlCommand(sql, conn);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            var schema = r.GetString(0);
            var name = r.GetString(1);
            var opts = new List<string>
            {
                $"START = {r.GetString(2)}", $"GETTOKEN = {r.GetString(3)}",
                $"END = {r.GetString(4)}", $"LEXTYPES = {r.GetString(5)}",
            };
            if (!r.IsDBNull(6)) opts.Add($"HEADLINE = {r.GetString(6)}");
            var body = $"CREATE TEXT SEARCH PARSER {schema}.{name} ({string.Join(", ", opts)});";
            list.Add(MakeRaw(ObjectKind.TextSearchParser, schema, name, $"textsearchparser:{schema}.{name}", body, bodyComparable: false));
        }
        return list;
    }

    private async Task<List<RawObjectDefinition>> ReadTextSearchTemplatesAsync(NpgsqlConnection conn, CancellationToken ct)
    {
        var sql = _q.TextSearchTemplates;

        var list = new List<RawObjectDefinition>();
        await using var cmd = new NpgsqlCommand(sql, conn);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            var schema = r.GetString(0);
            var name = r.GetString(1);
            var opts = new List<string>();
            if (!r.IsDBNull(2)) opts.Add($"INIT = {r.GetString(2)}");
            opts.Add($"LEXIZE = {r.GetString(3)}");
            var body = $"CREATE TEXT SEARCH TEMPLATE {schema}.{name} ({string.Join(", ", opts)});";
            list.Add(MakeRaw(ObjectKind.TextSearchTemplate, schema, name, $"textsearchtemplate:{schema}.{name}", body, bodyComparable: false));
        }
        return list;
    }

    private async Task<List<RawObjectDefinition>> ReadForeignDataWrappersAsync(NpgsqlConnection conn, CancellationToken ct)
    {
        var sql = _q.ForeignDataWrappers;

        var list = new List<RawObjectDefinition>();
        await using var cmd = new NpgsqlCommand(sql, conn);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            var name = r.GetString(0);
            var body = $"CREATE FOREIGN DATA WRAPPER {name}";
            if (!r.IsDBNull(1)) body += $" HANDLER {r.GetString(1)}";
            if (!r.IsDBNull(2)) body += $" VALIDATOR {r.GetString(2)}";
            body += OptionsClause(r.IsDBNull(3) ? null : r.GetFieldValue<string[]>(3));
            list.Add(MakeRaw(ObjectKind.ForeignDataWrapper, "", name, $"foreigndatawrapper:{name}", body + ";"));
        }
        return list;
    }

    private async Task<List<RawObjectDefinition>> ReadServersAsync(NpgsqlConnection conn, CancellationToken ct)
    {
        var sql = _q.Servers;

        var list = new List<RawObjectDefinition>();
        await using var cmd = new NpgsqlCommand(sql, conn);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            var name = r.GetString(0);
            var body = $"CREATE SERVER {name}";
            if (!r.IsDBNull(2)) body += $" TYPE '{r.GetString(2).Replace("'", "''")}'";
            if (!r.IsDBNull(3)) body += $" VERSION '{r.GetString(3).Replace("'", "''")}'";
            body += $" FOREIGN DATA WRAPPER {r.GetString(1)}";
            body += OptionsClause(r.IsDBNull(4) ? null : r.GetFieldValue<string[]>(4));
            list.Add(MakeRaw(ObjectKind.Server, "", name, $"server:{name}", body + ";"));
        }
        return list;
    }

    private async Task<List<RawObjectDefinition>> ReadUserMappingsAsync(NpgsqlConnection conn, CancellationToken ct)
    {
        var sql = _q.UserMappings;

        var list = new List<RawObjectDefinition>();
        await using var cmd = new NpgsqlCommand(sql, conn);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            // usename is NULL for a PUBLIC mapping (#108); the parser reads "PUBLIC" as the user.
            var user = r.IsDBNull(0) ? "PUBLIC" : r.GetString(0);
            var srv = r.GetString(1);
            var opts = OptionsClause(r.IsDBNull(2) ? null : r.GetFieldValue<string[]>(2));
            var name = $"FOR {user} SERVER {srv}";   // also the DROP USER MAPPING target (Signature style)
            var body = $"CREATE USER MAPPING {name}{opts};";
            // Identity-paired (not body-compared): options ordering/visibility makes the body fragile; the
            // reconstruction is for extract fidelity. Identity matches the parser's usermapping:for…server….
            list.Add(MakeRaw(ObjectKind.UserMapping, "", name, $"usermapping:{name}", body, bodyComparable: false));
        }
        return list;
    }

    private async Task<List<RawObjectDefinition>> ReadStatisticsAsync(NpgsqlConnection conn, CancellationToken ct)
    {
        // Column-based extended statistics only (stxexprs IS NULL); expression stats stay existence-only.
        var sql = _q.Statistics;

        var list = new List<RawObjectDefinition>();
        await using var cmd = new NpgsqlCommand(sql, conn);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            var schema = r.GetString(0);
            var name = r.GetString(1);
            var tbl = r.GetString(2);
            var cols = r.IsDBNull(3) ? "" : r.GetString(3);
            var kinds = r.IsDBNull(4) ? Array.Empty<string>() : r.GetFieldValue<string[]>(4);
            var kindList = kinds.Length > 0 ? $" ({string.Join(", ", kinds)})" : "";
            var body = $"CREATE STATISTICS {schema}.{name}{kindList} ON {cols} FROM {tbl};";
            list.Add(MakeRaw(ObjectKind.Statistics, schema, name, $"statistics:{schema}.{name}", body));
        }
        return list;
    }

    private async Task<List<RawObjectDefinition>> ReadPublicationsAsync(NpgsqlConnection conn, CancellationToken ct)
    {
        var sql = _q.Publications;

        var list = new List<RawObjectDefinition>();
        await using var cmd = new NpgsqlCommand(sql, conn);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            var name = r.GetString(0);
            var body = new System.Text.StringBuilder($"CREATE PUBLICATION {name}");

            if (r.GetBoolean(1))
            {
                body.Append(" FOR ALL TABLES");
            }
            else
            {
                var fors = new List<string>();
                if (!r.IsDBNull(7)) fors.Add($"TABLE {r.GetString(7)}");
                if (!r.IsDBNull(8)) fors.Add($"TABLES IN SCHEMA {r.GetString(8)}");
                if (fors.Count > 0) body.Append(" FOR ").Append(string.Join(", ", fors));
            }

            var ops = new List<string>();
            if (r.GetBoolean(2)) ops.Add("insert");
            if (r.GetBoolean(3)) ops.Add("update");
            if (r.GetBoolean(4)) ops.Add("delete");
            if (r.GetBoolean(5)) ops.Add("truncate");
            var with = new List<string> { $"publish = '{string.Join(", ", ops)}'" };
            if (r.GetBoolean(6)) with.Add("publish_via_partition_root = true");
            body.Append($" WITH ({string.Join(", ", with)});");

            list.Add(MakeRaw(ObjectKind.Publication, "", name, $"publication:{name}".ToLowerInvariant(), body.ToString()));
        }
        return list;
    }

    // pg_*options is text[] of "key=value"; render as an OPTIONS (key 'value', …) clause.
    private static string OptionsClause(string[]? opts)
    {
        if (opts is null || opts.Length == 0) return string.Empty;
        var parts = opts.Select(o =>
        {
            var eq = o.IndexOf('=');
            var key = eq >= 0 ? o[..eq] : o;
            var val = eq >= 0 ? o[(eq + 1)..] : string.Empty;
            return $"{key} '{val.Replace("'", "''")}'";
        });
        return $" OPTIONS ({string.Join(", ", parts)})";
    }
}

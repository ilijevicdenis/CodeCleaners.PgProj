using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Npgsql;
using PgProj.Core.Model;
using PgProj.Core.Versioning;

namespace PgProj.Core.Introspection;

/// <summary>
/// Reads the live schema of a Postgres server into a <see cref="DatabaseModel"/> by querying the
/// system catalogs with raw ADO.NET (Npgsql — no ORM, to avoid materialisation/tracking overhead).
/// Types come straight from <c>format_type()</c>, the same canonical spelling project types normalise
/// to, so the model diffs against a project model with minimal phantom differences.
///
/// Reads run CONCURRENTLY: a single Npgsql connection cannot multiplex commands, so each catalog
/// query gets its own pooled connection and the independent reads fan out (bounded by a semaphore).
/// Each read returns its own data — nothing mutates the shared <see cref="DatabaseModel"/> off-thread —
/// and the results are merged single-threaded at the end. Table-dependent reads (PK/unique/check/FK)
/// run as a second wave once the table map exists, touching disjoint members of each table object.
///
/// This file is the orchestration: the reader registry, the fan-out/merge, and shared helpers. The
/// reader bodies live in partials mirroring the <see cref="CatalogQueries"/> split:
///   LiveDatabaseReader.Relations.cs — schemas, tables/columns + wave-2 constraints, indexes, views,
///                                     sequences, typed/partition tables, foreign tables
///   LiveDatabaseReader.Types.cs     — enum/composite/range/shell types, domains, collations, casts,
///                                     conversions
///   LiveDatabaseReader.Routines.cs  — functions, aggregates, triggers, rules, event triggers,
///                                     languages, operators and operator classes/families
///   LiveDatabaseReader.Objects.cs   — extensions, comments, policies, FDW/servers/user mappings,
///                                     statistics, text search, publications
/// </summary>
public sealed partial class LiveDatabaseReader
{
    private const int MaxConcurrentReads = 8;

    // The catalog SQL this reader issues is sourced from the active version profile's CatalogQueries —
    // no SQL literals live in this file anymore. Defaults to the latest profile; pass an older profile
    // (or one selected from a project's TargetPostgresVersion) to introspect with that version's queries.
    private readonly CatalogQueries _q;

    public LiveDatabaseReader() : this(PostgresVersionProfile.Latest) { }

    public LiveDatabaseReader(PostgresVersionProfile profile) => _q = profile.CatalogQueries;

    /// <summary>One raw-object introspection read: returns its objects for the connection it is given.</summary>
    private delegate Task<List<RawObjectDefinition>> RawObjectReader(NpgsqlConnection conn, CancellationToken ct);

    /// <summary>
    /// The raw-object reader registry (issue #44): the single, ordered list of every raw-object read the
    /// introspector runs. Adding a raw kind = add its reader here (its catalog SQL lives on the
    /// <see cref="PostgresVersionProfile"/>, its metadata in <see cref="Extensibility.ObjectKindRegistry"/>) —
    /// no edit to <see cref="ReadAsync"/>'s fan-out. (Readers don't map 1:1 to kinds — e.g. enum/composite/
    /// range/shell all yield <c>Type</c>, and existence-objects yield several — so this is a reader list,
    /// not a per-kind dispatch.)
    /// </summary>
    private RawObjectReader[] RawObjectReaders =>
    [
        ReadExtensionsAsync, ReadTypedTablesAsync, ReadPartitionChildrenAsync, ReadEnumTypesAsync, ReadCompositeTypesAsync,
        ReadRangeTypesAsync, ReadShellTypesAsync, ReadCollationsAsync, ReadAggregatesAsync,
        ReadDomainsAsync, ReadTriggersAsync, ReadRulesAsync, ReadPoliciesAsync, ReadEventTriggersAsync,
        ReadCommentsAsync, ReadConversionsAsync, ReadForeignDataWrappersAsync, ReadServersAsync,
        ReadUserMappingsAsync, ReadStatisticsAsync, ReadCastsAsync, ReadForeignTablesAsync, ReadOperatorsAsync,
        ReadOperatorFamiliesAsync, ReadOperatorClassesAsync, ReadTextSearchDictionariesAsync,
        ReadTextSearchConfigurationsAsync, ReadTextSearchParsersAsync, ReadTextSearchTemplatesAsync,
        ReadLanguagesAsync, ReadPublicationsAsync, ReadExpressionStatisticsAsync,
    ];

    public async Task<DatabaseModel> ReadAsync(string connectionString, CancellationToken ct = default)
    {
        using var gate = new SemaphoreSlim(MaxConcurrentReads);

        // Run a read on its own pooled connection (commands on one connection can't run concurrently).
        async Task<T> Read<T>(Func<NpgsqlConnection, Task<T>> body)
        {
            await gate.WaitAsync(ct);
            try
            {
                await using var conn = new NpgsqlConnection(connectionString);
                await conn.OpenAsync(ct);
                return await body(conn);
            }
            finally { gate.Release(); }
        }
        // Non-result variant for the table-dependent reads (they mutate the shared table objects).
        Task ReadVoid(Func<NpgsqlConnection, Task> body) => Read<object?>(async c => { await body(c); return null; });

        // ---- wave 1: everything that doesn't depend on the table map, in parallel ----
        var schemasTask   = Read(c => ReadSchemasAsync(c, ct));
        var tablesTask    = Read(c => ReadTablesAndColumnsAsync(c, ct));
        var indexesTask   = Read(c => ReadIndexesAsync(c, ct));
        var viewsTask     = Read(c => ReadViewsAsync(c, ct));
        var sequencesTask = Read(c => ReadSequencesAsync(c, ct));
        var functionsTask = Read(c => ReadFunctionsAsync(c, ct));

        // Raw-object reads come from the reader registry (issue #44) — one entry per reader, the single
        // place a new raw kind's introspection is registered. Each runs on its own pooled connection
        // under the concurrency gate; merge order doesn't matter (model.Objects is sorted canonically
        // after merge, #59).
        var readers = RawObjectReaders;
        var rawTasks = new Task<List<RawObjectDefinition>>[readers.Length];
        for (var i = 0; i < readers.Length; i++)
        {
            var reader = readers[i];
            rawTasks[i] = Read(c => reader(c, ct));
        }

        // ---- wave 2: table-dependent reads, started as soon as the table map is ready ----
        // INVARIANT: these run concurrently and mutate the SAME shared TableDefinition objects, so
        // each must write a DISJOINT member — constraints→PrimaryKey/Unique, checks→Checks,
        // fks→ForeignKeys. They never touch the same list, so no lock is needed. A new wave-2 reader
        // that writes a member another already writes would be a data race: gate it or give it its
        // own wave.
        var (tables, byKey) = await tablesTask;
        var constraintsTask = ReadVoid(c => ReadConstraintsAsync(c, byKey, ct));
        var checksTask      = ReadVoid(c => ReadChecksAsync(c, byKey, ct));
        var fksTask         = ReadVoid(c => ReadForeignKeysAsync(c, byKey, ct));
        var excludesTask    = ReadVoid(c => ReadExcludeConstraintsAsync(c, byKey, ct));
        var partitioningTask = ReadVoid(c => ReadTablePartitioningAsync(c, byKey, ct));

        await Task.WhenAll(rawTasks);
        await Task.WhenAll(schemasTask, indexesTask, viewsTask, sequencesTask, functionsTask,
                           constraintsTask, checksTask, fksTask, excludesTask, partitioningTask);

        // ---- merge (single-threaded) ----
        var model = new DatabaseModel();
        model.Schemas.AddRange(schemasTask.Result);
        model.Tables.AddRange(tables);
        model.Indexes.AddRange(indexesTask.Result);
        model.Views.AddRange(viewsTask.Result);
        model.Sequences.AddRange(sequencesTask.Result);
        model.Functions.AddRange(functionsTask.Result);
        foreach (var t in rawTasks) model.Objects.AddRange(t.Result);

        // DETERMINISM (issue #59): the raw reads above fan out across ~25 parallel tasks and a few
        // build their lists from Dictionary enumeration (enum/composite types), so the merged
        // `Objects` order is not stable run-to-run or machine-to-machine. Impose a total, culture-
        // independent order so the introspected model — and everything derived from it (canonical
        // form, hash, diff, deploy script) — is byte-reproducible. Phase ordering still dominates the
        // generated script (the comparer sorts by phase, stably), so this only canonicalises the
        // within-phase tie-break; it cannot reorder a dependency across phases.
        model.Objects.Sort(CompareCanonical);
        return model;
    }

    /// <summary>
    /// Total, culture-independent ordering for raw objects: (kind, schema, name, identity). Used to
    /// canonicalise the parallel-merged <see cref="DatabaseModel.Objects"/> so introspection is
    /// byte-reproducible. <see cref="RawObjectDefinition.Identity"/> is unique, so this is a strict
    /// total order — the final tie-break never falls through. Ordinal string compares keep it
    /// identical regardless of the current culture.
    /// </summary>
    public static int CompareCanonical(RawObjectDefinition a, RawObjectDefinition b)
    {
        var c = ((int)a.Kind).CompareTo((int)b.Kind);
        if (c != 0) return c;
        c = string.CompareOrdinal(a.Schema, b.Schema);
        if (c != 0) return c;
        c = string.CompareOrdinal(a.Name, b.Name);
        if (c != 0) return c;
        return string.CompareOrdinal(a.Identity, b.Identity);
    }

    // ---- helpers shared across the reader partials ------------------------------------------

    private static bool IsUserSchema(string schema) =>
        schema is not ("pg_catalog" or "information_schema") && !schema.StartsWith("pg_", StringComparison.Ordinal);

    // The catalog's char-type columns (contype, confdeltype, …) are the single-byte internal "char";
    // read defensively so a provider that surfaces it as a string doesn't blow up.
    private static char ReadChar(NpgsqlDataReader r, int ordinal, char fallback = 'a')
    {
        if (r.IsDBNull(ordinal)) return fallback;
        return r.GetValue(ordinal) switch
        {
            char c => c,
            string s => s.Length > 0 ? s[0] : fallback,
            _ => fallback,
        };
    }

    private static RawObjectDefinition MakeRaw(ObjectKind kind, string schema, string name, string identity, string body, string? on = null, bool bodyComparable = true) =>
        new(kind, schema, name, identity.ToLowerInvariant(), body, on, bodyComparable);
}

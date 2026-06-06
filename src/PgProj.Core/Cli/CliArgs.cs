using System;
using System.Collections.Generic;
using System.Linq;

namespace PgProj.Core.Cli;

/// <summary>
/// Thrown when the CLI was invoked incorrectly (missing argument, malformed option, unknown command).
/// The CLI entry point maps this to <see cref="ExitCode.Usage"/> and prints usage, distinguishing a
/// user mistake from an unexpected runtime failure (<see cref="ExitCode.Error"/>).
/// </summary>
public sealed class CliUsageException : Exception
{
    public CliUsageException(string message) : base(message) { }
}

/// <summary>
/// A thin, allocation-light reader over a raw <c>args</c> array shared by every <c>pgproj</c> verb.
/// It centralizes the option/flag/positional grammar so the five SSDT-parity epics extend one tested
/// parser instead of re-deriving <c>--opt value</c> / <c>--flag</c> / <c>Name=Value</c> handling in
/// each verb (and so a future <c>pgproj serve</c> host can reuse the exact same parsing).
/// </summary>
/// <remarks>
/// Grammar (matches the pre-existing hand-rolled helpers it replaces):
/// <list type="bullet">
/// <item><c>args[0]</c> is the verb; positionals are the remaining tokens that do not start with '-'.</item>
/// <item>An option takes the immediately-following token as its value: <c>--output file.sql</c>.</item>
/// <item>A flag is its mere presence: <c>--dry-run</c>.</item>
/// <item>Repeatable key/value options carry <c>Name=Value</c> as their value: <c>--var Env=prod</c>.</item>
/// <item>Option and flag names match case-insensitively; <c>Name</c> keys are case-insensitive too.</item>
/// </list>
/// </remarks>
public sealed class CliArgs
{
    private readonly string[] _args;

    public CliArgs(string[] args) => _args = args ?? Array.Empty<string>();

    /// <summary>The raw argument array (verb included), for the rare verb that needs bespoke parsing.</summary>
    public IReadOnlyList<string> Raw => _args;

    /// <summary>The verb token (<c>args[0]</c>), lower-cased, or empty when no arguments were given.</summary>
    public string Verb => _args.Length == 0 ? string.Empty : _args[0].ToLowerInvariant();

    /// <summary>The value of the first matching option (<c>--name value</c>), or null if none is present.</summary>
    public string? GetOption(params string[] names)
    {
        for (var i = 0; i < _args.Length - 1; i++)
            if (names.Contains(_args[i], StringComparer.OrdinalIgnoreCase))
                return _args[i + 1];
        return null;
    }

    /// <summary>Every value supplied for a repeatable option, in order (e.g. all <c>--var</c> values).</summary>
    public IReadOnlyList<string> GetOptionValues(string name)
    {
        var values = new List<string>();
        for (var i = 0; i < _args.Length - 1; i++)
            if (_args[i].Equals(name, StringComparison.OrdinalIgnoreCase))
                values.Add(_args[i + 1]);
        return values;
    }

    /// <summary>True when a bare flag (e.g. <c>--strict</c>) is present anywhere in the arguments.</summary>
    public bool HasFlag(string name) => _args.Any(a => a.Equals(name, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Parses every occurrence of a repeatable <c>Name=Value</c> option (e.g. <c>--var Env=prod</c> or
    /// <c>--rule PG003=off</c>) into a case-insensitive map. Later occurrences win. A value missing the
    /// '=' (or with an empty name) is a usage error.
    /// </summary>
    public IReadOnlyDictionary<string, string> GetKeyValues(string optionName)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in GetOptionValues(optionName))
        {
            var eq = pair.IndexOf('=');
            if (eq <= 0)
                throw new CliUsageException($"{optionName} expects Name=Value (got '{pair}').");
            map[pair[..eq].Trim()] = pair[(eq + 1)..];
        }
        return map;
    }

    /// <summary>The <paramref name="index"/>-th positional (non-option token) after the verb, or null.</summary>
    public string? Positional(int index) =>
        _args.Skip(1).Where(a => !a.StartsWith('-')).ElementAtOrDefault(index);

    /// <summary>The first positional after the verb, or a <see cref="CliUsageException"/> naming what was expected.</summary>
    public string RequirePositional(string what) =>
        Positional(0) ?? throw new CliUsageException($"Expected a {what} argument.");

    /// <summary>The requested output mode (<c>--format</c>), defaulting to <see cref="OutputFormat.Text"/>.</summary>
    public OutputFormat Format => OutputFormats.Parse(GetOption("--format"));

    /// <summary>Convenience: true when <c>--format json</c> was requested.</summary>
    public bool WantsJson => Format == OutputFormat.Json;

    /// <summary>
    /// The Postgres connection string from <c>-c</c>/<c>--connection</c>, falling back to the
    /// <c>PGPROJ_CONNECTION</c> environment variable. Throws a usage error when neither is set.
    /// </summary>
    public string RequireConnection() =>
        GetOption("-c", "--connection")
        ?? Environment.GetEnvironmentVariable("PGPROJ_CONNECTION")
        ?? throw new CliUsageException("A connection string is required (--connection or PGPROJ_CONNECTION).");
}

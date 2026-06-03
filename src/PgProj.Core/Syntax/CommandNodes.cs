namespace PgProj.Core.Syntax;

/// <summary>
/// Session / procedural / utility commands (DO, CALL, SET, SHOW, EXPLAIN, LOCK, PREPARE, cursors,
/// transaction control, COPY, GRANT/REVOKE, …). These have small, varied grammars; the parse method
/// per command does the accept/reject work, and the node records the kind plus any nested statement.
/// </summary>
public sealed class CommandStatement : SqlStatement
{
    public string Kind { get; init; } = "";          // e.g. "DO", "SET", "EXPLAIN", "GRANT", "BEGIN"
    public string? Detail { get; set; }              // a salient name/target where useful
    public SqlStatement? Inner { get; set; }         // EXPLAIN / PREPARE wrapped statement
    public SelectQuery? Query { get; set; }          // DECLARE CURSOR FOR <query>
}

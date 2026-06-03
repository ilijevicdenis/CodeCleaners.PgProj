namespace PgProj.Core.Model;

/// <summary>
/// Every schema-object kind the tool understands beyond the finely-modelled core
/// (schema/table/index/view/sequence/function). The kinds below are handled by the generic
/// "raw object" mechanism: captured verbatim with a stable identity and diffed by body.
/// </summary>
public enum ObjectKind
{
    Extension,
    Language,
    Type,
    Domain,
    Collation,
    Conversion,
    Cast,
    Operator,
    OperatorClass,
    OperatorFamily,
    Aggregate,
    Trigger,
    Rule,
    Policy,
    EventTrigger,
    Statistics,
    ForeignDataWrapper,
    Server,
    UserMapping,
    ForeignTable,
    TextSearchConfiguration,
    TextSearchDictionary,
    TextSearchParser,
    TextSearchTemplate,
    Transform,
    Comment,
}

/// <summary>
/// A schema object captured verbatim. <see cref="Identity"/> is stable across edits (it does not
/// include the body), so the comparer can tell "this object changed" from "a different object".
/// <see cref="Body"/> is the full CREATE/COMMENT statement, replayed on deploy. <see cref="OnObject"/>
/// holds the qualified table for table-scoped kinds (trigger/rule/policy).
/// </summary>
public sealed record RawObjectDefinition(
    ObjectKind Kind,
    string Schema,
    string Name,
    string Identity,
    string Body,
    string? OnObject = null,
    bool BodyComparable = true);

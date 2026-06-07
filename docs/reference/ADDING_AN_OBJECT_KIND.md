# Adding a PostgreSQL object kind

The `IProjectObject` contract + `ProjectObjectRegistry` (issue #44) make adding an object kind a
bounded, predictable change instead of an ad-hoc hunt across the codebase. Diff, codegen, and
extract iterate kinds through the contract via the registry, so a new kind flows through them once it
is registered — no switch statements to chase.

## The contract

Every kind is an [`IProjectObject`](../../src/PgProj.Core/Extensibility/IProjectObject.cs):

| Method | Delegates to (foundation) |
|--------|---------------------------|
| `Identity()` → ObjectId + StableId + CanonicalHash | `ObjectIdentityComputer` (#42) |
| `Canonicalize()` → canonical form | `Comparison/Canonicalizer` (#42) |
| `Hash()` → CanonicalHash | `ObjectIdentityComputer` (#42) |
| `Diff(other)` → Unchanged/Rename/Alter/Drop+Create | `IdentityDiff` (#42); field-level deltas → #53 |
| `GenerateSql(versionProfile)` → version-aware DDL | `SqlEmitter` / catalog body, `PostgresVersionProfile` (#43) |
| `Validate(symbols)` → diagnostics | `SymbolTable` (#46); deepened by Phase 5 (#48) |

## Checklist — a **raw** object kind (the common case)

Most kinds are captured verbatim with a stable identity (`RawObjectDefinition`). To add one:

1. Add the value to the [`ObjectKind`](../../src/PgProj.Core/Model/RawObject.cs) enum.
2. Register its metadata in [`RawObjectMeta`](../../src/PgProj.Core/Comparison/RawObjectMeta.cs):
   deploy `Phase`, `DropSql`/`DropKeyword`/`DropTarget`, `Folder`, and — if its live reconstruction
   can't be textually faithful — `ComparesByIdentityOnly`.
3. Add its token to [`SchemaCompareObjectType.OfKind`](../../src/PgProj.Core/Comparison/SchemaCompareObjectType.cs).
4. Register its **catalog query** on the [`PostgresVersionProfile`](../../src/PgProj.Core/Versioning/CatalogQueries.cs)
   (`CatalogQueries`) and read it in `LiveDatabaseReader`.

That's it: `ProjectObjectKind.For(kind)` then surfaces the metadata, `RawProjectObject` wraps it, and
the `ProjectObjectRegistry` includes it — diff/codegen/extract pick it up with **no further edits**.

## Checklist — a **finely-modelled** kind

A kind with its own structured record (like Table/View/Index) additionally needs: a `Definition`
record, an `ObjectIdentityComputer` overload (`Identify`/`StableIdOf`/`CanonicalHashOf`), a
`SqlEmitter` emitter, and a small `ProjectObjectBase` adapter
([`Extensibility/ProjectObjects.cs`](../../src/PgProj.Core/Extensibility/ProjectObjects.cs)).

## Status

The contract, registry, per-kind metadata, and adapters for all current kinds are in place and
covered by `ProjectObjectRegistryTests`. The remaining migration — routing `SchemaComparer` /
`DeployScriptGenerator` / `LiveDatabaseReader` to drive the registry so the legacy per-kind switches
can be deleted — is tracked as the completion of #44.

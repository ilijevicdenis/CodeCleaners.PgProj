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
2. Add **one row** to [`ObjectKindRegistry`](../../src/PgProj.Core/Extensibility/ObjectKindRegistry.cs)
   — the single per-kind table: compare-filter token, deploy `Phase`, extract `Folder`, DROP keyword +
   `DropTargetStyle`, and the `ComparesByIdentityOnly` / `IsDestructiveRecreate` flags. (This one row
   replaces what used to be edits to six separate `switch` statements in `RawObjectMeta` and
   `SchemaCompareObjectType` — those now read from the registry.)
3. Register its **catalog query** on the [`PostgresVersionProfile`](../../src/PgProj.Core/Versioning/CatalogQueries.cs)
   (`CatalogQueries`) and read it in `LiveDatabaseReader`.

That's it: `RawObjectMeta` / `SchemaCompareObjectType` / `ProjectObjectKind` all surface the metadata
from the registry row, `RawProjectObject` wraps it, and the `ProjectObjectRegistry` includes it —
diff/codegen/extract pick it up with **no further edits**. (A conformance test fails the build if an
`ObjectKind` has no registry row.)

## Checklist — a **finely-modelled** kind

A kind with its own structured record (like Table/View/Index) additionally needs: a `Definition`
record, an `ObjectIdentityComputer` overload (`Identify`/`StableIdOf`/`CanonicalHashOf`), a
`SqlEmitter` emitter, and a small `ProjectObjectBase` adapter
([`Extensibility/ProjectObjects.cs`](../../src/PgProj.Core/Extensibility/ProjectObjects.cs)).

## Status

The contract, registry, per-kind metadata, and adapters for all current kinds are in place and
covered by `ProjectObjectRegistryTests`. The per-kind `switch` statements in `RawObjectMeta` (phase /
drop / folder / compare flags) and `SchemaCompareObjectType.OfKind` have been **collapsed into the
single `ObjectKindRegistry` table** — those accessors are now thin reads over it (behavior proven
unchanged by the golden deploy-script tests).

Remaining for full #44 closure: `LiveDatabaseReader` still fans out to a per-kind `ReadXxxAsync`
method per kind (the catalog SQL itself already moved behind the `PostgresVersionProfile` in #43);
turning introspection into a fully registry-driven "each kind self-registers its reader" form is the
last increment.

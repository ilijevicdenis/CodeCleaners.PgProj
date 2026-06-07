// Pure form-state <-> table-JSON mapping for the graphical table designer (EP-DESIGNER #26).
//
// This module has NO dependency on the VS Code host or the webview DOM — it is the unit-testable core
// the panel and the webview both rely on. The designer NEVER builds SQL itself: it edits a structured
// `TableModelDto` (the exact shape the engine's `describe-table` emits and `emit-table` consumes) and
// hands it straight back to the engine, whose SqlEmitter is the single source of truth for the .sql.

import {
  DesignerColumnDto,
  DesignerForeignKeyDto,
  DesignerIndexDto,
  TableModelDto,
} from "../engine/contract";

/**
 * The mutable form state the webview edits. It is structurally the editable subset of `TableModelDto`,
 * carrying the view-only fields (otherConstraints / trailingOptions / companions) verbatim so they survive
 * a save untouched. Kept a plain data object so it serialises cleanly over the webview message channel.
 */
export interface TableFormState {
  schema: string;
  name: string;
  columns: DesignerColumnDto[];
  primaryKeyColumns: string[];
  primaryKeyName?: string;
  unique: { name?: string; columns: string[] }[];
  foreignKeys: DesignerForeignKeyDto[];
  checks: { name?: string; expression: string }[];
  indexes: DesignerIndexDto[];
  // ---- view-only pass-through (preserved verbatim across the round-trip) ----
  otherConstraints: string[];
  trailingOptions?: string;
  companions: string[];
}

/** Project the engine's table DTO into the editable form state the webview binds to. */
export function toFormState(dto: TableModelDto): TableFormState {
  return {
    schema: dto.schema,
    name: dto.name,
    columns: dto.columns.map((c) => ({ ...c })),
    primaryKeyColumns: dto.primaryKey ? [...dto.primaryKey.columns] : [],
    primaryKeyName: dto.primaryKey?.name,
    unique: dto.unique.map((u) => ({ name: u.name, columns: [...u.columns] })),
    foreignKeys: dto.foreignKeys.map((f) => ({
      ...f,
      columns: [...f.columns],
      referencedColumns: [...f.referencedColumns],
    })),
    checks: dto.checks.map((c) => ({ name: c.name, expression: c.expression })),
    indexes: dto.indexes.map((i) => ({ ...i, columns: [...i.columns] })),
    otherConstraints: [...dto.otherConstraints],
    trailingOptions: dto.trailingOptions,
    companions: [...dto.companions],
  };
}

/**
 * Map the edited form state back to a `TableModelDto` for `emit-table`. Empty optional fields are
 * normalised to undefined so the engine's omit-null contract produces the same .sql whether a field was
 * never set or cleared in the form. A table with no primary-key columns has no primary key.
 */
export function toTableModel(state: TableFormState): TableModelDto {
  return {
    schemaVersion: "1.0",
    verb: "describe-table",
    schema: state.schema,
    name: state.name,
    columns: state.columns.map(normalizeColumn),
    primaryKey:
      state.primaryKeyColumns.length > 0
        ? { name: blankToUndefined(state.primaryKeyName), columns: [...state.primaryKeyColumns] }
        : undefined,
    unique: state.unique
      .filter((u) => u.columns.length > 0)
      .map((u) => ({ name: blankToUndefined(u.name), columns: [...u.columns] })),
    foreignKeys: state.foreignKeys
      .filter((f) => f.columns.length > 0 && f.referencedTable.trim().length > 0)
      .map(normalizeForeignKey),
    checks: state.checks
      .filter((c) => c.expression.trim().length > 0)
      .map((c) => ({ name: blankToUndefined(c.name), expression: c.expression.trim() })),
    indexes: state.indexes
      .filter((i) => i.name.trim().length > 0 && i.columns.length > 0)
      .map(normalizeIndex),
    otherConstraints: [...state.otherConstraints],
    trailingOptions: blankToUndefined(state.trailingOptions),
    companions: [...state.companions],
  };
}

function normalizeColumn(c: DesignerColumnDto): DesignerColumnDto {
  return {
    name: c.name.trim(),
    dataType: c.dataType.trim(),
    nullable: c.nullable,
    default: blankToUndefined(c.default),
    identity: c.identity,
    identityKind: c.identity ? c.identityKind ?? "BY DEFAULT" : undefined,
    generated: blankToUndefined(c.generated),
    serial: c.serial,
  };
}

function normalizeForeignKey(f: DesignerForeignKeyDto): DesignerForeignKeyDto {
  return {
    name: blankToUndefined(f.name),
    columns: [...f.columns],
    referencedSchema: f.referencedSchema.trim() || "public",
    referencedTable: f.referencedTable.trim(),
    referencedColumns: f.referencedColumns.filter((c) => c.trim().length > 0),
    onDelete: blankToUndefined(f.onDelete),
    onUpdate: blankToUndefined(f.onUpdate),
  };
}

function normalizeIndex(i: DesignerIndexDto): DesignerIndexDto {
  return {
    name: i.name.trim(),
    unique: i.unique,
    columns: i.columns.filter((c) => c.trim().length > 0),
    method: blankToUndefined(i.method),
    where: blankToUndefined(i.where),
  };
}

/** Treat an empty/whitespace string as "not set" so it serialises as undefined (omit-null contract). */
export function blankToUndefined(s: string | undefined): string | undefined {
  if (s === undefined) {
    return undefined;
  }
  const t = s.trim();
  return t.length === 0 ? undefined : t;
}

/** Validate the editable shape before a save: a table needs a name and at least one named column. Returns
 * a list of human-readable problems (empty = valid). Pure, so the panel and tests share one rule set. */
export function validateForm(state: TableFormState): string[] {
  const problems: string[] = [];
  if (state.name.trim().length === 0) {
    problems.push("Table name is required.");
  }
  if (state.columns.length === 0) {
    problems.push("A table needs at least one column.");
  }
  state.columns.forEach((c, i) => {
    if (c.name.trim().length === 0) {
      problems.push(`Column ${i + 1} has no name.`);
    }
    if (c.dataType.trim().length === 0 && !c.serial) {
      problems.push(`Column "${c.name || i + 1}" has no data type.`);
    }
  });
  const seen = new Set<string>();
  for (const c of state.columns) {
    const key = c.name.trim().toLowerCase();
    if (key && seen.has(key)) {
      problems.push(`Duplicate column name "${c.name}".`);
    }
    seen.add(key);
  }
  return problems;
}

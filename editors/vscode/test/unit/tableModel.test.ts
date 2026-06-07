import { describe, it, expect } from "vitest";
import { TableModelDto } from "../../src/engine/contract";
import {
  toFormState,
  toTableModel,
  validateForm,
  blankToUndefined,
  TableFormState,
} from "../../src/designer/tableModel";

// A representative engine `describe-table` payload exercising each designer surface.
const sample: TableModelDto = {
  schemaVersion: "1.0",
  verb: "describe-table",
  schema: "app",
  name: "customer",
  columns: [
    { name: "id", dataType: "bigint", nullable: true, identity: true, identityKind: "ALWAYS", serial: false },
    { name: "code", dataType: "integer", nullable: false, identity: false, serial: false },
    { name: "full", dataType: "text", nullable: true, identity: false, generated: "code", serial: false },
  ],
  primaryKey: { name: "pk_customer", columns: ["id"] },
  unique: [{ name: "uq_code", columns: ["code"] }],
  foreignKeys: [
    {
      name: "fk_org",
      columns: ["code"],
      referencedSchema: "app",
      referencedTable: "org",
      referencedColumns: ["id"],
      onDelete: "CASCADE",
    },
  ],
  checks: [{ name: "ck_code", expression: "code > 0" }],
  indexes: [{ name: "ix_full", unique: true, columns: ["full"], where: "full IS NOT NULL" }],
  otherConstraints: ["EXCLUDE USING gist(code WITH =)"],
  trailingOptions: "PARTITION BY LIST (code)",
  companions: ["ALTER TABLE app.customer ENABLE ROW LEVEL SECURITY"],
};

describe("toFormState", () => {
  it("projects every editable surface into form state", () => {
    const s = toFormState(sample);
    expect(s.schema).toBe("app");
    expect(s.name).toBe("customer");
    expect(s.columns).toHaveLength(3);
    expect(s.primaryKeyColumns).toEqual(["id"]);
    expect(s.primaryKeyName).toBe("pk_customer");
    expect(s.unique[0].columns).toEqual(["code"]);
    expect(s.foreignKeys[0].referencedTable).toBe("org");
    expect(s.checks[0].expression).toBe("code > 0");
    expect(s.indexes[0].where).toBe("full IS NOT NULL");
  });

  it("carries the view-only fields verbatim", () => {
    const s = toFormState(sample);
    expect(s.otherConstraints).toEqual(["EXCLUDE USING gist(code WITH =)"]);
    expect(s.trailingOptions).toBe("PARTITION BY LIST (code)");
    expect(s.companions).toEqual(["ALTER TABLE app.customer ENABLE ROW LEVEL SECURITY"]);
  });

  it("clones arrays so editing form state does not mutate the source DTO", () => {
    const s = toFormState(sample);
    s.columns[0].name = "renamed";
    s.primaryKeyColumns.push("code");
    expect(sample.columns[0].name).toBe("id");
    expect(sample.primaryKey?.columns).toEqual(["id"]);
  });
});

describe("toFormState → toTableModel round-trip", () => {
  it("is structurally identity for a fully-populated table", () => {
    const round = toTableModel(toFormState(sample));
    expect(round.schema).toBe(sample.schema);
    expect(round.name).toBe(sample.name);
    expect(round.columns).toEqual(sample.columns);
    expect(round.primaryKey).toEqual(sample.primaryKey);
    expect(round.unique).toEqual(sample.unique);
    expect(round.foreignKeys).toEqual(sample.foreignKeys);
    expect(round.checks).toEqual(sample.checks);
    expect(round.indexes).toEqual(sample.indexes);
    expect(round.otherConstraints).toEqual(sample.otherConstraints);
    expect(round.trailingOptions).toBe(sample.trailingOptions);
    expect(round.companions).toEqual(sample.companions);
  });
});

describe("toTableModel normalisation", () => {
  function emptyForm(): TableFormState {
    return {
      schema: "app",
      name: "t",
      columns: [{ name: "id", dataType: "int", nullable: true, identity: false, serial: false }],
      primaryKeyColumns: [],
      unique: [],
      foreignKeys: [],
      checks: [],
      indexes: [],
      otherConstraints: [],
      companions: [],
    };
  }

  it("drops the primary key when no columns are selected", () => {
    const m = toTableModel(emptyForm());
    expect(m.primaryKey).toBeUndefined();
  });

  it("keeps a primary key once columns are added", () => {
    const f = emptyForm();
    f.primaryKeyColumns = ["id"];
    f.primaryKeyName = "pk_t";
    expect(toTableModel(f).primaryKey).toEqual({ name: "pk_t", columns: ["id"] });
  });

  it("filters out incomplete unique / fk / check / index rows", () => {
    const f = emptyForm();
    f.unique = [{ name: "u", columns: [] }]; // no columns → dropped
    f.foreignKeys = [
      { name: "fk", columns: [], referencedSchema: "app", referencedTable: "", referencedColumns: [] },
    ]; // no columns / table → dropped
    f.checks = [{ name: "c", expression: "  " }]; // blank expr → dropped
    f.indexes = [{ name: "", unique: false, columns: ["id"] }]; // no name → dropped
    const m = toTableModel(f);
    expect(m.unique).toHaveLength(0);
    expect(m.foreignKeys).toHaveLength(0);
    expect(m.checks).toHaveLength(0);
    expect(m.indexes).toHaveLength(0);
  });

  it("defaults identityKind to BY DEFAULT for an identity column missing a kind", () => {
    const f = emptyForm();
    f.columns = [{ name: "id", dataType: "int", nullable: false, identity: true, serial: false }];
    expect(toTableModel(f).columns[0].identityKind).toBe("BY DEFAULT");
  });

  it("clears identityKind for a non-identity column", () => {
    const f = emptyForm();
    f.columns = [
      { name: "id", dataType: "int", nullable: false, identity: false, identityKind: "ALWAYS", serial: false },
    ];
    expect(toTableModel(f).columns[0].identityKind).toBeUndefined();
  });

  it("normalises blank optional strings to undefined", () => {
    const f = emptyForm();
    f.columns = [{ name: "id", dataType: "int", nullable: true, identity: false, serial: false, default: "   " }];
    f.trailingOptions = "  ";
    const m = toTableModel(f);
    expect(m.columns[0].default).toBeUndefined();
    expect(m.trailingOptions).toBeUndefined();
  });
});

describe("validateForm", () => {
  it("accepts a valid table", () => {
    expect(validateForm(toFormState(sample))).toEqual([]);
  });

  it("rejects a nameless table with no columns", () => {
    const problems = validateForm({
      schema: "app",
      name: "",
      columns: [],
      primaryKeyColumns: [],
      unique: [],
      foreignKeys: [],
      checks: [],
      indexes: [],
      otherConstraints: [],
      companions: [],
    });
    expect(problems).toContain("Table name is required.");
    expect(problems).toContain("A table needs at least one column.");
  });

  it("flags a column with no name and a duplicate column", () => {
    const problems = validateForm({
      schema: "app",
      name: "t",
      columns: [
        { name: "", dataType: "int", nullable: true, identity: false, serial: false },
        { name: "x", dataType: "int", nullable: true, identity: false, serial: false },
        { name: "X", dataType: "int", nullable: true, identity: false, serial: false },
      ],
      primaryKeyColumns: [],
      unique: [],
      foreignKeys: [],
      checks: [],
      indexes: [],
      otherConstraints: [],
      companions: [],
    });
    expect(problems.some((p) => p.includes("has no name"))).toBe(true);
    expect(problems.some((p) => p.includes("Duplicate column name"))).toBe(true);
  });

  it("allows a serial column without an explicit data type", () => {
    const problems = validateForm({
      schema: "app",
      name: "t",
      columns: [{ name: "id", dataType: "", nullable: false, identity: false, serial: true }],
      primaryKeyColumns: [],
      unique: [],
      foreignKeys: [],
      checks: [],
      indexes: [],
      otherConstraints: [],
      companions: [],
    });
    expect(problems).toEqual([]);
  });
});

describe("blankToUndefined", () => {
  it("maps undefined and blank to undefined, trims otherwise", () => {
    expect(blankToUndefined(undefined)).toBeUndefined();
    expect(blankToUndefined("   ")).toBeUndefined();
    expect(blankToUndefined("  x  ")).toBe("x");
  });
});

import { describe, it, expect } from "vitest";
import * as fs from "fs";
import * as path from "path";
import {
  variablesToCli,
  toPublishProfileJson,
  scriptSelectedChanges,
  PublishFormState,
} from "../../src/webviews/protocol";
import { SchemaCompareReportDto } from "../../src/engine/schemaCompare";

const diff = JSON.parse(
  fs.readFileSync(path.join(__dirname, "../fixtures/compare-diff.json"), "utf8")
) as SchemaCompareReportDto;

describe("variablesToCli", () => {
  it("renders name=value entries and drops rows with a blank name", () => {
    expect(
      variablesToCli([
        { name: "Env", value: "Prod" },
        { name: "  ", value: "ignored" },
        { name: " Region ", value: "eu" },
      ])
    ).toEqual(["Env=Prod", "Region=eu"]);
  });
});

describe("toPublishProfileJson", () => {
  const base: PublishFormState = {
    connection: "Host=db;Password=secret",
    connectionName: "prod",
    allowDrops: true,
    noTransaction: false,
    variables: [{ name: "Env", value: "Prod" }],
    targetVersion: "18",
  };

  it("writes only whitelisted, non-secret fields (no connection string)", () => {
    const json = toPublishProfileJson(base);
    const obj = JSON.parse(json);
    expect(obj).toEqual({
      targetPostgresVersion: "18",
      connectionName: "prod",
      variables: { Env: "Prod" },
      options: { allowDrops: true },
    });
    expect(json).not.toContain("secret");
    expect(json).not.toContain("connection\"");
  });

  it("maps noTransaction -> wrapInTransaction:false and omits empty blocks", () => {
    const json = toPublishProfileJson({
      connection: "",
      connectionName: "",
      allowDrops: false,
      noTransaction: true,
      variables: [],
    });
    const obj = JSON.parse(json);
    expect(obj).toEqual({ options: { wrapInTransaction: false } });
    expect(obj.variables).toBeUndefined();
    expect(obj.connectionName).toBeUndefined();
  });
});

describe("scriptSelectedChanges", () => {
  it("emits only the checked changes, ordered by deploy phase", () => {
    const sql = scriptSelectedChanges(diff, ["c1", "c3"]);
    // c3 (phase 1, DROP) must come before c1 (phase 2, CREATE TABLE); c2 is excluded.
    expect(sql.indexOf("DROP TABLE afd.legacy")).toBeLessThan(sql.indexOf("CREATE TABLE afd.customers"));
    expect(sql).not.toContain("customers_email_idx");
    expect(sql).toContain("2 of 3 change(s) selected");
  });

  it("returns a no-op comment when nothing is selected", () => {
    expect(scriptSelectedChanges(diff, [])).toContain("No changes selected");
  });
});

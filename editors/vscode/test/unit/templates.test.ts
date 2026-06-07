import { describe, it, expect } from "vitest";
import { renderTemplate, templateRelativePath } from "../../src/templates";
import {
  setTargetVersionInProjectXml,
  readDefaultSchemaFromXml,
  readTargetVersionFromXml,
} from "../../src/projectFile";

describe("object templates", () => {
  it("renders a CREATE TABLE with the schema-qualified name", () => {
    const sql = renderTemplate("table", "afd", "customers");
    expect(sql).toContain("CREATE TABLE afd.customers");
    expect(sql).toContain("PRIMARY KEY");
  });

  it("renders a CREATE FUNCTION skeleton", () => {
    const sql = renderTemplate("function", "afd", "do_thing");
    expect(sql).toContain("CREATE FUNCTION afd.do_thing()");
    expect(sql).toContain("LANGUAGE plpgsql");
  });

  it("suggests a folder-based relative path", () => {
    expect(templateRelativePath("table", "afd", "customers")).toBe("Tables/afd.customers.sql");
    expect(templateRelativePath("view", "afd", "active")).toBe("Views/afd.active.sql");
    expect(templateRelativePath("function", "afd", "f")).toBe("Functions/afd.f.sql");
  });
});

describe("project-file editing", () => {
  const xml = `<Project DefaultTargets="Build">
  <PropertyGroup>
    <Name>AllFeaturesDb</Name>
    <DefaultSchema>afd</DefaultSchema>
    <TargetPostgresVersion>18</TargetPostgresVersion>
  </PropertyGroup>
</Project>`;

  it("replaces an existing TargetPostgresVersion in place", () => {
    const out = setTargetVersionInProjectXml(xml, "16");
    expect(out).toContain("<TargetPostgresVersion>16</TargetPostgresVersion>");
    expect(out).not.toContain("<TargetPostgresVersion>18</TargetPostgresVersion>");
  });

  it("injects TargetPostgresVersion when absent", () => {
    const noVer = `<Project>
  <PropertyGroup>
    <Name>X</Name>
  </PropertyGroup>
</Project>`;
    const out = setTargetVersionInProjectXml(noVer, "17");
    expect(out).toContain("<TargetPostgresVersion>17</TargetPostgresVersion>");
    expect(out.indexOf("<TargetPostgresVersion>")).toBeLessThan(out.indexOf("</PropertyGroup>"));
  });

  it("reads the default schema", () => {
    expect(readDefaultSchemaFromXml(xml)).toBe("afd");
    expect(readDefaultSchemaFromXml("<Project></Project>")).toBe("public");
  });

  it("reads the target version (undefined when not pinned)", () => {
    expect(readTargetVersionFromXml(xml)).toBe("18");
    expect(readTargetVersionFromXml("<Project></Project>")).toBeUndefined();
  });
});

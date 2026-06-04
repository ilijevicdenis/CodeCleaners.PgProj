import { describe, it, expect } from "vitest";
import * as fs from "fs";
import * as path from "path";
import { ModelTreeDto } from "../../src/engine/contract";
import { buildProjectNode, buildObjectFolders } from "../../src/views/treeModel";

const fixture = JSON.parse(
  fs.readFileSync(path.join(__dirname, "../fixtures/model-tree.json"), "utf8")
) as ModelTreeDto;

describe("treeModel", () => {
  it("builds a project root node labelled by the project name", () => {
    const root = buildProjectNode("/ws/AllFeaturesDb/AllFeaturesDb.pgproj", fixture);
    expect(root.kind).toBe("project");
    expect(root.label).toBe("AllFeaturesDb");
    expect(root.projectFile).toBe("/ws/AllFeaturesDb/AllFeaturesDb.pgproj");
    expect(root.children.length).toBeGreaterThan(0);
    expect(root.children.every((c) => c.kind === "folder")).toBe(true);
  });

  it("groups objects into kind folders in the canonical order (Schemas before Tables)", () => {
    const folders = buildObjectFolders(fixture);
    const labels = folders.map((f) => f.label);
    expect(labels).toContain("Schemas");
    expect(labels).toContain("Tables");
    expect(labels).toContain("Functions");
    expect(labels.indexOf("Schemas")).toBeLessThan(labels.indexOf("Tables"));
    expect(labels.indexOf("Tables")).toBeLessThan(labels.indexOf("Functions"));
  });

  it("places every model-tree node under exactly one folder", () => {
    const folders = buildObjectFolders(fixture);
    const totalObjects = folders.reduce((n, f) => n + f.children.length, 0);
    expect(totalObjects).toBe(fixture.nodes.length);
  });

  it("nests a table's columns as child nodes", () => {
    const tables = buildObjectFolders(fixture).find((f) => f.objectKind === "table");
    expect(tables).toBeDefined();
    const withColumns = tables!.children.find((t) => t.children.length > 0);
    expect(withColumns).toBeDefined();
    expect(withColumns!.children.every((c) => c.kind === "child" && c.objectKind === "column")).toBe(true);
  });

  it("sorts objects within a folder alphabetically", () => {
    const functions = buildObjectFolders(fixture).find((f) => f.objectKind === "function")!;
    const labels = functions.children.map((c) => c.label);
    const sorted = [...labels].sort((a, b) => a.localeCompare(b));
    expect(labels).toEqual(sorted);
  });

  it("falls back to a pluralised folder label for unknown kinds", () => {
    const tree: ModelTreeDto = {
      schemaVersion: "1.0",
      verb: "model-tree",
      project: "X",
      summary: { schemas: 0, tables: 0, indexes: 0, views: 0, sequences: 0, functions: 0, objects: 1 },
      nodes: [{ kind: "weirdThing", schema: "s", name: "n", qualifiedName: "s.n", line: 0, col: 0 }],
    };
    const folders = buildObjectFolders(tree);
    expect(folders[0].label).toBe("Weird Things");
  });
});

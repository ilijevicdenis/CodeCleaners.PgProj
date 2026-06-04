import { describe, it, expect } from "vitest";
import * as fs from "fs";
import * as path from "path";
import { AnalyzeReportDto, BuildReportDto } from "../../src/engine/contract";
import { mapDiagnostics, mapDiagnostic, toRange } from "../../src/engine/diagnostics";

const analyze = JSON.parse(
  fs.readFileSync(path.join(__dirname, "../fixtures/analyze.json"), "utf8")
) as AnalyzeReportDto;
const build = JSON.parse(
  fs.readFileSync(path.join(__dirname, "../fixtures/build.json"), "utf8")
) as BuildReportDto;

describe("diagnostics mapper", () => {
  it("converts 1-based contract line/col to 0-based ranges", () => {
    const r = toRange({ ruleId: "X", severity: "Warning", message: "m", line: 3, col: 5 });
    expect(r.startLine).toBe(2);
    expect(r.startCol).toBe(4);
    expect(r.endLine).toBe(2);
  });

  it("maps unknown (0) line/col to the top of the file", () => {
    const r = toRange({ ruleId: "X", severity: "Info", message: "m", line: 0, col: 0 });
    expect(r.startLine).toBe(0);
    expect(r.startCol).toBe(0);
  });

  it("maps contract severities to mapped severities", () => {
    expect(mapDiagnostic({ ruleId: "X", severity: "Error", message: "m", line: 1, col: 1 }).severity).toBe("error");
    expect(mapDiagnostic({ ruleId: "X", severity: "Warning", message: "m", line: 1, col: 1 }).severity).toBe("warning");
    expect(mapDiagnostic({ ruleId: "X", severity: "Info", message: "m", line: 1, col: 1 }).severity).toBe("info");
  });

  it("groups analyzer findings by their project-relative file", () => {
    const byFile = mapDiagnostics(analyze.diagnostics, "AllFeaturesDb.pgproj");
    const total = Object.values(byFile).reduce((n, arr) => n + arr.length, 0);
    expect(total).toBe(analyze.diagnostics.length);
    // The first analyzer finding in the fixture anchors to a Functions/*.sql file.
    expect(Object.keys(byFile).some((f) => f.startsWith("Functions/"))).toBe(true);
    const first = analyze.diagnostics[0];
    const group = byFile[first.file!];
    expect(group.some((m) => m.ruleId === first.ruleId)).toBe(true);
  });

  it("attaches file-less diagnostics to the fallback project file", () => {
    const byFile = mapDiagnostics(
      [{ ruleId: "BUILD", severity: "Error", message: "no anchor", line: 0, col: 0 }],
      "Proj.pgproj"
    );
    expect(byFile["Proj.pgproj"]).toBeDefined();
    expect(byFile["Proj.pgproj"][0].message).toBe("no anchor");
  });

  it("produces no groups for a clean build", () => {
    const byFile = mapDiagnostics(build.diagnostics, "AllFeaturesDb.pgproj");
    expect(Object.keys(byFile).length).toBe(0);
  });
});

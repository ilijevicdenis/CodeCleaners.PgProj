import { describe, it, expect } from "vitest";
import * as fs from "fs";
import * as path from "path";
import {
  PgProjEngine,
  RunResult,
  Spawner,
  buildCompareTwoWayArgs,
} from "../../src/engine/engine";
const diffJson = fs.readFileSync(path.join(__dirname, "../fixtures/compare-diff.json"), "utf8");

function recordingSpawner(stdout: string): {
  spawner: Spawner;
  calls: Array<{ command: string; args: string[]; cwd: string }>;
} {
  const calls: Array<{ command: string; args: string[]; cwd: string }> = [];
  const spawner: Spawner = async (command, args, cwd): Promise<RunResult> => {
    calls.push({ command, args, cwd });
    return { exitCode: 0, stdout, stderr: "" };
  };
  return { spawner, calls };
}

describe("buildCompareTwoWayArgs", () => {
  it("sets --source/--target and omits --format json (runJson adds it)", () => {
    expect(buildCompareTwoWayArgs("a.pgproj", "Host=db", {})).toEqual([
      "compare",
      "--source",
      "a.pgproj",
      "--target",
      "Host=db",
    ]);
  });

  it("threads allow-drops, excludes (repeatable) and -o outFile", () => {
    expect(
      buildCompareTwoWayArgs("a.pgproj", "b.pgpkg", {
        allowDrops: true,
        exclude: ["index", "trigger"],
        outFile: "/tmp/diff.json",
      })
    ).toEqual([
      "compare",
      "--source",
      "a.pgproj",
      "--target",
      "b.pgpkg",
      "--allow-drops",
      "--exclude",
      "index",
      "--exclude",
      "trigger",
      "-o",
      "/tmp/diff.json",
    ]);
  });
});

describe("PgProjEngine.compareTwoWay", () => {
  it("invokes compare --source/--target --format json and parses the diff report", async () => {
    const { spawner, calls } = recordingSpawner(diffJson);
    const engine = new PgProjEngine({ cliPath: "pgproj", dotnetPath: "dotnet" }, spawner);
    const report = await engine.compareTwoWay("a.pgproj", "Host=db", "/ws", { outFile: "/tmp/d.json" });
    expect(calls[0].args).toEqual([
      "compare",
      "--source",
      "a.pgproj",
      "--target",
      "Host=db",
      "-o",
      "/tmp/d.json",
      "--format",
      "json",
    ]);
    expect(report.changeCount).toBe(3);
    expect(report.destructiveCount).toBe(1);
    expect(report.changes.map((c) => c.id)).toEqual(["c1", "c2", "c3"]);
  });

  it("rejects an unsupported schemaVersion major", async () => {
    const { spawner } = recordingSpawner(JSON.stringify({ schemaVersion: "2.0", verb: "compare" }));
    const engine = new PgProjEngine({ cliPath: "pgproj", dotnetPath: "dotnet" }, spawner);
    await expect(engine.compareTwoWay("a", "b", "/ws")).rejects.toThrow(/schemaVersion/);
  });
});

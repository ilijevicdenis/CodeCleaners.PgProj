import { describe, it, expect } from "vitest";
import * as fs from "fs";
import * as path from "path";
import {
  PgProjEngine,
  RunResult,
  Spawner,
  resolveInvocation,
  buildPublishArgs,
} from "../../src/engine/engine";

const modelTreeJson = fs.readFileSync(path.join(__dirname, "../fixtures/model-tree.json"), "utf8");
const buildJson = fs.readFileSync(path.join(__dirname, "../fixtures/build.json"), "utf8");
const analyzeJson = fs.readFileSync(path.join(__dirname, "../fixtures/analyze.json"), "utf8");

/** A spawner that records the invocation and returns a canned stdout. */
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

describe("resolveInvocation", () => {
  it("invokes the CLI directly when cliPath is an exe name", () => {
    const inv = resolveInvocation({ cliPath: "pgproj", dotnetPath: "dotnet" }, ["build", "x.pgproj"]);
    expect(inv.command).toBe("pgproj");
    expect(inv.args).toEqual(["build", "x.pgproj"]);
  });

  it("routes a .dll through the dotnet host", () => {
    const inv = resolveInvocation(
      { cliPath: "/eng/PgProj.Cli.dll", dotnetPath: "dotnet" },
      ["model-tree", "x.pgproj"]
    );
    expect(inv.command).toBe("dotnet");
    expect(inv.args).toEqual(["/eng/PgProj.Cli.dll", "model-tree", "x.pgproj"]);
  });
});

describe("buildPublishArgs", () => {
  it("includes connection and threads through options + sqlcmd vars", () => {
    const args = buildPublishArgs("p.pgproj", "Host=db", {
      allowDrops: true,
      noTransaction: true,
      variables: ["Env=Prod", "Region=eu"],
    });
    expect(args).toEqual([
      "publish",
      "p.pgproj",
      "--connection",
      "Host=db",
      "--allow-drops",
      "--no-transaction",
      "--var",
      "Env=Prod",
      "--var",
      "Region=eu",
    ]);
  });

  it("omits optional flags when not set", () => {
    expect(buildPublishArgs("p.pgproj", "c", {})).toEqual([
      "publish",
      "p.pgproj",
      "--connection",
      "c",
    ]);
  });
});

describe("PgProjEngine command argv", () => {
  it("model-tree appends --format json and parses the tree", async () => {
    const { spawner, calls } = recordingSpawner(modelTreeJson);
    const engine = new PgProjEngine({ cliPath: "pgproj", dotnetPath: "dotnet" }, spawner);
    const tree = await engine.modelTree("AllFeaturesDb.pgproj", "/ws");
    expect(calls[0].command).toBe("pgproj");
    expect(calls[0].args).toEqual(["model-tree", "AllFeaturesDb.pgproj", "--format", "json"]);
    expect(calls[0].cwd).toBe("/ws");
    expect(tree.project).toBe("AllFeaturesDb");
    expect(tree.nodes.length).toBeGreaterThan(0);
  });

  it("build appends --format json and returns the parsed report", async () => {
    const { spawner, calls } = recordingSpawner(buildJson);
    const engine = new PgProjEngine({ cliPath: "pgproj", dotnetPath: "dotnet" }, spawner);
    const { report } = await engine.build("AllFeaturesDb.pgproj", "/ws");
    expect(calls[0].args).toEqual(["build", "AllFeaturesDb.pgproj", "--format", "json"]);
    expect(report.success).toBe(true);
  });

  it("analyze passes --strict before --format json", async () => {
    const { spawner, calls } = recordingSpawner(analyzeJson);
    const engine = new PgProjEngine({ cliPath: "pgproj", dotnetPath: "dotnet" }, spawner);
    await engine.analyze("AllFeaturesDb.pgproj", "/ws", true);
    expect(calls[0].args).toEqual(["analyze", "AllFeaturesDb.pgproj", "--strict", "--format", "json"]);
  });

  it("compare builds the connection + allow-drops argv", async () => {
    const compareJson = JSON.stringify({
      schemaVersion: "1.0",
      verb: "compare",
      project: "X",
      inSync: false,
      changeCount: 1,
      destructiveCount: 0,
      changes: [{ kind: "CreateTableChange", description: "create t", destructive: false, phase: 1 }],
    });
    const { spawner, calls } = recordingSpawner(compareJson);
    const engine = new PgProjEngine({ cliPath: "pgproj", dotnetPath: "dotnet" }, spawner);
    const report = await engine.compare("p.pgproj", "Host=db", "/ws", true);
    expect(calls[0].args).toEqual([
      "compare",
      "p.pgproj",
      "--connection",
      "Host=db",
      "--allow-drops",
      "--format",
      "json",
    ]);
    expect(report.changeCount).toBe(1);
  });

  it("publish dry-run appends --dry-run --format json", async () => {
    const planJson = JSON.stringify({
      schemaVersion: "1.0",
      verb: "publish",
      project: "X",
      dryRun: true,
      inSync: false,
      changeCount: 2,
      destructiveCount: 1,
      changes: [],
      script: "BEGIN;\n...\nCOMMIT;",
    });
    const { spawner, calls } = recordingSpawner(planJson);
    const engine = new PgProjEngine({ cliPath: "pgproj", dotnetPath: "dotnet" }, spawner);
    const plan = await engine.publishDryRun("p.pgproj", "Host=db", "/ws", { allowDrops: true });
    expect(calls[0].args).toEqual([
      "publish",
      "p.pgproj",
      "--connection",
      "Host=db",
      "--allow-drops",
      "--dry-run",
      "--format",
      "json",
    ]);
    expect(plan.script).toContain("COMMIT");
  });

  it("rejects an unsupported schemaVersion major", async () => {
    const { spawner } = recordingSpawner(JSON.stringify({ schemaVersion: "2.0", verb: "build" }));
    const engine = new PgProjEngine({ cliPath: "pgproj", dotnetPath: "dotnet" }, spawner);
    await expect(engine.build("p.pgproj", "/ws")).rejects.toThrow(/schemaVersion/);
  });

  it("throws a helpful error when the engine emits no JSON", async () => {
    const spawner: Spawner = async () => ({ exitCode: 1, stdout: "", stderr: "boom" });
    const engine = new PgProjEngine({ cliPath: "pgproj", dotnetPath: "dotnet" }, spawner);
    await expect(engine.modelTree("p.pgproj", "/ws")).rejects.toThrow(/boom/);
  });
});

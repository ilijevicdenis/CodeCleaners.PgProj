// The engine client: the *only* place the extension shells out to the pgproj CLI. Every command
// handler funnels through here, which keeps the spawn/arg/JSON-parse logic in one testable unit.
//
// Resolution order for the executable (mirrors the MS SQL Database Projects "find sqlproj engine"
// approach): the `pgproj.cliPath` setting (default "pgproj", resolved on PATH). If that path ends in
// ".dll" we invoke it via the dotnet host (`pgproj.dotnetPath`, default "dotnet"), exactly like
// running `dotnet PgProj.Cli.dll` — this lets a dev point at a freshly-built engine without packaging.

import { spawn } from "child_process";
import {
  AnalyzeReportDto,
  assertSchemaVersion,
  BuildReportDto,
  CompareReportDto,
  ModelTreeDto,
  PublishPlanDto,
} from "./contract";
import { SchemaCompareReportDto } from "./schemaCompare";

export interface EngineConfig {
  /** Path to the pgproj CLI (or a .dll). */
  cliPath: string;
  /** Path to the dotnet host, used when cliPath ends in .dll. */
  dotnetPath: string;
}

export interface RunResult {
  exitCode: number;
  stdout: string;
  stderr: string;
}

/** Abstracts process launching so unit tests can inject a fake without spawning anything. */
export type Spawner = (
  command: string,
  args: string[],
  cwd: string
) => Promise<RunResult>;

/** The real spawner: launches a child process and collects its stdio. */
export const nodeSpawner: Spawner = (command, args, cwd) =>
  new Promise<RunResult>((resolve, reject) => {
    const child = spawn(command, args, { cwd, shell: false });
    let stdout = "";
    let stderr = "";
    child.stdout.on("data", (d) => (stdout += d.toString()));
    child.stderr.on("data", (d) => (stderr += d.toString()));
    child.on("error", reject);
    child.on("close", (code) => resolve({ exitCode: code ?? 0, stdout, stderr }));
  });

/**
 * Builds the [command, args] pair for a pgproj verb. Exposed (and pure) so a unit test can assert the
 * exact argv each command handler produces without launching a process.
 */
export function resolveInvocation(
  config: EngineConfig,
  verbArgs: string[]
): { command: string; args: string[] } {
  if (config.cliPath.toLowerCase().endsWith(".dll")) {
    return { command: config.dotnetPath, args: [config.cliPath, ...verbArgs] };
  }
  return { command: config.cliPath, args: verbArgs };
}

export class PgProjEngine {
  constructor(
    private readonly config: EngineConfig,
    private readonly spawner: Spawner = nodeSpawner
  ) {}

  /** Run a raw verb (no JSON parsing). The caller owns interpreting stdout/stderr/exitCode. */
  async run(verbArgs: string[], cwd: string): Promise<RunResult> {
    const { command, args } = resolveInvocation(this.config, verbArgs);
    return this.spawner(command, args, cwd);
  }

  /** Run a `--format json` verb and parse + version-check its single JSON document. */
  private async runJson<T extends { schemaVersion: string }>(
    verbArgs: string[],
    cwd: string
  ): Promise<{ result: RunResult; data: T }> {
    const result = await this.run([...verbArgs, "--format", "json"], cwd);
    const text = result.stdout.trim();
    if (!text) {
      throw new Error(
        `pgproj produced no JSON output.${result.stderr ? ` stderr: ${result.stderr.trim()}` : ""}`
      );
    }
    let data: T;
    try {
      data = JSON.parse(text) as T;
    } catch (e) {
      throw new Error(
        `Failed to parse pgproj JSON output: ${(e as Error).message}\n${text.slice(0, 500)}`
      );
    }
    assertSchemaVersion(data.schemaVersion);
    return { result, data };
  }

  async modelTree(projectFile: string, cwd: string): Promise<ModelTreeDto> {
    const { data } = await this.runJson<ModelTreeDto>(["model-tree", projectFile], cwd);
    return data;
  }

  async build(
    projectFile: string,
    cwd: string,
    extraArgs: string[] = []
  ): Promise<{ result: RunResult; report: BuildReportDto }> {
    const { result, data } = await this.runJson<BuildReportDto>(
      ["build", projectFile, ...extraArgs],
      cwd
    );
    return { result, report: data };
  }

  async analyze(
    projectFile: string,
    cwd: string,
    strict = false
  ): Promise<{ result: RunResult; report: AnalyzeReportDto }> {
    const args = ["analyze", projectFile];
    if (strict) {
      args.push("--strict");
    }
    const { result, data } = await this.runJson<AnalyzeReportDto>(args, cwd);
    return { result, report: data };
  }

  async compare(
    projectFile: string,
    connection: string,
    cwd: string,
    allowDrops = false
  ): Promise<CompareReportDto> {
    const args = ["compare", projectFile, "--connection", connection];
    if (allowDrops) {
      args.push("--allow-drops");
    }
    const { data } = await this.runJson<CompareReportDto>(args, cwd);
    return data;
  }

  /**
   * Two-way Schema Compare (EP-SCHEMACOMPARE): each endpoint is a project/.pgpkg/.schema.snapshot/live
   * DB. Emits the structured, selectable `SchemaCompareReportDto`. `--format json` mirrors the report to
   * stdout (we parse that); `-o <path>` additionally writes it to a file for tooling — passed when given.
   */
  async compareTwoWay(
    source: string,
    target: string,
    cwd: string,
    opts: CompareTwoWayOptions = {}
  ): Promise<SchemaCompareReportDto> {
    const args = buildCompareTwoWayArgs(source, target, opts);
    const { data } = await this.runJson<SchemaCompareReportDto>(args, cwd);
    return data;
  }

  async publishDryRun(
    projectFile: string,
    connection: string,
    cwd: string,
    opts: PublishOptions
  ): Promise<PublishPlanDto> {
    const args = buildPublishArgs(projectFile, connection, opts);
    args.push("--dry-run");
    const { data } = await this.runJson<PublishPlanDto>(args, cwd);
    return data;
  }

  /** Run an actual publish (text output — there is no JSON contract for a live publish yet). */
  async publish(
    projectFile: string,
    connection: string,
    cwd: string,
    opts: PublishOptions
  ): Promise<RunResult> {
    return this.run(buildPublishArgs(projectFile, connection, opts), cwd);
  }
}

export interface CompareTwoWayOptions {
  /** Drop objects present in target but not source (the `--allow-drops` flag). */
  allowDrops?: boolean;
  /** Object-types to exclude from the diff (repeatable `--exclude`). */
  exclude?: string[];
  /** Also write the diff JSON to this path (`-o`), in addition to mirroring it to stdout. */
  outFile?: string;
}

/**
 * Pure arg-builder for the two-way compare (shared by the engine + a unit test). Does NOT append
 * `--format json` — `runJson` adds that — but it does set `--source`/`--target` and pass-through options.
 */
export function buildCompareTwoWayArgs(
  source: string,
  target: string,
  opts: CompareTwoWayOptions
): string[] {
  const args = ["compare", "--source", source, "--target", target];
  if (opts.allowDrops) {
    args.push("--allow-drops");
  }
  for (const ex of opts.exclude ?? []) {
    args.push("--exclude", ex);
  }
  if (opts.outFile) {
    args.push("-o", opts.outFile);
  }
  return args;
}

export interface PublishOptions {
  allowDrops?: boolean;
  noTransaction?: boolean;
  /** SQLCMD-style variables, "name=value". Passed through as `--var name=value` entries. */
  variables?: string[];
  /** Optional `.pgpublish.json` to layer under the CLI flags (`--profile`). */
  profile?: string;
}

/** Pure arg-builder for publish, shared by dry-run and live publish — kept testable. */
export function buildPublishArgs(
  projectFile: string,
  connection: string,
  opts: PublishOptions
): string[] {
  const args = ["publish", projectFile, "--connection", connection];
  if (opts.profile) {
    args.push("--profile", opts.profile);
  }
  if (opts.allowDrops) {
    args.push("--allow-drops");
  }
  if (opts.noTransaction) {
    args.push("--no-transaction");
  }
  for (const v of opts.variables ?? []) {
    args.push("--var", v);
  }
  return args;
}

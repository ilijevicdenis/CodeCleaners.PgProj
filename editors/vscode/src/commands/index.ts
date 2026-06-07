// Command handlers. Each shells out through PgProjEngine, streams output to the shared OutputChannel,
// and (for build/analyze) pushes diagnostics into the Problems panel. Commands accept an optional
// TreeNode (from a context-menu invocation); when absent (palette invocation) they resolve the active
// project via pickProject().

import * as fs from "fs";
import * as path from "path";
import * as vscode from "vscode";
import { PgProjEngine, PublishOptions } from "../engine/engine";
import { DiagnosticsController } from "../diagnosticsController";
import { ProjectsTreeProvider } from "../views/projectsTree";
import { TreeNode } from "../views/treeModel";
import {
  OBJECT_TEMPLATES,
  ObjectTemplateKind,
  renderTemplate,
  templateRelativePath,
} from "../templates";
import { readDefaultSchemaFromXml, setTargetVersionInProjectXml } from "../projectFile";
import { TableDesignerPanel } from "../designer/designerPanel";

export interface CommandContext {
  engine: PgProjEngine;
  diagnostics: DiagnosticsController;
  tree: ProjectsTreeProvider;
  output: vscode.OutputChannel;
  /** The extension context, for webview-owning commands (the table designer). */
  extensionContext: vscode.ExtensionContext;
}

/** Resolve the project file path from a context-menu node, else prompt across discovered projects. */
async function resolveProjectFile(node?: TreeNode): Promise<string | undefined> {
  if (node?.projectFile) {
    return node.projectFile;
  }
  const files = await vscode.workspace.findFiles("**/*.pgproj", "**/{node_modules,bin,obj}/**");
  if (files.length === 0) {
    void vscode.window.showWarningMessage("No .pgproj files found in this workspace.");
    return undefined;
  }
  if (files.length === 1) {
    return files[0].fsPath;
  }
  const pick = await vscode.window.showQuickPick(
    files.map((f) => ({ label: path.basename(f.fsPath), description: f.fsPath, fsPath: f.fsPath })),
    { placeHolder: "Select a database project" }
  );
  return pick?.fsPath;
}

function run(ctx: CommandContext, title: string, fn: () => Promise<void>): Thenable<void> {
  return vscode.window.withProgress(
    { location: vscode.ProgressLocation.Notification, title, cancellable: false },
    async () => {
      try {
        await fn();
      } catch (err) {
        ctx.output.appendLine(`error: ${String(err)}`);
        void vscode.window.showErrorMessage(`${title} failed: ${(err as Error).message}`);
      }
    }
  );
}

// ---- Build ---------------------------------------------------------------------------------------

export async function buildCommand(ctx: CommandContext, node?: TreeNode): Promise<void> {
  const projectFile = await resolveProjectFile(node);
  if (!projectFile) {
    return;
  }
  await run(ctx, `Building ${path.basename(projectFile)}`, async () => {
    const cwd = path.dirname(projectFile);
    const { result, report } = await ctx.engine.build(projectFile, cwd);
    ctx.output.appendLine(
      `[build] ${report.project}: success=${report.success} ` +
        `(${report.summary.errors} error, ${report.summary.warnings} warning) ` +
        `tables=${report.model.tables} functions=${report.model.functions}`
    );
    ctx.diagnostics.setForProject(projectFile, report.diagnostics);
    ctx.tree.refresh();
    if (result.stderr.trim()) {
      ctx.output.appendLine(result.stderr.trim());
    }
    if (report.success) {
      void vscode.window.showInformationMessage(`Build succeeded: ${report.project}`);
    } else {
      void vscode.window.showErrorMessage(
        `Build failed: ${report.project} (${report.summary.errors} error(s)). See Problems.`
      );
    }
  });
}

// ---- Analyze -------------------------------------------------------------------------------------

export async function analyzeCommand(ctx: CommandContext, node?: TreeNode): Promise<void> {
  const projectFile = await resolveProjectFile(node);
  if (!projectFile) {
    return;
  }
  await run(ctx, `Analyzing ${path.basename(projectFile)}`, async () => {
    const cwd = path.dirname(projectFile);
    const { report } = await ctx.engine.analyze(projectFile, cwd);
    ctx.output.appendLine(
      `[analyze] ${report.project}: ${report.ruleCount} rule(s), ` +
        `${report.summary.errors} error / ${report.summary.warnings} warning / ${report.summary.infos} info` +
        (report.blocked ? " — BLOCKING" : "")
    );
    ctx.diagnostics.setForProject(projectFile, report.diagnostics);
    void vscode.window.showInformationMessage(
      `Code analysis: ${report.summary.total} finding(s). See Problems.`
    );
  });
}

// ---- Generate script -----------------------------------------------------------------------------

export async function generateScriptCommand(ctx: CommandContext, node?: TreeNode): Promise<void> {
  const projectFile = await resolveProjectFile(node);
  if (!projectFile) {
    return;
  }
  await run(ctx, `Generating script for ${path.basename(projectFile)}`, async () => {
    const cwd = path.dirname(projectFile);
    const result = await ctx.engine.run(["script", projectFile], cwd);
    if (result.exitCode !== 0) {
      throw new Error(result.stderr.trim() || `script exited ${result.exitCode}`);
    }
    const doc = await vscode.workspace.openTextDocument({ language: "sql", content: result.stdout });
    await vscode.window.showTextDocument(doc);
  });
}

// ---- Validate ------------------------------------------------------------------------------------

export async function validateCommand(ctx: CommandContext, node?: TreeNode): Promise<void> {
  const projectFile = await resolveProjectFile(node);
  if (!projectFile) {
    return;
  }
  const connection = await promptConnection();
  if (!connection) {
    return;
  }
  await run(ctx, `Validating ${path.basename(projectFile)}`, async () => {
    const cwd = path.dirname(projectFile);
    const result = await ctx.engine.run(["validate", projectFile, "--connection", connection], cwd);
    ctx.output.appendLine(result.stdout.trim());
    if (result.stderr.trim()) {
      ctx.output.appendLine(result.stderr.trim());
    }
    if (result.exitCode === 0) {
      void vscode.window.showInformationMessage("Validation passed — project applies cleanly.");
    } else {
      void vscode.window.showErrorMessage("Validation failed. See output.");
      ctx.output.show(true);
    }
  });
}

// ---- Publish (input-driven MVP; full webview is a follow-up, see EP-PROFILE) ---------------------

export async function publishCommand(ctx: CommandContext, node?: TreeNode): Promise<void> {
  const projectFile = await resolveProjectFile(node);
  if (!projectFile) {
    return;
  }
  const connection = await promptConnection();
  if (!connection) {
    return;
  }

  const optionPicks = await vscode.window.showQuickPick(
    [
      { label: "Allow destructive changes (--allow-drops)", picked: false, key: "allowDrops" },
      { label: "Do not wrap in a transaction (--no-transaction)", picked: false, key: "noTransaction" },
    ],
    { canPickMany: true, placeHolder: "Publish options (optional)" }
  );
  if (optionPicks === undefined) {
    return; // cancelled
  }
  const opts: PublishOptions = {
    allowDrops: optionPicks.some((p) => p.key === "allowDrops"),
    noTransaction: optionPicks.some((p) => p.key === "noTransaction"),
    variables: await promptSqlcmdVariables(),
  };

  // Preview via dry-run, then confirm. This mirrors the SQL Database Projects "Generate Script" gate
  // before a live deploy without needing the full webview yet.
  const cwd = path.dirname(projectFile);
  let confirmed = false;
  await run(ctx, `Previewing publish for ${path.basename(projectFile)}`, async () => {
    const plan = await ctx.engine.publishDryRun(projectFile, connection, cwd, opts);
    if (plan.inSync) {
      void vscode.window.showInformationMessage("Nothing to publish — target already matches the project.");
      return;
    }
    ctx.output.appendLine(
      `[publish dry-run] ${plan.project}: ${plan.changeCount} change(s), ${plan.destructiveCount} destructive`
    );
    const doc = await vscode.workspace.openTextDocument({ language: "sql", content: plan.script });
    await vscode.window.showTextDocument(doc, { preview: true });
    const choice = await vscode.window.showWarningMessage(
      `Publish ${plan.changeCount} change(s) (${plan.destructiveCount} destructive) to the target?`,
      { modal: true },
      "Publish"
    );
    confirmed = choice === "Publish";
  });
  if (!confirmed) {
    return;
  }

  await run(ctx, `Publishing ${path.basename(projectFile)}`, async () => {
    const result = await ctx.engine.publish(projectFile, connection, cwd, opts);
    ctx.output.appendLine(result.stdout.trim());
    if (result.stderr.trim()) {
      ctx.output.appendLine(result.stderr.trim());
    }
    if (result.exitCode === 0) {
      void vscode.window.showInformationMessage("Publish succeeded.");
    } else {
      void vscode.window.showErrorMessage("Publish failed. See output.");
      ctx.output.show(true);
    }
  });
}

// ---- Schema Compare (stub — full webview is EP-SCHEMACOMPARE #19) --------------------------------

export async function schemaCompareCommand(ctx: CommandContext, node?: TreeNode): Promise<void> {
  const projectFile = await resolveProjectFile(node);
  if (!projectFile) {
    return;
  }
  const connection = await promptConnection("Target connection string to compare against");
  if (!connection) {
    return;
  }
  await run(ctx, `Comparing ${path.basename(projectFile)}`, async () => {
    const cwd = path.dirname(projectFile);
    const report = await ctx.engine.compare(projectFile, connection, cwd);
    if (report.inSync) {
      void vscode.window.showInformationMessage("In sync — target already matches the project.");
      return;
    }
    ctx.output.appendLine(`[compare] ${report.project}: ${report.changeCount} change(s):`);
    for (const c of report.changes) {
      ctx.output.appendLine(`  [${c.destructive ? "!" : "+"}] ${c.description}`);
    }
    ctx.output.show(true);
    // TODO(EP-SCHEMACOMPARE #19): render this as a checkable diff webview with apply/script.
    void vscode.window.showInformationMessage(
      `${report.changeCount} difference(s). Full Schema Compare UI is a follow-up (#19) — see output.`
    );
  });
}

// ---- Design table (EP-DESIGNER #26: graphical table designer webview) -----------------------------

export async function designTableCommand(
  ctx: CommandContext,
  arg?: TreeNode | vscode.Uri
): Promise<void> {
  const sqlFile = await resolveTableSqlFile(arg);
  if (!sqlFile) {
    return;
  }
  await TableDesignerPanel.show(ctx.extensionContext, ctx.engine, ctx.output, sqlFile);
}

/** Resolve the .sql file to design: a passed Uri / the active editor / a tree node, else a quick pick. */
async function resolveTableSqlFile(arg?: TreeNode | vscode.Uri): Promise<string | undefined> {
  if (arg instanceof vscode.Uri && arg.fsPath.toLowerCase().endsWith(".sql")) {
    return arg.fsPath;
  }
  const active = vscode.window.activeTextEditor?.document;
  if (active && active.fileName.toLowerCase().endsWith(".sql")) {
    return active.fileName;
  }
  const files = await vscode.workspace.findFiles("**/*.sql", "**/{node_modules,bin,obj}/**");
  if (files.length === 0) {
    void vscode.window.showWarningMessage("No .sql files found in this workspace.");
    return undefined;
  }
  const pick = await vscode.window.showQuickPick(
    files.map((f) => ({ label: path.basename(f.fsPath), description: f.fsPath, fsPath: f.fsPath })),
    { placeHolder: "Select a table .sql file to design" }
  );
  return pick?.fsPath;
}

// ---- Add object ----------------------------------------------------------------------------------

export async function addObjectCommand(ctx: CommandContext, node?: TreeNode): Promise<void> {
  const projectFile = await resolveProjectFile(node);
  if (!projectFile) {
    return;
  }
  const kindPick = await vscode.window.showQuickPick(
    OBJECT_TEMPLATES.map((t) => ({ label: t.label, templateKind: t.kind })),
    { placeHolder: "Object type to add" }
  );
  if (!kindPick) {
    return;
  }
  const kind = kindPick.templateKind as ObjectTemplateKind;

  const projectDir = path.dirname(projectFile);
  const defaultSchema = readDefaultSchema(projectFile);
  const name = await vscode.window.showInputBox({
    prompt: `Name for the new ${kind}`,
    placeHolder: "object_name",
    validateInput: (v) => (/^[A-Za-z_][A-Za-z0-9_]*$/.test(v) ? undefined : "Use a valid SQL identifier."),
  });
  if (!name) {
    return;
  }

  const rel = templateRelativePath(kind, defaultSchema, name);
  const absolute = path.join(projectDir, rel);
  if (fs.existsSync(absolute)) {
    void vscode.window.showErrorMessage(`${rel} already exists.`);
    return;
  }
  fs.mkdirSync(path.dirname(absolute), { recursive: true });
  fs.writeFileSync(absolute, renderTemplate(kind, defaultSchema, name), "utf8");
  ctx.output.appendLine(`[add] created ${rel}`);
  ctx.tree.refresh();
  const doc = await vscode.workspace.openTextDocument(vscode.Uri.file(absolute));
  await vscode.window.showTextDocument(doc);
}

// ---- Open project file / Set target version ------------------------------------------------------

export async function openProjectFileCommand(_ctx: CommandContext, node?: TreeNode): Promise<void> {
  const projectFile = await resolveProjectFile(node);
  if (!projectFile) {
    return;
  }
  const doc = await vscode.workspace.openTextDocument(vscode.Uri.file(projectFile));
  await vscode.window.showTextDocument(doc);
}

export async function setTargetVersionCommand(ctx: CommandContext, node?: TreeNode): Promise<void> {
  const projectFile = await resolveProjectFile(node);
  if (!projectFile) {
    return;
  }
  const version = await vscode.window.showQuickPick(["18", "17", "16", "15", "14", "13"], {
    placeHolder: "Target PostgreSQL major version",
  });
  if (!version) {
    return;
  }
  const xml = fs.readFileSync(projectFile, "utf8");
  fs.writeFileSync(projectFile, setTargetVersionInProjectXml(xml, version), "utf8");
  ctx.output.appendLine(`[set-target] ${path.basename(projectFile)} -> PostgreSQL ${version}`);
  ctx.tree.refresh();
  void vscode.window.showInformationMessage(`Target version set to PostgreSQL ${version}.`);
}

// ---- New project / Open --------------------------------------------------------------------------

export async function newProjectCommand(ctx: CommandContext): Promise<void> {
  const folder = vscode.workspace.workspaceFolders?.[0];
  const name = await vscode.window.showInputBox({
    prompt: "New project name",
    placeHolder: "MyDatabase",
    validateInput: (v) => (/^[A-Za-z_][A-Za-z0-9_]*$/.test(v) ? undefined : "Use a valid identifier."),
  });
  if (!name || !folder) {
    if (!folder) {
      void vscode.window.showWarningMessage("Open a folder before creating a project.");
    }
    return;
  }
  const schema = (await vscode.window.showInputBox({ prompt: "Default schema", value: "public" })) ?? "public";
  const projDir = path.join(folder.uri.fsPath, name);
  fs.mkdirSync(projDir, { recursive: true });
  const projPath = path.join(projDir, `${name}.pgproj`);
  fs.writeFileSync(projPath, newProjectXml(name, schema), "utf8");
  ctx.output.appendLine(`[new] created ${projPath}`);
  ctx.tree.refresh();
  const doc = await vscode.workspace.openTextDocument(vscode.Uri.file(projPath));
  await vscode.window.showTextDocument(doc);
}

export async function openProjectCommand(_ctx: CommandContext): Promise<void> {
  const picked = await vscode.window.showOpenDialog({
    canSelectMany: false,
    filters: { "PostgreSQL Database Project": ["pgproj"] },
    openLabel: "Open Project",
  });
  if (!picked || picked.length === 0) {
    return;
  }
  // Add the project's folder to the workspace so it is discovered by the tree.
  const dir = path.dirname(picked[0].fsPath);
  const existing = vscode.workspace.workspaceFolders?.length ?? 0;
  vscode.workspace.updateWorkspaceFolders(existing, 0, { uri: vscode.Uri.file(dir) });
}

// ---- prompts -------------------------------------------------------------------------------------

function promptConnection(prompt = "PostgreSQL connection string"): Thenable<string | undefined> {
  return vscode.window.showInputBox({
    prompt,
    placeHolder: "Host=localhost;Port=5432;Username=postgres;Password=...;Database=mydb",
    ignoreFocusOut: true,
  });
}

/** Collect SQLCMD-style variables ("name=value") one at a time; empty input ends the loop. */
async function promptSqlcmdVariables(): Promise<string[]> {
  const vars: string[] = [];
  for (;;) {
    const entry = await vscode.window.showInputBox({
      prompt: `SQLCMD variable ${vars.length + 1} as name=value (leave empty to finish)`,
      placeHolder: "Environment=Production",
      ignoreFocusOut: true,
      validateInput: (v) => (v === "" || /^[^=]+=.*/.test(v) ? undefined : "Use name=value."),
    });
    if (!entry) {
      break;
    }
    vars.push(entry);
  }
  return vars;
}

// ---- project-file helpers ------------------------------------------------------------------------

function readDefaultSchema(projectFile: string): string {
  try {
    return readDefaultSchemaFromXml(fs.readFileSync(projectFile, "utf8"));
  } catch {
    return "public";
  }
}

function newProjectXml(name: string, schema: string): string {
  return `<Project DefaultTargets="Build">
  <PropertyGroup>
    <Name>${name}</Name>
    <DefaultSchema>${schema}</DefaultSchema>
    <TargetPostgresVersion>18</TargetPostgresVersion>
  </PropertyGroup>
  <ItemGroup>
    <Build Include="**/*.sql" />
  </ItemGroup>
</Project>
`;
}

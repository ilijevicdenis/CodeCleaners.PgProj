// Extension entrypoint: wires the engine, the Projects tree, the diagnostics controller, and all
// commands. Activation is lazy (a workspace containing a .pgproj, or the view being opened).

import * as vscode from "vscode";
import { EngineConfig, PgProjEngine } from "./engine/engine";
import { ProjectsTreeProvider } from "./views/projectsTree";
import { TreeNode } from "./views/treeModel";
import { DiagnosticsController } from "./diagnosticsController";
import * as cmd from "./commands";

function readEngineConfig(): EngineConfig {
  const cfg = vscode.workspace.getConfiguration("pgproj");
  return {
    cliPath: cfg.get<string>("cliPath", "pgproj"),
    dotnetPath: cfg.get<string>("dotnetPath", "dotnet"),
  };
}

export function activate(context: vscode.ExtensionContext): void {
  const output = vscode.window.createOutputChannel("PostgreSQL Database Projects");
  let engine = new PgProjEngine(readEngineConfig());
  const diagnostics = new DiagnosticsController();
  const tree = new ProjectsTreeProvider(engine, output);

  const ctx: cmd.CommandContext = { engine, diagnostics, tree, output, extensionContext: context };

  // Re-read the engine config (cliPath/dotnetPath) when the user changes it.
  context.subscriptions.push(
    vscode.workspace.onDidChangeConfiguration((e) => {
      if (e.affectsConfiguration("pgproj.cliPath") || e.affectsConfiguration("pgproj.dotnetPath")) {
        engine = new PgProjEngine(readEngineConfig());
        ctx.engine = engine;
        tree.refresh();
      }
    })
  );

  context.subscriptions.push(
    vscode.window.registerTreeDataProvider("pgproj.projects", tree),
    output,
    diagnostics,
    { dispose: () => {} }
  );

  const register = (id: string, handler: (node?: TreeNode) => unknown) =>
    context.subscriptions.push(
      vscode.commands.registerCommand(id, (node?: TreeNode) => handler(node))
    );

  register("pgproj.refresh", () => tree.refresh());
  register("pgproj.build", (node) => cmd.buildCommand(ctx, node));
  register("pgproj.analyze", (node) => cmd.analyzeCommand(ctx, node));
  register("pgproj.validate", (node) => cmd.validateCommand(ctx, node));
  register("pgproj.publish", (node) => cmd.publishCommand(ctx, node));
  register("pgproj.generateScript", (node) => cmd.generateScriptCommand(ctx, node));
  register("pgproj.schemaCompare", (node) => cmd.schemaCompareCommand(ctx, node));
  register("pgproj.addObject", (node) => cmd.addObjectCommand(ctx, node));
  context.subscriptions.push(
    vscode.commands.registerCommand("pgproj.designTable", (arg?: TreeNode | vscode.Uri) =>
      cmd.designTableCommand(ctx, arg)
    )
  );
  register("pgproj.openProjectFile", (node) => cmd.openProjectFileCommand(ctx, node));
  register("pgproj.setTargetVersion", (node) => cmd.setTargetVersionCommand(ctx, node));
  register("pgproj.newProject", () => cmd.newProjectCommand(ctx));
  register("pgproj.openProject", () => cmd.openProjectCommand(ctx));

  // Refresh the tree when a .pgproj is created/deleted/changed in the workspace.
  const watcher = vscode.workspace.createFileSystemWatcher("**/*.pgproj");
  watcher.onDidCreate(() => tree.refresh());
  watcher.onDidDelete(() => tree.refresh());
  watcher.onDidChange(() => tree.refresh());
  context.subscriptions.push(watcher);

  output.appendLine("PostgreSQL Database Projects extension activated.");
}

export function deactivate(): void {
  // Subscriptions (output channel, diagnostics, watcher) are disposed by VS Code via context.subscriptions.
}

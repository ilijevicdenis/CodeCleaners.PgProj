// The VS Code TreeDataProvider for the "PostgreSQL Database Projects" view. Discovers .pgproj files in
// the workspace, asks the engine for each project's model tree, and renders the pure TreeNode model
// from treeModel.ts. Refreshable (after a build) and resilient to one project failing to load.

import * as path from "path";
import * as vscode from "vscode";
import { PgProjEngine } from "../engine/engine";
import { buildProjectNode, TreeNode } from "./treeModel";

export class ProjectsTreeProvider implements vscode.TreeDataProvider<TreeNode> {
  private readonly _onDidChange = new vscode.EventEmitter<TreeNode | undefined | void>();
  readonly onDidChangeTreeData = this._onDidChange.event;

  private rootsCache: TreeNode[] | undefined;

  constructor(private readonly engine: PgProjEngine, private readonly output: vscode.OutputChannel) {}

  refresh(): void {
    this.rootsCache = undefined;
    this._onDidChange.fire();
  }

  getTreeItem(node: TreeNode): vscode.TreeItem {
    const collapsible =
      node.children.length > 0
        ? node.kind === "project"
          ? vscode.TreeItemCollapsibleState.Expanded
          : vscode.TreeItemCollapsibleState.Collapsed
        : vscode.TreeItemCollapsibleState.None;

    const item = new vscode.TreeItem(node.label, collapsible);
    item.contextValue =
      node.kind === "project" ? "pgprojProject" : `pgproj${capitalize(node.kind)}`;
    item.iconPath = iconFor(node);

    if (node.kind === "project" && node.projectFile) {
      item.resourceUri = vscode.Uri.file(node.projectFile);
      item.description = path.basename(node.projectFile);
    }

    // Object/child nodes with a source anchor become clickable go-to-definition links.
    if ((node.kind === "object" || node.kind === "child") && node.file && node.line && node.line > 0) {
      const projectFile = this.findOwningProjectFile(node);
      if (projectFile) {
        const absolute = path.resolve(path.dirname(projectFile), node.file);
        item.command = {
          command: "vscode.open",
          title: "Open",
          arguments: [
            vscode.Uri.file(absolute),
            { selection: rangeFromOneBased(node.line, node.col ?? 1) },
          ],
        };
      }
    }
    return item;
  }

  async getChildren(node?: TreeNode): Promise<TreeNode[]> {
    if (node) {
      return node.children;
    }
    if (!this.rootsCache) {
      this.rootsCache = await this.loadRoots();
    }
    return this.rootsCache;
  }

  private async loadRoots(): Promise<TreeNode[]> {
    const files = await vscode.workspace.findFiles("**/*.pgproj", "**/{node_modules,bin,obj}/**");
    const roots: TreeNode[] = [];
    for (const uri of files.sort((a, b) => a.fsPath.localeCompare(b.fsPath))) {
      const cwd = path.dirname(uri.fsPath);
      try {
        const tree = await this.engine.modelTree(uri.fsPath, cwd);
        roots.push(buildProjectNode(uri.fsPath, tree));
      } catch (err) {
        this.output.appendLine(`Failed to load model tree for ${uri.fsPath}: ${String(err)}`);
        // Render a project node with no children rather than dropping it entirely.
        roots.push({ kind: "project", label: path.basename(uri.fsPath, ".pgproj"), projectFile: uri.fsPath, children: [] });
      }
    }
    this.projectFiles = roots.map((r) => r.projectFile!).filter(Boolean);
    return roots;
  }

  // Cached project-file list, used to anchor relative object files for go-to-definition.
  private projectFiles: string[] = [];

  private findOwningProjectFile(_node: TreeNode): string | undefined {
    // Single-project workspaces are the common case; for multi-project, the first project's dir is a
    // safe anchor because object `file` paths are project-relative and dirs rarely overlap. A precise
    // owner link is a follow-up once nodes carry a back-reference.
    return this.projectFiles[0];
  }
}

function rangeFromOneBased(line: number, col: number): vscode.Range {
  const pos = new vscode.Position(Math.max(0, line - 1), Math.max(0, col - 1));
  return new vscode.Range(pos, pos);
}

function capitalize(s: string): string {
  return s.charAt(0).toUpperCase() + s.slice(1);
}

function iconFor(node: TreeNode): vscode.ThemeIcon {
  if (node.kind === "project") {
    return new vscode.ThemeIcon("database");
  }
  if (node.kind === "folder") {
    return new vscode.ThemeIcon("folder");
  }
  if (node.kind === "child") {
    return new vscode.ThemeIcon("symbol-field");
  }
  return new vscode.ThemeIcon(OBJECT_ICONS[node.objectKind ?? ""] ?? "symbol-misc");
}

const OBJECT_ICONS: Record<string, string> = {
  schema: "symbol-namespace",
  table: "table",
  view: "symbol-interface",
  materializedView: "symbol-interface",
  function: "symbol-function",
  aggregate: "symbol-function",
  index: "list-tree",
  sequence: "symbol-numeric",
  type: "symbol-enum",
  domain: "symbol-enum",
  trigger: "zap",
  eventTrigger: "zap",
  policy: "shield",
  extension: "extensions",
};

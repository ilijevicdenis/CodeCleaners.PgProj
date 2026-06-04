// Pure tree model: turns a project file + its ModelTreeDto into the node hierarchy the VS Code tree
// renders (project -> object-kind folders -> objects -> children). No `vscode` import, so Vitest can
// assert the shape directly from a model-tree JSON fixture.
//
// Folder grouping mirrors the SQL Database Projects extension: objects are bucketed by kind into
// pluralised, ordered folders ("Schemas", "Tables", "Views", "Functions", …). A table's columns become
// child nodes under the table itself (the contract already nests them in node.children).

import { ModelTreeDto, ModelTreeNodeDto } from "../engine/contract";

export type TreeNodeKind = "project" | "folder" | "object" | "child";

export interface TreeNode {
  kind: TreeNodeKind;
  label: string;
  /** The underlying object kind for object/child nodes (e.g. "table", "function"). */
  objectKind?: string;
  /** Absolute path to the .pgproj for project nodes. */
  projectFile?: string;
  /** Project-relative source file for go-to-definition (object/child nodes). */
  file?: string;
  /** 1-based source line, 0 when unknown. */
  line?: number;
  /** 1-based source column, 0 when unknown. */
  col?: number;
  children: TreeNode[];
}

// Display order + plural folder labels, matching the on-disk sample folder names where they exist.
const FOLDER_ORDER: Array<{ kind: string; label: string }> = [
  { kind: "schema", label: "Schemas" },
  { kind: "table", label: "Tables" },
  { kind: "view", label: "Views" },
  { kind: "materializedView", label: "Materialized Views" },
  { kind: "index", label: "Indexes" },
  { kind: "sequence", label: "Sequences" },
  { kind: "function", label: "Functions" },
  { kind: "aggregate", label: "Aggregates" },
  { kind: "type", label: "Types" },
  { kind: "domain", label: "Domains" },
  { kind: "trigger", label: "Triggers" },
  { kind: "eventTrigger", label: "Event Triggers" },
  { kind: "policy", label: "Policies" },
  { kind: "rule", label: "Rules" },
  { kind: "operator", label: "Operators" },
  { kind: "operatorClass", label: "Operator Classes" },
  { kind: "cast", label: "Casts" },
  { kind: "collation", label: "Collations" },
  { kind: "conversion", label: "Conversions" },
  { kind: "extension", label: "Extensions" },
  { kind: "foreignDataWrapper", label: "Foreign Data Wrappers" },
  { kind: "server", label: "Foreign Servers" },
  { kind: "foreignTable", label: "Foreign Tables" },
  { kind: "publication", label: "Publications" },
  { kind: "statistics", label: "Statistics" },
  { kind: "textSearchConfiguration", label: "Text Search Configurations" },
  { kind: "textSearchDictionary", label: "Text Search Dictionaries" },
  { kind: "comment", label: "Comments" },
];

const ORDER_INDEX = new Map(FOLDER_ORDER.map((f, i) => [f.kind, i]));
const LABEL_FOR = new Map(FOLDER_ORDER.map((f) => [f.kind, f.label]));

/** Title-case an unknown kind into a folder label fallback (e.g. "fooBar" -> "Foo Bars"). */
function fallbackFolderLabel(kind: string): string {
  const spaced = kind.replace(/([a-z])([A-Z])/g, "$1 $2");
  const titled = spaced.charAt(0).toUpperCase() + spaced.slice(1);
  return titled.endsWith("s") ? titled : `${titled}s`;
}

function objectLabel(node: ModelTreeNodeDto): string {
  // Functions/aggregates carry their arg signature in qualifiedName; prefer it for disambiguation.
  if (node.qualifiedName && node.qualifiedName !== `${node.schema}.${node.name}`) {
    return node.qualifiedName;
  }
  return node.qualifiedName || `${node.schema}.${node.name}`;
}

function toChild(node: ModelTreeNodeDto): TreeNode {
  return {
    kind: "child",
    label: node.qualifiedName || node.name,
    objectKind: node.kind,
    file: node.file,
    line: node.line,
    col: node.col,
    children: (node.children ?? []).map(toChild),
  };
}

/**
 * Build the folder-grouped object children for one project from its model tree. Exposed separately so
 * a test can assert folder bucketing without constructing the whole project node.
 */
export function buildObjectFolders(tree: ModelTreeDto): TreeNode[] {
  const buckets = new Map<string, TreeNode[]>();
  for (const node of tree.nodes) {
    const objectNode: TreeNode = {
      kind: "object",
      label: objectLabel(node),
      objectKind: node.kind,
      file: node.file,
      line: node.line,
      col: node.col,
      children: (node.children ?? []).map(toChild),
    };
    (buckets.get(node.kind) ?? buckets.set(node.kind, []).get(node.kind)!).push(objectNode);
  }

  const folders: TreeNode[] = [];
  for (const [kind, objects] of buckets) {
    objects.sort((a, b) => a.label.localeCompare(b.label));
    folders.push({
      kind: "folder",
      label: LABEL_FOR.get(kind) ?? fallbackFolderLabel(kind),
      objectKind: kind,
      children: objects,
    });
  }
  folders.sort((a, b) => {
    const ai = ORDER_INDEX.get(a.objectKind!) ?? Number.MAX_SAFE_INTEGER;
    const bi = ORDER_INDEX.get(b.objectKind!) ?? Number.MAX_SAFE_INTEGER;
    return ai !== bi ? ai - bi : a.label.localeCompare(b.label);
  });
  return folders;
}

/** Build the root project node from its file path and model tree. */
export function buildProjectNode(projectFile: string, tree: ModelTreeDto): TreeNode {
  return {
    kind: "project",
    label: tree.project,
    projectFile,
    children: buildObjectFolders(tree),
  };
}

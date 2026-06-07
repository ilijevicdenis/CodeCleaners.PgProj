// Message protocols between the webview panels (untrusted UI) and the extension host. Kept as a pure,
// vscode-free module so both sides import the same shapes and a unit test can assert the host's handling
// of each inbound message without a webview. All messages are discriminated on `type`.

import { SchemaCompareReportDto } from "../engine/schemaCompare";

// ---- Publish webview ----------------------------------------------------------------------------

export interface PublishVariable {
  name: string;
  value: string;
}

export interface PublishFormState {
  connection: string;
  /** A non-secret connection label, stored in the profile (never the connection string). */
  connectionName: string;
  allowDrops: boolean;
  noTransaction: boolean;
  variables: PublishVariable[];
  targetVersion?: string;
}

/** webview -> host */
export type PublishInbound =
  | { type: "ready" }
  | { type: "generateScript"; state: PublishFormState }
  | { type: "publish"; state: PublishFormState }
  | { type: "saveProfile"; state: PublishFormState };

/** host -> webview */
export type PublishOutbound =
  | { type: "init"; state: PublishFormState }
  | { type: "status"; message: string; level: "info" | "error" };

// ---- Schema Compare webview ---------------------------------------------------------------------

/** webview -> host */
export type CompareInbound =
  | { type: "ready" }
  | { type: "recompare" }
  | { type: "toggle"; id: string; included: boolean }
  | { type: "setSource"; spec: string }
  | { type: "setTarget"; spec: string }
  | { type: "script"; includedIds: string[] }
  | { type: "apply"; includedIds: string[] };

/** host -> webview */
export type CompareOutbound =
  | { type: "init"; source: string; target: string }
  | { type: "report"; report: SchemaCompareReportDto }
  | { type: "status"; message: string; level: "info" | "error" };

// ---- Pure helpers shared by host + tests --------------------------------------------------------

/** Convert the publish form's variable rows into the engine's `name=value` --var entries (drop blanks). */
export function variablesToCli(vars: PublishVariable[]): string[] {
  return vars
    .filter((v) => v.name.trim().length > 0)
    .map((v) => `${v.name.trim()}=${v.value}`);
}

/**
 * The whitelisted, secret-free `.pgpublish.json` body a Save-Profile writes. Mirrors PublishProfile's
 * wire shape (camelCase, omit-null, only non-secret fields) so the engine's `PublishProfile.Load` reads
 * it back. The connection STRING is never included — only the non-secret connectionName label.
 */
export function toPublishProfileJson(state: PublishFormState): string {
  const variables: Record<string, string> = {};
  for (const v of state.variables) {
    if (v.name.trim().length > 0) {
      variables[v.name.trim()] = v.value;
    }
  }
  const options: Record<string, boolean> = {};
  if (state.allowDrops) {
    options.allowDrops = true;
  }
  if (state.noTransaction) {
    options.wrapInTransaction = false;
  }

  const body: Record<string, unknown> = {};
  if (state.targetVersion) {
    body.targetPostgresVersion = state.targetVersion;
  }
  if (state.connectionName.trim().length > 0) {
    body.connectionName = state.connectionName.trim();
  }
  if (Object.keys(variables).length > 0) {
    body.variables = variables;
  }
  if (Object.keys(options).length > 0) {
    body.options = options;
  }
  return JSON.stringify(body, null, 2) + "\n";
}

/**
 * Assemble the SQL for a subset of compare changes (the checked rows), in deploy order, from the diff
 * report — used by the Schema Compare "Script (selected)" action. Pure so it is unit-tested directly.
 */
export function scriptSelectedChanges(
  report: SchemaCompareReportDto,
  includedIds: string[]
): string {
  const wanted = new Set(includedIds);
  const selected = report.changes
    .filter((c) => wanted.has(c.id))
    .sort((a, b) => a.phase - b.phase);
  if (selected.length === 0) {
    return "-- No changes selected.\n";
  }
  const header = `-- Schema Compare: ${report.source.displayName} -> ${report.target.displayName}\n` +
    `-- ${selected.length} of ${report.changeCount} change(s) selected\n\n`;
  return header + selected.map((c) => `-- [${c.objectType}] ${c.description}\n${c.sql.trim()}`).join("\n\n") + "\n";
}

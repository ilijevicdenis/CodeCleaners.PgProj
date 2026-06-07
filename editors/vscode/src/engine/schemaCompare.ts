// TypeScript mirror of the `compare --source X --target Y -o diff.json` wire shape
// (src/PgProj.Core/Comparison/SchemaCompareReport.cs → SchemaCompareReportDto). This is the
// EP-SCHEMACOMPARE structured, selectable diff the Schema Compare webview renders — distinct from the
// legacy one-way CompareReportDto in contract.ts (project → live DB). camelCase, string enums.

export interface SchemaCompareEndpointDto {
  /** "project" | "package" | "liveDatabase" | "snapshot" | "unknown" */
  kind: string;
  displayName: string;
  buildDiagnostics: string[];
}

export interface SchemaCompareChangeDto {
  /** Stable, deterministic id — referenced across re-compares and from a saved selection. */
  id: string;
  /** Change-record type name, e.g. "CreateTableChange". */
  kind: string;
  /** Coarse object-type the filters operate on, e.g. "table", "index". */
  objectType: string;
  description: string;
  /** Whether this change is currently part of the subset to script/apply. */
  included: boolean;
  /** Whether applying the change can lose data/objects. */
  destructive: boolean;
  /** Deploy-ordering phase (lower runs first). */
  phase: number;
  /** The exact SQL this change emits. */
  sql: string;
}

export interface SchemaCompareReportDto {
  schemaVersion: string;
  verb: "compare";
  source: SchemaCompareEndpointDto;
  target: SchemaCompareEndpointDto;
  inSync: boolean;
  changeCount: number;
  includedCount: number;
  destructiveCount: number;
  objectTypes: string[];
  changes: SchemaCompareChangeDto[];
}

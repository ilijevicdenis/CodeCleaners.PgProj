// TypeScript mirror of the EP-RPC JSON contract (docs/JSON_CONTRACT.md, schemaVersion "1.0").
// Wire format: camelCase, null fields omitted, enums as string names. Kept deliberately small —
// only the fields the extension consumes. New optional fields keep the major; editors refuse an
// unknown major (see assertSchemaVersion below).

export const SUPPORTED_SCHEMA_MAJOR = 1;

export type ContractSeverity = "Info" | "Warning" | "Error";

export interface DiagnosticDto {
  ruleId: string;
  severity: ContractSeverity;
  message: string;
  target?: string;
  /** Project-relative source file, or undefined when the finding has no file anchor. */
  file?: string;
  /** 1-based line, 0 when unknown. */
  line: number;
  /** 1-based column, 0 when unknown. */
  col: number;
}

export interface DiagnosticSummaryDto {
  errors: number;
  warnings: number;
  infos: number;
  total: number;
}

export interface ModelSummaryDto {
  schemas: number;
  tables: number;
  indexes: number;
  views: number;
  sequences: number;
  functions: number;
  objects: number;
}

export interface ModelTreeNodeDto {
  kind: string;
  schema: string;
  name: string;
  qualifiedName: string;
  file?: string;
  line: number;
  col: number;
  children?: ModelTreeNodeDto[];
}

export interface ModelTreeDto {
  schemaVersion: string;
  verb: "model-tree";
  project: string;
  summary: ModelSummaryDto;
  nodes: ModelTreeNodeDto[];
}

export interface BuildReportDto {
  schemaVersion: string;
  verb: "build";
  project: string;
  success: boolean;
  fileCount: number;
  model: ModelSummaryDto;
  summary: DiagnosticSummaryDto;
  diagnostics: DiagnosticDto[];
  modelTree?: ModelTreeDto;
}

export interface AnalyzeReportDto {
  schemaVersion: string;
  verb: "analyze";
  project: string;
  ruleCount: number;
  blocked: boolean;
  summary: DiagnosticSummaryDto;
  diagnostics: DiagnosticDto[];
}

export interface ChangeDto {
  kind: string;
  description: string;
  destructive: boolean;
  phase: number;
}

export interface CompareReportDto {
  schemaVersion: string;
  verb: "compare";
  project: string;
  inSync: boolean;
  changeCount: number;
  destructiveCount: number;
  changes: ChangeDto[];
}

export interface PublishPlanDto {
  schemaVersion: string;
  verb: "publish";
  project: string;
  dryRun: boolean;
  inSync: boolean;
  changeCount: number;
  destructiveCount: number;
  changes: ChangeDto[];
  script: string;
}

/** Parse the major component of a "major.minor" schemaVersion string. */
export function schemaMajor(version: string): number {
  const major = parseInt(String(version).split(".")[0], 10);
  return Number.isNaN(major) ? -1 : major;
}

/** Throws when the payload's schemaVersion major is one this extension does not understand. */
export function assertSchemaVersion(version: string): void {
  const major = schemaMajor(version);
  if (major !== SUPPORTED_SCHEMA_MAJOR) {
    throw new Error(
      `Unsupported pgproj JSON contract schemaVersion "${version}" (this extension supports major ${SUPPORTED_SCHEMA_MAJOR}). Update the extension or the pgproj engine.`
    );
  }
}

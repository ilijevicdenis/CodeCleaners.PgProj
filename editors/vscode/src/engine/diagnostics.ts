// Maps contract DiagnosticDto[] -> a lightweight, vscode-free shape grouped per file. The extension
// layer (diagnosticsController.ts) turns these into real vscode.Diagnostic objects against a
// DiagnosticCollection; keeping the mapping pure here lets Vitest test it without the editor host.
//
// Range math: the contract gives 1-based line/col (0 = unknown). VS Code positions are 0-based, so we
// subtract 1 (clamped at 0). We have no end position, so the range covers from the start col to end of
// the same line — represented here as a single-line range whose end col we leave the host to extend.

import { DiagnosticDto, ContractSeverity } from "./contract";

export type MappedSeverity = "error" | "warning" | "info";

export interface MappedRange {
  /** 0-based start line. */
  startLine: number;
  /** 0-based start character. */
  startCol: number;
  /** 0-based end line (same as start line — single-line squiggle). */
  endLine: number;
  /** 0-based end character. Host extends to end-of-line when this equals startCol. */
  endCol: number;
}

export interface MappedDiagnostic {
  ruleId: string;
  severity: MappedSeverity;
  message: string;
  target?: string;
  range: MappedRange;
}

/** Diagnostics grouped by their project-relative file (the key for a DiagnosticCollection entry). */
export interface MappedDiagnosticsByFile {
  [file: string]: MappedDiagnostic[];
}

function mapSeverity(s: ContractSeverity): MappedSeverity {
  switch (s) {
    case "Error":
      return "error";
    case "Warning":
      return "warning";
    default:
      return "info";
  }
}

/** Convert a single contract diagnostic to a range (1-based -> 0-based, unknown -> top of file). */
export function toRange(d: DiagnosticDto): MappedRange {
  const startLine = Math.max(0, (d.line || 1) - 1);
  const startCol = Math.max(0, (d.col || 1) - 1);
  // No end position in the contract: produce a zero-width start that the host widens to EOL.
  return { startLine, startCol, endLine: startLine, endCol: startCol };
}

export function mapDiagnostic(d: DiagnosticDto): MappedDiagnostic {
  return {
    ruleId: d.ruleId,
    severity: mapSeverity(d.severity),
    message: d.message,
    target: d.target,
    range: toRange(d),
  };
}

/**
 * Group a contract diagnostic list by file. Diagnostics with no `file` anchor are collected under the
 * provided `fallbackFile` (typically the .pgproj itself) so they still surface in the Problems panel.
 */
export function mapDiagnostics(
  diagnostics: DiagnosticDto[],
  fallbackFile: string
): MappedDiagnosticsByFile {
  const byFile: MappedDiagnosticsByFile = {};
  for (const d of diagnostics) {
    const key = d.file ?? fallbackFile;
    (byFile[key] ??= []).push(mapDiagnostic(d));
  }
  return byFile;
}

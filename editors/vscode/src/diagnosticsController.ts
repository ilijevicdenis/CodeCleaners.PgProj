// Bridges mapped contract diagnostics into a vscode.DiagnosticCollection so build/analyze findings
// show in the Problems panel with file:line:col squiggles. The pure mapping lives in
// engine/diagnostics.ts; this file only owns the editor-side DiagnosticCollection lifecycle.

import * as path from "path";
import * as vscode from "vscode";
import { DiagnosticDto } from "./engine/contract";
import { mapDiagnostics, MappedSeverity } from "./engine/diagnostics";

export class DiagnosticsController {
  private readonly collection: vscode.DiagnosticCollection;

  constructor() {
    this.collection = vscode.languages.createDiagnosticCollection("pgproj");
  }

  dispose(): void {
    this.collection.dispose();
  }

  /**
   * Replace all diagnostics for a project. `projectFile` is the absolute .pgproj path; contract `file`
   * fields are project-relative and resolved against its directory. Findings without a file anchor are
   * attached to the .pgproj itself so they still appear in Problems.
   */
  setForProject(projectFile: string, diagnostics: DiagnosticDto[]): void {
    this.collection.clear();
    const projectDir = path.dirname(projectFile);
    const fallback = path.basename(projectFile);
    const byFile = mapDiagnostics(diagnostics, fallback);

    for (const [relOrFallback, mapped] of Object.entries(byFile)) {
      const absolute =
        relOrFallback === fallback
          ? projectFile
          : path.resolve(projectDir, relOrFallback);
      const uri = vscode.Uri.file(absolute);
      const entries = mapped.map((m) => {
        const range = new vscode.Range(
          new vscode.Position(m.range.startLine, m.range.startCol),
          new vscode.Position(m.range.endLine, m.range.endCol)
        );
        const d = new vscode.Diagnostic(range, m.message, severity(m.severity));
        d.code = m.ruleId;
        d.source = "pgproj";
        return d;
      });
      this.collection.set(uri, entries);
    }
  }

  clear(): void {
    this.collection.clear();
  }
}

function severity(s: MappedSeverity): vscode.DiagnosticSeverity {
  switch (s) {
    case "error":
      return vscode.DiagnosticSeverity.Error;
    case "warning":
      return vscode.DiagnosticSeverity.Warning;
    default:
      return vscode.DiagnosticSeverity.Information;
  }
}

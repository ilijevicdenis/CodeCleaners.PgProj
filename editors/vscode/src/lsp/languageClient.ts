// The LIVE path: a vscode-languageclient over `pgproj serve` (STDIO LSP, docs/LSP_LANGUAGE_SERVER.md).
// This is the as-you-type companion to the build-time DiagnosticsController: the server publishes
// diagnostics, definition, hover, and completion for open SQL buffers, computed by the SAME engine the
// batch `pgproj build`/`analyze` use, so the live verdict matches the build verdict.
//
// The server OWNS live diagnostics for files it sees: its publishDiagnostics land in their own
// DiagnosticCollection (created by the client under the id below), separate from the build-time "pgproj"
// collection — so a build can still surface findings for files that aren't open, without the two fighting.

import * as vscode from "vscode";
import {
  LanguageClient,
  LanguageClientOptions,
  ServerOptions,
  TransportKind,
} from "vscode-languageclient/node";
import { EngineConfig } from "../engine/engine";
import { resolveServeInvocation } from "./serverArgs";

/** The DiagnosticCollection id the live (LSP) path publishes under — distinct from the build-time one. */
export const LSP_DIAGNOSTIC_COLLECTION = "pgproj-live";

/**
 * Owns the LanguageClient lifecycle: start, restart (on config change), and dispose. Spawning a missing
 * engine is non-fatal — the client logs and stays down, so the rest of the extension (tree, build
 * commands) still works without `pgproj serve` on PATH.
 */
export class PgProjLanguageClient {
  private client: LanguageClient | undefined;

  constructor(private readonly output: vscode.OutputChannel) {}

  /** (Re)start the client against the given engine config. A prior client is stopped first. */
  async start(config: EngineConfig): Promise<void> {
    await this.stop();

    const workspaceDir = vscode.workspace.workspaceFolders?.[0]?.uri.fsPath;
    const debounceMs = vscode.workspace
      .getConfiguration("pgproj")
      .get<number>("lsp.debounceMs", 150);
    const { command, args } = resolveServeInvocation(config, { workspaceDir, debounceMs });

    // Same executable for stdin and stderr-logging runs; transport is stdio (the server's wire is stdout).
    const serverOptions: ServerOptions = {
      run: { command, args, transport: TransportKind.stdio },
      debug: { command, args, transport: TransportKind.stdio },
    };

    const clientOptions: LanguageClientOptions = {
      // The server diagnoses .sql buffers (the extension registers a `sql` language; `pgsql` too if used).
      documentSelector: [
        { scheme: "file", language: "sql" },
        { scheme: "file", language: "pgsql" },
        { scheme: "untitled", language: "sql" },
      ],
      diagnosticCollectionName: LSP_DIAGNOSTIC_COLLECTION,
      outputChannel: this.output,
      // A loose buffer with no project still gets single-file diagnostics from the server.
      synchronize: {
        fileEvents: vscode.workspace.createFileSystemWatcher("**/*.{sql,pgproj}"),
      },
      // If the server can't be spawned (no engine on PATH), don't nag with a modal on every keystroke.
      initializationFailedHandler: (err) => {
        this.output.appendLine(`[lsp] initialize failed: ${String(err)}`);
        return false; // do not restart in a loop
      },
    };

    const client = new LanguageClient(
      "pgproj",
      "PostgreSQL Database Projects (pgproj serve)",
      serverOptions,
      clientOptions
    );
    this.client = client;

    try {
      await client.start();
      this.output.appendLine(`[lsp] language server started: ${command} ${args.join(" ")}`);
    } catch (err) {
      // Engine not runnable here (no .NET / not on PATH): keep the extension usable; build-time path still works.
      this.output.appendLine(
        `[lsp] could not start 'pgproj serve' (${command}). Live diagnostics/hover/completion are off. ` +
          `Set 'pgproj.cliPath' to a runnable engine. Cause: ${(err as Error).message}`
      );
      this.client = undefined;
    }
  }

  /** Whether a server is currently running (used by tests/diagnostics). */
  isRunning(): boolean {
    return this.client !== undefined;
  }

  async stop(): Promise<void> {
    const client = this.client;
    this.client = undefined;
    if (client) {
      try {
        await client.stop();
      } catch (err) {
        this.output.appendLine(`[lsp] error stopping language server: ${String(err)}`);
      }
    }
  }

  dispose(): void {
    void this.stop();
  }
}

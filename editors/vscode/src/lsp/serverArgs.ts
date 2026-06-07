// Pure resolution of the `pgproj serve` invocation (command + args), kept vscode-free so a unit test
// can assert the exact spawn shape the LanguageClient will use. Mirrors engine/resolveInvocation: a
// cliPath ending in ".dll" is launched through the dotnet host, otherwise the cliPath is the command.
//
// `serve` is the resident STDIO LSP host (docs/LSP_LANGUAGE_SERVER.md). The optional workspace dir is
// passed positionally; the client also sends rootUri in `initialize`, which the server prefers when set.

import { EngineConfig } from "../engine/engine";

export interface ServeInvocation {
  command: string;
  args: string[];
}

export interface ServeOptions {
  /** Optional workspace directory passed positionally to `serve`. */
  workspaceDir?: string;
  /** Optional debounce window (ms) for the live diagnose scheduler. */
  debounceMs?: number;
}

/** Build the [command, args] for `pgproj serve`, routing a .dll through the dotnet host. */
export function resolveServeInvocation(
  config: EngineConfig,
  opts: ServeOptions = {}
): ServeInvocation {
  const verbArgs = ["serve"];
  if (opts.workspaceDir) {
    verbArgs.push(opts.workspaceDir);
  }
  if (typeof opts.debounceMs === "number" && Number.isFinite(opts.debounceMs)) {
    verbArgs.push("--debounce", String(Math.max(0, Math.trunc(opts.debounceMs))));
  }

  if (config.cliPath.toLowerCase().endsWith(".dll")) {
    return { command: config.dotnetPath, args: [config.cliPath, ...verbArgs] };
  }
  return { command: config.cliPath, args: verbArgs };
}

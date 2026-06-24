// DB-backed E2E: drive the extension's commands against the REAL Docker target PostgreSQL (the same
// two-server harness the CLI blackbox suite uses). These are true end-to-end paths —
// VS Code command → extension → pgproj CLI → live PostgreSQL → result surfaced in the UI — covering
// both a happy path (validate passes) and a failure path (validate fails on a dead connection).
//
// Gated on PGPROJ_TARGET_CONNECTION (forwarded by runTest.ts). Absent → skipped, so the suite stays
// green in a host without the containers. Requires a runnable engine (pgproj.cliPath → the built dll).

import * as assert from "assert";
import * as fs from "fs";
import * as path from "path";
import * as vscode from "vscode";

const EXT_ID = "codecleaners.pgproj-vscode";
const TARGET = process.env.PGPROJ_TARGET_CONNECTION;
const DEAD = "Host=localhost;Port=15999;Username=postgres;Password=pgproj;Database=postgres";

function repoRoot(): string {
  return path.resolve(__dirname, "../../../../");
}

/** Replace the input box + result-message UI with stubs so a headless run can answer prompts and
 *  observe what the command reported. Returns a restore() and a live capture of the messages. */
function stubUi(connection: string) {
  const win = vscode.window as any;
  const orig = {
    showInputBox: win.showInputBox,
    showInformationMessage: win.showInformationMessage,
    showErrorMessage: win.showErrorMessage,
  };
  const captured = { info: [] as string[], error: [] as string[] };
  win.showInputBox = async () => connection;
  win.showInformationMessage = (msg: string) => { captured.info.push(msg); return Promise.resolve(undefined); };
  win.showErrorMessage = (msg: string) => { captured.error.push(msg); return Promise.resolve(undefined); };
  const restore = () => Object.assign(win, orig);
  return { captured, restore };
}

describe("pgproj-vscode DB-backed E2E (validate against the live target)", function () {
  this.timeout(120000);

  before(async function () {
    if (!TARGET) {
      this.skip();
    }
    // Point the engine at the locally built CLI so it actually runs. Prefer the absolute path the
    // launcher forwarded (the in-host repoRoot() is wrong under the space-free mirror).
    const dll = process.env.PGPROJ_CLI_DLL || path.resolve(repoRoot(), "src/PgProj.Cli/bin/Debug/net10.0/PgProj.Cli.dll");
    if (fs.existsSync(dll)) {
      await vscode.workspace.getConfiguration("pgproj").update("cliPath", dll, vscode.ConfigurationTarget.Workspace);
    } else {
      this.skip(); // engine not built → can't drive the live validate path
    }
    const ext = vscode.extensions.getExtension(EXT_ID);
    assert.ok(ext, "extension should be discoverable");
    await ext!.activate();
  });

  it("validate against the target server reports success", async function () {
    if (!TARGET) this.skip();
    const { captured, restore } = stubUi(TARGET!);
    try {
      await vscode.commands.executeCommand("pgproj.validate");
    } finally {
      restore();
    }
    assert.ok(
      captured.info.some((m) => /passed|cleanly/i.test(m)),
      `expected a success message; got info=${JSON.stringify(captured.info)} error=${JSON.stringify(captured.error)}`
    );
  });

  it("validate against a dead connection reports failure (not a crash)", async function () {
    if (!TARGET) this.skip();
    const { captured, restore } = stubUi(DEAD);
    try {
      await vscode.commands.executeCommand("pgproj.validate");
    } finally {
      restore();
    }
    assert.ok(
      captured.error.some((m) => /failed/i.test(m)),
      `expected a failure message; got info=${JSON.stringify(captured.info)} error=${JSON.stringify(captured.error)}`
    );
  });
});

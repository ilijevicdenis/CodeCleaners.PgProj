// Headless E2E entrypoint: downloads a VS Code build and runs the suite against the sample workspace.
// NOTE: this requires network access to download VS Code (and on Linux, an xvfb display). In sandboxed
// CI without those, this file still type-checks and compiles; the mocked unit tests are the gate.

import * as path from "path";
import * as os from "os";
import * as fs from "fs";
import { runTests } from "@vscode/test-electron";

// @vscode/test-electron's extension host fails to load `extensionTestsPath` when the path contains a
// SPACE (it splits the arg, e.g. ".../Code cleaners/..." → "Cannot find module 'c:\\repos\\Code'"). A
// directory junction doesn't help (Node/VS Code canonicalise it back to the spaced real path). So when
// the repo path has a space, mirror the runtime build (bundle + compiled tests + manifest + node_modules)
// and the sample workspace into a space-free temp dir and run there. Verified to make the suite run.
function mirrorToSpaceFree(extDev: string, workspace: string): { extDev: string; testsPath: string; workspace: string } {
  const tmpRoot = path.join(os.tmpdir(), "pgproj-vscode-e2e");
  const extDst = path.join(tmpRoot, "ext");
  const wsDst = path.join(tmpRoot, "sample", "AllFeaturesDb");
  fs.mkdirSync(extDst, { recursive: true });
  // Refresh the fast-changing build outputs every run; reuse node_modules if already mirrored (it's large).
  for (const d of ["dist", "out-test", "media"]) {
    fs.rmSync(path.join(extDst, d), { recursive: true, force: true });
    const s = path.join(extDev, d);
    if (fs.existsSync(s)) fs.cpSync(s, path.join(extDst, d), { recursive: true });
  }
  fs.cpSync(path.join(extDev, "package.json"), path.join(extDst, "package.json"));
  if (!fs.existsSync(path.join(extDst, "node_modules"))) {
    fs.cpSync(path.join(extDev, "node_modules"), path.join(extDst, "node_modules"), { recursive: true });
  }
  fs.rmSync(wsDst, { recursive: true, force: true });
  fs.cpSync(workspace, wsDst, { recursive: true });
  console.log(`[e2e] repo path contains a space; mirrored to ${tmpRoot}`);
  return { extDev: extDst, testsPath: path.join(extDst, "out-test", "suite", "index"), workspace: wsDst };
}

async function main(): Promise<void> {
  try {
    // At runtime this file is editors/vscode/out-test/runTest.js, so the extension root is one up.
    let extensionDevelopmentPath = path.resolve(__dirname, "../");
    let extensionTestsPath = path.resolve(__dirname, "./suite/index");
    // The sample AllFeaturesDb project lives at repo-root/sample/AllFeaturesDb.
    let workspace = path.resolve(extensionDevelopmentPath, "../../sample/AllFeaturesDb");

    if (/\s/.test(extensionDevelopmentPath)) {
      const m = mirrorToSpaceFree(extensionDevelopmentPath, workspace);
      extensionDevelopmentPath = m.extDev;
      extensionTestsPath = m.testsPath;
      workspace = m.workspace;
    }

    await runTests({
      extensionDevelopmentPath,
      extensionTestsPath,
      // Stable space-free cache so repeated runs don't re-download VS Code.
      cachePath: path.join(os.tmpdir(), "pgproj-vscode-e2e-cache"),
      launchArgs: [workspace, "--disable-extensions", "--user-data-dir", path.join(os.tmpdir(), "pgproj-vscode-e2e-ud")],
    });
  } catch (err) {
    console.error("E2E tests failed to run:", err);
    process.exit(1);
  }
}

void main();

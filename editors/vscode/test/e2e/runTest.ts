// Headless E2E entrypoint: downloads a VS Code build and runs the suite against the sample workspace.
// NOTE: this requires network access to download VS Code (and on Linux, an xvfb display). In sandboxed
// CI without those, this file still type-checks and compiles; the mocked unit tests are the gate.

import * as path from "path";
import { runTests } from "@vscode/test-electron";

async function main(): Promise<void> {
  try {
    // At runtime this file is editors/vscode/out-test/runTest.js, so the extension root is one up.
    const extensionDevelopmentPath = path.resolve(__dirname, "../");
    const extensionTestsPath = path.resolve(__dirname, "./suite/index");
    // The sample AllFeaturesDb project lives at repo-root/sample/AllFeaturesDb.
    const workspace = path.resolve(
      extensionDevelopmentPath,
      "../../sample/AllFeaturesDb"
    );

    await runTests({
      extensionDevelopmentPath,
      extensionTestsPath,
      launchArgs: [workspace, "--disable-extensions"],
    });
  } catch (err) {
    console.error("E2E tests failed to run:", err);
    process.exit(1);
  }
}

void main();

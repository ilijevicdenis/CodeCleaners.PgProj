// Mocha test runner invoked *inside* the headless VS Code host. Discovers the compiled *.test.js
// suites next to this file and runs them.

import * as path from "path";
import Mocha from "mocha";
import { glob } from "glob";

export async function run(): Promise<void> {
  const mocha = new Mocha({ ui: "bdd", color: true, timeout: 60000 });
  const testsRoot = __dirname;

  const files = await glob("**/*.test.js", { cwd: testsRoot });
  for (const f of files) {
    mocha.addFile(path.resolve(testsRoot, f));
  }

  await new Promise<void>((resolve, reject) => {
    try {
      mocha.run((failures) => (failures > 0 ? reject(new Error(`${failures} test(s) failed.`)) : resolve()));
    } catch (err) {
      reject(err);
    }
  });
}

import { defineConfig } from "vitest/config";

// Unit tests run in plain Node (no VS Code host). They only exercise the pure modules
// (engine, diagnostics mapper, tree model, templates, project-file editing) with a mocked spawner —
// nothing under test imports the `vscode` module, so no shim is needed.
export default defineConfig({
  test: {
    include: ["test/unit/**/*.test.ts"],
    environment: "node",
  },
});

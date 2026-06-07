import { describe, it, expect } from "vitest";
import { resolveServeInvocation } from "../../src/lsp/serverArgs";

describe("resolveServeInvocation", () => {
  it("runs `pgproj serve` directly when cliPath is an exe name", () => {
    const inv = resolveServeInvocation({ cliPath: "pgproj", dotnetPath: "dotnet" });
    expect(inv.command).toBe("pgproj");
    expect(inv.args).toEqual(["serve"]);
  });

  it("routes a .dll through the dotnet host", () => {
    const inv = resolveServeInvocation({ cliPath: "/eng/PgProj.Cli.dll", dotnetPath: "dotnet" });
    expect(inv.command).toBe("dotnet");
    expect(inv.args).toEqual(["/eng/PgProj.Cli.dll", "serve"]);
  });

  it("appends the workspace dir and debounce when provided", () => {
    const inv = resolveServeInvocation(
      { cliPath: "pgproj", dotnetPath: "dotnet" },
      { workspaceDir: "/ws", debounceMs: 200 }
    );
    expect(inv.args).toEqual(["serve", "/ws", "--debounce", "200"]);
  });

  it("clamps a negative/fractional debounce to a non-negative integer", () => {
    const inv = resolveServeInvocation(
      { cliPath: "pgproj", dotnetPath: "dotnet" },
      { debounceMs: -5.7 }
    );
    expect(inv.args).toEqual(["serve", "--debounce", "0"]);
  });

  it("omits debounce when not finite", () => {
    const inv = resolveServeInvocation(
      { cliPath: "pgproj", dotnetPath: "dotnet" },
      { debounceMs: Number.NaN }
    );
    expect(inv.args).toEqual(["serve"]);
  });
});

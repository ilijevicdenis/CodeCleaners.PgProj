import { describe, it, expect } from "vitest";
import { escapeHtml, htmlDocument, makeNonce } from "../../src/webviews/webviewHtml";

describe("escapeHtml", () => {
  it("escapes the five HTML-significant characters", () => {
    expect(escapeHtml(`<a href="x">'&'</a>`)).toBe(
      "&lt;a href=&quot;x&quot;&gt;&#39;&amp;&#39;&lt;/a&gt;"
    );
  });
});

describe("makeNonce", () => {
  it("produces a 32-char alphanumeric nonce, distinct per call", () => {
    const a = makeNonce();
    const b = makeNonce();
    expect(a).toMatch(/^[A-Za-z0-9]{32}$/);
    expect(a).not.toBe(b);
  });
});

describe("htmlDocument", () => {
  it("embeds a strict CSP that allows only the nonce'd script and the cspSource styles", () => {
    const html = htmlDocument({
      title: "T",
      cspSource: "vscode-resource:",
      nonce: "NONCE123",
      body: "<p>body</p>",
      script: "console.log(1)",
    });
    expect(html).toContain("Content-Security-Policy");
    expect(html).toContain("script-src 'nonce-NONCE123'");
    expect(html).toContain(`<script nonce="NONCE123">`);
    expect(html).toContain("<p>body</p>");
    expect(html).toContain("style-src vscode-resource:");
  });
});

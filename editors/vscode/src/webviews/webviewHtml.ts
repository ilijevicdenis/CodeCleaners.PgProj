// Tiny shared helpers for the extension's webviews: a CSP nonce and the boilerplate <head> wiring so
// each panel only writes its <body> + inline <script>. Pure string building (no vscode import) so the
// HTML scaffolding is unit-testable.

/** A cryptographically-irrelevant but unguessable nonce for the script CSP allow-list. */
export function makeNonce(): string {
  const chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";
  let out = "";
  for (let i = 0; i < 32; i++) {
    out += chars.charAt(Math.floor(Math.random() * chars.length));
  }
  return out;
}

/** HTML-escape a string for safe interpolation into text/attribute content. */
export function escapeHtml(s: string): string {
  return s
    .replace(/&/g, "&amp;")
    .replace(/</g, "&lt;")
    .replace(/>/g, "&gt;")
    .replace(/"/g, "&quot;")
    .replace(/'/g, "&#39;");
}

/**
 * Wrap a body + script in a full HTML document with a strict CSP (scripts only from the given nonce).
 * `cspSource` is the webview.cspSource value the panel exposes.
 */
export function htmlDocument(opts: {
  title: string;
  cspSource: string;
  nonce: string;
  body: string;
  script: string;
}): string {
  const { title, cspSource, nonce, body, script } = opts;
  return `<!DOCTYPE html>
<html lang="en">
<head>
  <meta charset="UTF-8" />
  <meta http-equiv="Content-Security-Policy"
        content="default-src 'none'; style-src ${cspSource} 'unsafe-inline'; script-src 'nonce-${nonce}'; img-src ${cspSource};" />
  <meta name="viewport" content="width=device-width, initial-scale=1.0" />
  <title>${escapeHtml(title)}</title>
  <style>
    body { font-family: var(--vscode-font-family); color: var(--vscode-foreground);
           padding: 12px; font-size: var(--vscode-font-size); }
    h2 { margin-top: 0; }
    label { display: block; margin: 10px 0 4px; font-weight: 600; }
    input[type=text], select, textarea {
      width: 100%; box-sizing: border-box; padding: 4px 6px;
      background: var(--vscode-input-background); color: var(--vscode-input-foreground);
      border: 1px solid var(--vscode-input-border, transparent); }
    button { margin: 4px 6px 4px 0; padding: 5px 12px; cursor: pointer;
      background: var(--vscode-button-background); color: var(--vscode-button-foreground); border: none; }
    button.secondary { background: var(--vscode-button-secondaryBackground);
      color: var(--vscode-button-secondaryForeground); }
    button:hover { background: var(--vscode-button-hoverBackground); }
    table { border-collapse: collapse; width: 100%; margin-top: 6px; }
    th, td { text-align: left; padding: 4px 6px; border-bottom: 1px solid var(--vscode-panel-border); }
    .row { display: flex; gap: 8px; align-items: center; }
    .muted { color: var(--vscode-descriptionForeground); }
    .destructive { color: var(--vscode-errorForeground); }
    .checkbox { display: flex; align-items: center; gap: 6px; margin: 6px 0; }
    .toolbar { margin: 10px 0; }
    .empty { padding: 16px; text-align: center; }
  </style>
</head>
<body>
${body}
<script nonce="${nonce}">
${script}
</script>
</body>
</html>`;
}

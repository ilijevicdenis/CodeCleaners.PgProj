// Schema Compare webview (EP-SCHEMACOMPARE #19): source/target endpoint pickers, a checkable diff
// rendered from `compare --source X --target Y -o diff.json` (SchemaCompareReportDto), and apply/script
// actions over the checked subset. The host owns engine calls + the temp diff.json; the webview is a
// thin, checkable list that posts its selection back.

import * as os from "os";
import * as path from "path";
import * as vscode from "vscode";
import { PgProjEngine } from "../engine/engine";
import { SchemaCompareReportDto } from "../engine/schemaCompare";
import { CompareInbound, CompareOutbound, scriptSelectedChanges } from "./protocol";
import { htmlDocument, makeNonce } from "./webviewHtml";

export class SchemaCompareView {
  private static current: SchemaCompareView | undefined;
  private readonly panel: vscode.WebviewPanel;
  private disposed = false;
  private report: SchemaCompareReportDto | undefined;

  static show(
    context: vscode.ExtensionContext,
    engine: PgProjEngine,
    output: vscode.OutputChannel,
    source: string,
    target = ""
  ): void {
    if (SchemaCompareView.current && !SchemaCompareView.current.disposed) {
      SchemaCompareView.current.source = source;
      SchemaCompareView.current.target = target;
      SchemaCompareView.current.post({ type: "init", source, target });
      SchemaCompareView.current.panel.reveal();
      return;
    }
    SchemaCompareView.current = new SchemaCompareView(context, engine, output, source, target);
  }

  private constructor(
    context: vscode.ExtensionContext,
    private readonly engine: PgProjEngine,
    private readonly output: vscode.OutputChannel,
    private source: string,
    private target: string
  ) {
    this.panel = vscode.window.createWebviewPanel(
      "pgproj.schemaCompare",
      "Schema Compare",
      vscode.ViewColumn.Active,
      { enableScripts: true, retainContextWhenHidden: true }
    );
    this.panel.webview.html = this.render();
    this.panel.webview.onDidReceiveMessage(
      (m: CompareInbound) => this.onMessage(m),
      undefined,
      context.subscriptions
    );
    this.panel.onDidDispose(() => (this.disposed = true), undefined, context.subscriptions);
  }

  private post(msg: CompareOutbound): void {
    void this.panel.webview.postMessage(msg);
  }

  private status(message: string, level: "info" | "error" = "info"): void {
    this.post({ type: "status", message, level });
  }

  private async onMessage(msg: CompareInbound): Promise<void> {
    switch (msg.type) {
      case "ready":
        this.post({ type: "init", source: this.source, target: this.target });
        if (this.source && this.target) {
          return this.recompare();
        }
        return;
      case "setSource":
        this.source = msg.spec;
        return;
      case "setTarget":
        this.target = msg.spec;
        return;
      case "recompare":
        return this.recompare();
      case "toggle":
        if (this.report) {
          const c = this.report.changes.find((x) => x.id === msg.id);
          if (c) {
            c.included = msg.included;
          }
        }
        return;
      case "script":
        return this.scriptSelected(msg.includedIds);
      case "apply":
        return this.apply(msg.includedIds);
    }
  }

  private async recompare(): Promise<void> {
    if (!this.source || !this.target) {
      this.status("Pick both a source and a target to compare.", "error");
      return;
    }
    const cwd = this.cwd();
    try {
      // -o writes diff.json next to a temp dir too, so external tooling can pick it up; we parse stdout.
      const outFile = path.join(os.tmpdir(), `pgproj-diff-${Date.now()}.json`);
      const report = await this.engine.compareTwoWay(this.source, this.target, cwd, { outFile });
      this.report = report;
      this.post({ type: "report", report });
      this.status(
        report.inSync
          ? "In sync — source and target match."
          : `${report.changeCount} change(s), ${report.destructiveCount} destructive.`
      );
    } catch (err) {
      this.fail("Compare", err);
    }
  }

  private async scriptSelected(includedIds: string[]): Promise<void> {
    if (!this.report) {
      this.status("Run a compare first.", "error");
      return;
    }
    const sql = scriptSelectedChanges(this.report, includedIds);
    const doc = await vscode.workspace.openTextDocument({ language: "sql", content: sql });
    await vscode.window.showTextDocument(doc, { viewColumn: vscode.ViewColumn.Beside, preview: true });
    this.status(`Scripted ${includedIds.length} selected change(s).`);
  }

  private async apply(includedIds: string[]): Promise<void> {
    if (!this.report) {
      this.status("Run a compare first.", "error");
      return;
    }
    // Applying writes the selected subset to the target. The engine's apply path operates on a project →
    // live DB publish; a partial-selection apply against an arbitrary target endpoint is scripted here and
    // the user runs it (the engine has no "apply this id subset" verb yet). Script + confirm is the safe path.
    const selected = this.report.changes.filter((c) => includedIds.includes(c.id));
    const destructive = selected.filter((c) => c.destructive).length;
    const choice = await vscode.window.showWarningMessage(
      `Apply ${selected.length} selected change(s) (${destructive} destructive)? This generates a script to review and run.`,
      { modal: true },
      "Generate Apply Script"
    );
    if (choice !== "Generate Apply Script") {
      this.status("Apply cancelled.");
      return;
    }
    return this.scriptSelected(includedIds);
  }

  private cwd(): string {
    // Compare source is often a .pgproj path; use its dir, else the first workspace folder, else cwd.
    if (this.source.toLowerCase().endsWith(".pgproj")) {
      return path.dirname(this.source);
    }
    return vscode.workspace.workspaceFolders?.[0]?.uri.fsPath ?? process.cwd();
  }

  private fail(action: string, err: unknown): void {
    this.output.appendLine(`[schema-compare] ${action} error: ${String(err)}`);
    this.status(`${action} failed: ${(err as Error).message}`, "error");
  }

  private render(): string {
    const nonce = makeNonce();
    return htmlDocument({
      title: "Schema Compare",
      cspSource: this.panel.webview.cspSource,
      nonce,
      body: COMPARE_BODY,
      script: COMPARE_SCRIPT,
    });
  }
}

const COMPARE_BODY = `
<h2>Schema Compare</h2>
<div class="row">
  <div style="flex:1">
    <label for="source">Source (desired state)</label>
    <input type="text" id="source" placeholder=".pgproj, .pgpkg, .schema.snapshot, or connection string" />
  </div>
  <div style="flex:1">
    <label for="target">Target (compared against)</label>
    <input type="text" id="target" placeholder=".pgproj, .pgpkg, .schema.snapshot, or connection string" />
  </div>
</div>
<div class="toolbar">
  <button id="compare" type="button">Compare</button>
  <button class="secondary" id="script" type="button">Script (selected)</button>
  <button class="secondary" id="apply" type="button">Apply (selected)</button>
  <button class="secondary" id="all" type="button">Select all</button>
  <button class="secondary" id="none" type="button">Select none</button>
</div>
<div id="status" class="muted"></div>
<table id="diff">
  <thead><tr><th></th><th>Object</th><th>Change</th><th>Type</th></tr></thead>
  <tbody id="diffBody"><tr><td colspan="4" class="empty muted">No comparison yet.</td></tr></tbody>
</table>
`;

const COMPARE_SCRIPT = `
const vscode = acquireVsCodeApi();
let report = null;

function el(id) { return document.getElementById(id); }

function selectedIds() {
  if (!report) return [];
  return report.changes.filter(c => c.included).map(c => c.id);
}

function renderDiff() {
  const body = el("diffBody");
  body.innerHTML = "";
  if (!report || report.changes.length === 0) {
    const tr = document.createElement("tr");
    const td = document.createElement("td");
    td.colSpan = 4; td.className = "empty muted";
    td.textContent = report ? "In sync — no differences." : "No comparison yet.";
    tr.appendChild(td); body.appendChild(tr);
    return;
  }
  report.changes.forEach(c => {
    const tr = document.createElement("tr");
    const cbTd = document.createElement("td");
    const cb = document.createElement("input");
    cb.type = "checkbox"; cb.checked = !!c.included;
    cb.addEventListener("change", () => {
      c.included = cb.checked;
      vscode.postMessage({ type: "toggle", id: c.id, included: cb.checked });
    });
    cbTd.appendChild(cb);
    const descTd = document.createElement("td");
    descTd.textContent = c.description;
    if (c.destructive) descTd.className = "destructive";
    const kindTd = document.createElement("td");
    kindTd.textContent = (c.destructive ? "! " : "") + c.kind;
    const typeTd = document.createElement("td");
    typeTd.textContent = c.objectType;
    tr.appendChild(cbTd); tr.appendChild(descTd); tr.appendChild(kindTd); tr.appendChild(typeTd);
    body.appendChild(tr);
  });
}

el("compare").addEventListener("click", () => {
  vscode.postMessage({ type: "setSource", spec: el("source").value });
  vscode.postMessage({ type: "setTarget", spec: el("target").value });
  vscode.postMessage({ type: "recompare" });
});
el("script").addEventListener("click", () => vscode.postMessage({ type: "script", includedIds: selectedIds() }));
el("apply").addEventListener("click", () => vscode.postMessage({ type: "apply", includedIds: selectedIds() }));
el("all").addEventListener("click", () => { if (report) { report.changes.forEach(c => c.included = true); renderDiff(); } });
el("none").addEventListener("click", () => { if (report) { report.changes.forEach(c => c.included = false); renderDiff(); } });

window.addEventListener("message", (e) => {
  const msg = e.data;
  if (msg.type === "init") {
    el("source").value = msg.source || "";
    el("target").value = msg.target || "";
  } else if (msg.type === "report") {
    report = msg.report;
    renderDiff();
  } else if (msg.type === "status") {
    const st = el("status");
    st.textContent = msg.message;
    st.className = msg.level === "error" ? "destructive" : "muted";
  }
});

vscode.postMessage({ type: "ready" });
`;

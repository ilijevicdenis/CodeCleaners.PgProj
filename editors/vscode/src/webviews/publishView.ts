// Publish webview (EP-PROFILE): a real dialog mirroring the SQL Database Projects publish flow —
// connection picker, SQLCMD-variable grid, options, Save-as-profile (.pgpublish.json), Generate Script,
// and Publish. The host owns all engine calls; the webview is a thin form that posts its state back.

import * as path from "path";
import * as vscode from "vscode";
import { PgProjEngine, PublishOptions } from "../engine/engine";
import {
  PublishFormState,
  PublishInbound,
  PublishOutbound,
  toPublishProfileJson,
  variablesToCli,
} from "./protocol";
import { escapeHtml, htmlDocument, makeNonce } from "./webviewHtml";

export class PublishView {
  private static current: PublishView | undefined;
  private readonly panel: vscode.WebviewPanel;
  private disposed = false;

  static show(
    context: vscode.ExtensionContext,
    engine: PgProjEngine,
    output: vscode.OutputChannel,
    projectFile: string,
    initial: Partial<PublishFormState> = {}
  ): void {
    // One publish dialog at a time; re-target it if a different project is chosen.
    if (PublishView.current && !PublishView.current.disposed) {
      PublishView.current.reset(projectFile, initial);
      PublishView.current.panel.reveal();
      return;
    }
    PublishView.current = new PublishView(context, engine, output, projectFile, initial);
  }

  private state: PublishFormState;

  private constructor(
    context: vscode.ExtensionContext,
    private readonly engine: PgProjEngine,
    private readonly output: vscode.OutputChannel,
    private projectFile: string,
    initial: Partial<PublishFormState>
  ) {
    this.state = defaultState(initial);
    this.panel = vscode.window.createWebviewPanel(
      "pgproj.publish",
      `Publish ${path.basename(projectFile)}`,
      vscode.ViewColumn.Active,
      { enableScripts: true, retainContextWhenHidden: true }
    );
    this.panel.webview.html = this.render();
    this.panel.webview.onDidReceiveMessage(
      (m: PublishInbound) => this.onMessage(m),
      undefined,
      context.subscriptions
    );
    this.panel.onDidDispose(() => (this.disposed = true), undefined, context.subscriptions);
  }

  private reset(projectFile: string, initial: Partial<PublishFormState>): void {
    this.projectFile = projectFile;
    this.state = defaultState(initial);
    this.panel.title = `Publish ${path.basename(projectFile)}`;
    this.post({ type: "init", state: this.state });
  }

  private post(msg: PublishOutbound): void {
    void this.panel.webview.postMessage(msg);
  }

  private status(message: string, level: "info" | "error" = "info"): void {
    this.post({ type: "status", message, level });
  }

  private async onMessage(msg: PublishInbound): Promise<void> {
    switch (msg.type) {
      case "ready":
        this.post({ type: "init", state: this.state });
        return;
      case "generateScript":
        this.state = msg.state;
        return this.generateScript();
      case "publish":
        this.state = msg.state;
        return this.publish();
      case "saveProfile":
        this.state = msg.state;
        return this.saveProfile();
    }
  }

  private opts(): PublishOptions {
    return {
      allowDrops: this.state.allowDrops,
      noTransaction: this.state.noTransaction,
      variables: variablesToCli(this.state.variables),
    };
  }

  private async generateScript(): Promise<void> {
    if (!this.requireConnection()) {
      return;
    }
    const cwd = path.dirname(this.projectFile);
    try {
      const plan = await this.engine.publishDryRun(this.projectFile, this.state.connection, cwd, this.opts());
      if (plan.inSync) {
        this.status("Nothing to publish — target already matches the project.");
        return;
      }
      const doc = await vscode.workspace.openTextDocument({ language: "sql", content: plan.script });
      await vscode.window.showTextDocument(doc, { viewColumn: vscode.ViewColumn.Beside, preview: true });
      this.status(`Generated script: ${plan.changeCount} change(s), ${plan.destructiveCount} destructive.`);
    } catch (err) {
      this.fail("Generate Script", err);
    }
  }

  private async publish(): Promise<void> {
    if (!this.requireConnection()) {
      return;
    }
    const cwd = path.dirname(this.projectFile);
    try {
      const plan = await this.engine.publishDryRun(this.projectFile, this.state.connection, cwd, this.opts());
      if (plan.inSync) {
        this.status("Nothing to publish — target already matches the project.");
        return;
      }
      const choice = await vscode.window.showWarningMessage(
        `Publish ${plan.changeCount} change(s) (${plan.destructiveCount} destructive) to the target?`,
        { modal: true },
        "Publish"
      );
      if (choice !== "Publish") {
        this.status("Publish cancelled.");
        return;
      }
      const result = await this.engine.publish(this.projectFile, this.state.connection, cwd, this.opts());
      this.output.appendLine(result.stdout.trim());
      if (result.stderr.trim()) {
        this.output.appendLine(result.stderr.trim());
      }
      if (result.exitCode === 0) {
        this.status("Publish succeeded.");
        void vscode.window.showInformationMessage("Publish succeeded.");
      } else {
        this.status("Publish failed. See output.", "error");
        this.output.show(true);
      }
    } catch (err) {
      this.fail("Publish", err);
    }
  }

  private async saveProfile(): Promise<void> {
    const defaultUri = vscode.Uri.file(
      path.join(path.dirname(this.projectFile), `${path.basename(this.projectFile, ".pgproj")}.pgpublish.json`)
    );
    const target = await vscode.window.showSaveDialog({
      defaultUri,
      filters: { "Publish Profile": ["json"] },
      saveLabel: "Save Publish Profile",
    });
    if (!target) {
      return;
    }
    try {
      const json = toPublishProfileJson(this.state);
      await vscode.workspace.fs.writeFile(target, Buffer.from(json, "utf8"));
      this.status(`Saved profile to ${path.basename(target.fsPath)} (connection string not stored).`);
      const doc = await vscode.workspace.openTextDocument(target);
      await vscode.window.showTextDocument(doc, { viewColumn: vscode.ViewColumn.Beside });
    } catch (err) {
      this.fail("Save Profile", err);
    }
  }

  private requireConnection(): boolean {
    if (this.state.connection.trim().length === 0) {
      this.status("A connection string is required.", "error");
      return false;
    }
    return true;
  }

  private fail(action: string, err: unknown): void {
    this.output.appendLine(`[publish] ${action} error: ${String(err)}`);
    this.status(`${action} failed: ${(err as Error).message}`, "error");
  }

  private render(): string {
    const nonce = makeNonce();
    return htmlDocument({
      title: `Publish ${path.basename(this.projectFile)}`,
      cspSource: this.panel.webview.cspSource,
      nonce,
      body: PUBLISH_BODY(escapeHtml(path.basename(this.projectFile))),
      script: PUBLISH_SCRIPT,
    });
  }
}

function defaultState(initial: Partial<PublishFormState>): PublishFormState {
  return {
    connection: initial.connection ?? "",
    connectionName: initial.connectionName ?? "",
    allowDrops: initial.allowDrops ?? false,
    noTransaction: initial.noTransaction ?? false,
    variables: initial.variables ?? [],
    targetVersion: initial.targetVersion,
  };
}

const PUBLISH_BODY = (project: string): string => `
<h2>Publish — ${project}</h2>
<p class="muted">The connection string is used only for this publish and is never written to a profile.</p>

<label for="connection">Connection string</label>
<input type="text" id="connection" placeholder="Host=localhost;Port=5432;Username=postgres;Password=...;Database=mydb" />

<label for="connectionName">Connection name (optional, saved in profile)</label>
<input type="text" id="connectionName" placeholder="prod" />

<div class="checkbox"><input type="checkbox" id="allowDrops" /><label for="allowDrops" style="display:inline;margin:0;">Allow destructive changes (--allow-drops)</label></div>
<div class="checkbox"><input type="checkbox" id="noTransaction" /><label for="noTransaction" style="display:inline;margin:0;">Do not wrap in a transaction (--no-transaction)</label></div>

<label>SQLCMD variables</label>
<table id="vars">
  <thead><tr><th>Name</th><th>Value</th><th></th></tr></thead>
  <tbody id="varsBody"></tbody>
</table>
<button class="secondary" id="addVar" type="button">+ Add variable</button>

<div class="toolbar">
  <button id="generate" type="button">Generate Script</button>
  <button id="publish" type="button">Publish</button>
  <button class="secondary" id="saveProfile" type="button">Save as Profile…</button>
</div>
<div id="status" class="muted"></div>
`;

// The webview script: collects form state, renders the variable grid, and round-trips messages.
const PUBLISH_SCRIPT = `
const vscode = acquireVsCodeApi();
let variables = [];

function el(id) { return document.getElementById(id); }

function renderVars() {
  const body = el("varsBody");
  body.innerHTML = "";
  variables.forEach((v, i) => {
    const tr = document.createElement("tr");
    const nameTd = document.createElement("td");
    const nameInput = document.createElement("input");
    nameInput.type = "text"; nameInput.value = v.name; nameInput.placeholder = "Environment";
    nameInput.addEventListener("input", () => { variables[i].name = nameInput.value; });
    nameTd.appendChild(nameInput);
    const valTd = document.createElement("td");
    const valInput = document.createElement("input");
    valInput.type = "text"; valInput.value = v.value; valInput.placeholder = "Production";
    valInput.addEventListener("input", () => { variables[i].value = valInput.value; });
    valTd.appendChild(valInput);
    const delTd = document.createElement("td");
    const del = document.createElement("button");
    del.textContent = "Remove"; del.className = "secondary";
    del.addEventListener("click", () => { variables.splice(i, 1); renderVars(); });
    delTd.appendChild(del);
    tr.appendChild(nameTd); tr.appendChild(valTd); tr.appendChild(delTd);
    body.appendChild(tr);
  });
}

function collect() {
  return {
    connection: el("connection").value,
    connectionName: el("connectionName").value,
    allowDrops: el("allowDrops").checked,
    noTransaction: el("noTransaction").checked,
    variables: variables.filter(v => v.name.trim().length > 0),
  };
}

el("addVar").addEventListener("click", () => { variables.push({ name: "", value: "" }); renderVars(); });
el("generate").addEventListener("click", () => vscode.postMessage({ type: "generateScript", state: collect() }));
el("publish").addEventListener("click", () => vscode.postMessage({ type: "publish", state: collect() }));
el("saveProfile").addEventListener("click", () => vscode.postMessage({ type: "saveProfile", state: collect() }));

window.addEventListener("message", (e) => {
  const msg = e.data;
  if (msg.type === "init") {
    const s = msg.state;
    el("connection").value = s.connection || "";
    el("connectionName").value = s.connectionName || "";
    el("allowDrops").checked = !!s.allowDrops;
    el("noTransaction").checked = !!s.noTransaction;
    variables = (s.variables || []).map(v => ({ name: v.name, value: v.value }));
    renderVars();
  } else if (msg.type === "status") {
    const st = el("status");
    st.textContent = msg.message;
    st.className = msg.level === "error" ? "destructive" : "muted";
  }
});

renderVars();
vscode.postMessage({ type: "ready" });
`;

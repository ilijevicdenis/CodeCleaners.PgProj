// The graphical table designer webview (EP-DESIGNER #26). Opens a table .sql file, loads its structured
// model through the engine (`describe-table`), renders an editable form, and on Save emits the .sql back
// through the engine (`emit-table`) — so the designer can never drift from what deploy writes. The webview
// is locked down with a strict Content-Security-Policy + per-load nonce, matching the project's webview
// posture: no inline handlers, scripts gated to the single nonce'd <script>, default-src 'none'.

import * as fs from "fs";
import * as path from "path";
import * as vscode from "vscode";
import { PgProjEngine } from "../engine/engine";
import { TableModelDto } from "../engine/contract";
import { toFormState, toTableModel, validateForm, TableFormState } from "./tableModel";

/** Messages the webview posts back to the extension host. */
type InboundMessage =
  | { type: "ready" }
  | { type: "save"; state: TableFormState }
  | { type: "cancel" };

/** Messages the host posts to the webview. */
type OutboundMessage =
  | { type: "load"; state: TableFormState; readOnly: ReadOnlyView }
  | { type: "saved" }
  | { type: "error"; message: string };

/** The view-only fields shown but not edited (time-boxed surfaces survive the round-trip verbatim). */
interface ReadOnlyView {
  otherConstraints: string[];
  trailingOptions?: string;
  companions: string[];
}

export class TableDesignerPanel {
  private static readonly viewType = "pgproj.tableDesigner";
  private static readonly open = new Map<string, TableDesignerPanel>();

  private readonly panel: vscode.WebviewPanel;
  private disposed = false;

  static async show(
    _context: vscode.ExtensionContext,
    engine: PgProjEngine,
    output: vscode.OutputChannel,
    sqlFile: string,
    qualifiedName?: string
  ): Promise<void> {
    const key = `${sqlFile}::${qualifiedName ?? ""}`;
    const existing = TableDesignerPanel.open.get(key);
    if (existing) {
      existing.panel.reveal();
      return;
    }
    const panel = vscode.window.createWebviewPanel(
      TableDesignerPanel.viewType,
      `Designer: ${path.basename(sqlFile)}`,
      vscode.ViewColumn.Active,
      { enableScripts: true, retainContextWhenHidden: true }
    );
    const instance = new TableDesignerPanel(panel, engine, output, sqlFile, qualifiedName);
    TableDesignerPanel.open.set(key, instance);
    panel.onDidDispose(() => TableDesignerPanel.open.delete(key));
  }

  private constructor(
    panel: vscode.WebviewPanel,
    private readonly engine: PgProjEngine,
    private readonly output: vscode.OutputChannel,
    private readonly sqlFile: string,
    private readonly qualifiedName: string | undefined
  ) {
    this.panel = panel;
    this.panel.webview.html = this.render();
    this.panel.onDidDispose(() => (this.disposed = true));
    this.panel.webview.onDidReceiveMessage((msg: InboundMessage) => this.onMessage(msg));
  }

  private get cwd(): string {
    return path.dirname(this.sqlFile);
  }

  private post(msg: OutboundMessage): void {
    if (!this.disposed) {
      void this.panel.webview.postMessage(msg);
    }
  }

  private async onMessage(msg: InboundMessage): Promise<void> {
    switch (msg.type) {
      case "ready":
        await this.load();
        return;
      case "save":
        await this.save(msg.state);
        return;
      case "cancel":
        this.panel.dispose();
        return;
    }
  }

  private async load(): Promise<void> {
    try {
      const dto = await this.engine.describeTable(this.sqlFile, this.cwd, this.qualifiedName);
      this.post({
        type: "load",
        state: toFormState(dto),
        readOnly: {
          otherConstraints: dto.otherConstraints,
          trailingOptions: dto.trailingOptions,
          companions: dto.companions,
        },
      });
    } catch (err) {
      this.fail(`Could not load table: ${(err as Error).message}`);
    }
  }

  private async save(state: TableFormState): Promise<void> {
    const problems = validateForm(state);
    if (problems.length > 0) {
      this.fail(problems.join("\n"));
      return;
    }
    try {
      const model: TableModelDto = toTableModel(state);
      const sql = await this.engine.emitTable(model, this.cwd);
      fs.writeFileSync(this.sqlFile, sql, "utf8");
      this.output.appendLine(`[designer] saved ${path.basename(this.sqlFile)} (${model.columns.length} column(s))`);
      this.post({ type: "saved" });
      void vscode.window.showInformationMessage(`Saved ${path.basename(this.sqlFile)}.`);
    } catch (err) {
      this.fail(`Save failed: ${(err as Error).message}`);
    }
  }

  private fail(message: string): void {
    this.output.appendLine(`[designer] error: ${message}`);
    this.post({ type: "error", message });
  }

  private render(): string {
    const nonce = makeNonce();
    const csp = [
      "default-src 'none'",
      `style-src 'nonce-${nonce}'`,
      `script-src 'nonce-${nonce}'`,
    ].join("; ");

    // The body is static; all rendering is done by the nonce'd script from the `load` message. No inline
    // event handlers (CSP forbids them) — the script wires every listener.
    return `<!DOCTYPE html>
<html lang="en">
<head>
  <meta charset="UTF-8" />
  <meta http-equiv="Content-Security-Policy" content="${csp}" />
  <meta name="viewport" content="width=device-width, initial-scale=1.0" />
  <style nonce="${nonce}">${DESIGNER_CSS}</style>
  <title>PgProj Table Designer</title>
</head>
<body>
  <header>
    <h1 id="title">Table Designer</h1>
    <div class="actions">
      <button id="save" type="button">Save</button>
      <button id="cancel" type="button" class="secondary">Close</button>
    </div>
  </header>
  <div id="banner" class="banner hidden"></div>
  <main id="root">Loading…</main>
  <script nonce="${nonce}">${DESIGNER_JS}</script>
</body>
</html>`;
  }
}

function makeNonce(): string {
  let s = "";
  const chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";
  for (let i = 0; i < 32; i++) {
    s += chars.charAt(Math.floor(Math.random() * chars.length));
  }
  return s;
}

// The webview CSS/JS are kept as module string constants so the panel is a single file and the strict CSP
// can inject them under the nonce. The JS speaks the same {type,…} message protocol typed above.

const DESIGNER_CSS = `
  body { font-family: var(--vscode-font-family); color: var(--vscode-foreground); padding: 0 16px 24px; }
  header { display: flex; align-items: center; justify-content: space-between; position: sticky; top: 0;
    background: var(--vscode-editor-background); padding: 12px 0; z-index: 1; }
  h1 { font-size: 1.1rem; margin: 0; }
  h2 { font-size: 0.95rem; margin: 18px 0 6px; border-bottom: 1px solid var(--vscode-panel-border); padding-bottom: 4px; }
  .actions button, .row-actions button { font: inherit; cursor: pointer; }
  button { background: var(--vscode-button-background); color: var(--vscode-button-foreground);
    border: none; padding: 4px 12px; border-radius: 2px; }
  button.secondary { background: var(--vscode-button-secondaryBackground); color: var(--vscode-button-secondaryForeground); }
  table { border-collapse: collapse; width: 100%; margin-bottom: 8px; }
  th, td { text-align: left; padding: 3px 6px; border-bottom: 1px solid var(--vscode-panel-border); font-size: 0.85rem; }
  input[type=text] { width: 100%; box-sizing: border-box; background: var(--vscode-input-background);
    color: var(--vscode-input-foreground); border: 1px solid var(--vscode-input-border); padding: 2px 4px; }
  select { background: var(--vscode-dropdown-background); color: var(--vscode-dropdown-foreground);
    border: 1px solid var(--vscode-dropdown-border); }
  .banner { padding: 8px 10px; border-radius: 3px; margin: 8px 0; white-space: pre-wrap; }
  .banner.error { background: var(--vscode-inputValidation-errorBackground); border: 1px solid var(--vscode-inputValidation-errorBorder); }
  .banner.ok { background: var(--vscode-inputValidation-infoBackground); border: 1px solid var(--vscode-inputValidation-infoBorder); }
  .hidden { display: none; }
  .readonly pre { background: var(--vscode-textCodeBlock-background); padding: 6px 8px; white-space: pre-wrap; font-family: var(--vscode-editor-font-family); }
  .muted { color: var(--vscode-descriptionForeground); font-size: 0.8rem; }
  .row-actions { white-space: nowrap; }
`;

const DESIGNER_JS = `
const vscode = acquireVsCodeApi();
let state = null;

function el(tag, attrs, ...kids) {
  const e = document.createElement(tag);
  for (const k in (attrs || {})) {
    if (k === 'value') e.value = attrs[k];
    else if (k === 'checked') e.checked = !!attrs[k];
    else if (k.startsWith('on')) e.addEventListener(k.slice(2), attrs[k]);
    else if (attrs[k] !== undefined && attrs[k] !== null) e.setAttribute(k, attrs[k]);
  }
  for (const kid of kids) e.append(kid && kid.nodeType ? kid : document.createTextNode(kid == null ? '' : String(kid)));
  return e;
}
function banner(msg, kind) {
  const b = document.getElementById('banner');
  b.textContent = msg; b.className = 'banner ' + kind;
}
function clearBanner() { document.getElementById('banner').className = 'banner hidden'; }

function textInput(value, onInput) { return el('input', { type: 'text', value: value || '', oninput: e => onInput(e.target.value) }); }
function checkbox(checked, onChange) { return el('input', { type: 'checkbox', checked: !!checked, onchange: e => onChange(e.target.checked) }); }

function columnsTable() {
  const rows = state.columns.map((c, i) => el('tr', {},
    el('td', {}, textInput(c.name, v => c.name = v)),
    el('td', {}, textInput(c.dataType, v => c.dataType = v)),
    el('td', {}, checkbox(c.nullable, v => c.nullable = v)),
    el('td', {}, textInput(c.default, v => c.default = v)),
    el('td', {}, checkbox(c.identity, v => { c.identity = v; render(); })),
    el('td', {}, c.identity ? identityKindSelect(c) : el('span', { class: 'muted' }, '—')),
    el('td', {}, textInput(c.generated, v => c.generated = v)),
    el('td', { class: 'row-actions' }, el('button', { class: 'secondary', onclick: () => { state.columns.splice(i,1); render(); } }, 'Delete'))
  ));
  const head = el('tr', {}, ...['Name','Type','Null','Default','Identity','Kind','Generated AS',''].map(h => el('th', {}, h)));
  return el('table', {}, el('thead', {}, head), el('tbody', {}, ...rows));
}
function identityKindSelect(c) {
  const sel = el('select', { onchange: e => c.identityKind = e.target.value });
  for (const opt of ['BY DEFAULT','ALWAYS']) sel.append(el('option', { value: opt, ...(c.identityKind===opt?{selected:''}:{}) }, opt));
  return sel;
}

function keyList(title, list, makeEmpty) {
  const rows = list.map((k, i) => el('tr', {},
    el('td', {}, textInput(k.name, v => k.name = v)),
    el('td', {}, textInput((k.columns||[]).join(', '), v => k.columns = splitCols(v))),
    el('td', { class: 'row-actions' }, el('button', { class: 'secondary', onclick: () => { list.splice(i,1); render(); } }, 'Delete'))
  ));
  const head = el('tr', {}, el('th', {}, 'Name'), el('th', {}, 'Columns (comma-separated)'), el('th', {}, ''));
  return el('div', {},
    el('table', {}, el('thead', {}, head), el('tbody', {}, ...rows)),
    el('button', { onclick: () => { list.push(makeEmpty()); render(); } }, 'Add ' + title)
  );
}

function splitCols(v) { return v.split(',').map(s => s.trim()).filter(s => s.length); }

function fkTable() {
  const rows = state.foreignKeys.map((f, i) => el('tr', {},
    el('td', {}, textInput(f.name, v => f.name = v)),
    el('td', {}, textInput((f.columns||[]).join(', '), v => f.columns = splitCols(v))),
    el('td', {}, textInput(f.referencedSchema, v => f.referencedSchema = v)),
    el('td', {}, textInput(f.referencedTable, v => f.referencedTable = v)),
    el('td', {}, textInput((f.referencedColumns||[]).join(', '), v => f.referencedColumns = splitCols(v))),
    el('td', {}, textInput(f.onDelete, v => f.onDelete = v)),
    el('td', {}, textInput(f.onUpdate, v => f.onUpdate = v)),
    el('td', { class: 'row-actions' }, el('button', { class: 'secondary', onclick: () => { state.foreignKeys.splice(i,1); render(); } }, 'Delete'))
  ));
  const head = el('tr', {}, ...['Name','Columns','Ref schema','Ref table','Ref columns','On delete','On update',''].map(h => el('th', {}, h)));
  return el('div', {},
    el('table', {}, el('thead', {}, head), el('tbody', {}, ...rows)),
    el('button', { onclick: () => { state.foreignKeys.push({ name:'', columns:[], referencedSchema: state.schema, referencedTable:'', referencedColumns:[] }); render(); } }, 'Add foreign key')
  );
}

function checksTable() {
  const rows = state.checks.map((c, i) => el('tr', {},
    el('td', {}, textInput(c.name, v => c.name = v)),
    el('td', {}, textInput(c.expression, v => c.expression = v)),
    el('td', { class: 'row-actions' }, el('button', { class: 'secondary', onclick: () => { state.checks.splice(i,1); render(); } }, 'Delete'))
  ));
  return el('div', {},
    el('table', {}, el('thead', {}, el('tr', {}, el('th', {}, 'Name'), el('th', {}, 'Expression'), el('th', {}, ''))), el('tbody', {}, ...rows)),
    el('button', { onclick: () => { state.checks.push({ name:'', expression:'' }); render(); } }, 'Add check')
  );
}

function indexTable() {
  const rows = state.indexes.map((ix, i) => el('tr', {},
    el('td', {}, textInput(ix.name, v => ix.name = v)),
    el('td', {}, checkbox(ix.unique, v => ix.unique = v)),
    el('td', {}, textInput((ix.columns||[]).join(', '), v => ix.columns = splitCols(v))),
    el('td', {}, textInput(ix.method, v => ix.method = v)),
    el('td', {}, textInput(ix.where, v => ix.where = v)),
    el('td', { class: 'row-actions' }, el('button', { class: 'secondary', onclick: () => { state.indexes.splice(i,1); render(); } }, 'Delete'))
  ));
  const head = el('tr', {}, ...['Name','Unique','Columns','Method','Where',''].map(h => el('th', {}, h)));
  return el('div', {},
    el('table', {}, el('thead', {}, head), el('tbody', {}, ...rows)),
    el('button', { onclick: () => { state.indexes.push({ name:'', unique:false, columns:[] }); render(); } }, 'Add index')
  );
}

function readOnlySection(ro) {
  const kids = [];
  if (ro.trailingOptions) kids.push(el('div', {}, el('div', { class:'muted' }, 'Table options (PARTITION BY / INHERITS / WITH …)'), el('pre', {}, ro.trailingOptions)));
  if (ro.otherConstraints && ro.otherConstraints.length) kids.push(el('div', {}, el('div', { class:'muted' }, 'Other constraints (EXCLUDE …)'), el('pre', {}, ro.otherConstraints.join('\\n'))));
  if (ro.companions && ro.companions.length) kids.push(el('div', {}, el('div', { class:'muted' }, 'Companion statements (RLS / policies / comments)'), el('pre', {}, ro.companions.join('\\n'))));
  if (!kids.length) return el('div', { class:'muted' }, 'None.');
  return el('div', { class:'readonly' }, ...kids);
}

let readOnly = { otherConstraints: [], companions: [] };

function render() {
  document.getElementById('title').textContent = 'Table: ' + state.schema + '.' + state.name;
  const root = document.getElementById('root');
  root.replaceChildren(
    el('h2', {}, 'Columns'), columnsTable(),
    el('button', { onclick: () => { state.columns.push({ name:'', dataType:'text', nullable:true, identity:false, serial:false }); render(); } }, 'Add column'),
    el('h2', {}, 'Primary key'),
    el('div', {},
      el('label', { class:'muted' }, 'Name '), textInput(state.primaryKeyName, v => state.primaryKeyName = v),
      el('label', { class:'muted' }, ' Columns '), textInput((state.primaryKeyColumns||[]).join(', '), v => state.primaryKeyColumns = splitCols(v))),
    el('h2', {}, 'Unique constraints'), keyList('unique', state.unique, () => ({ name:'', columns:[] })),
    el('h2', {}, 'Foreign keys'), fkTable(),
    el('h2', {}, 'Check constraints'), checksTable(),
    el('h2', {}, 'Indexes'), indexTable(),
    el('h2', {}, 'Preserved (view-only)'), readOnlySection(readOnly)
  );
}

window.addEventListener('message', e => {
  const msg = e.data;
  if (msg.type === 'load') { state = msg.state; readOnly = msg.readOnly || readOnly; clearBanner(); render(); }
  else if (msg.type === 'saved') { banner('Saved.', 'ok'); }
  else if (msg.type === 'error') { banner(msg.message, 'error'); }
});
document.getElementById('save').addEventListener('click', () => { clearBanner(); vscode.postMessage({ type: 'save', state }); });
document.getElementById('cancel').addEventListener('click', () => vscode.postMessage({ type: 'cancel' }));
vscode.postMessage({ type: 'ready' });
`;

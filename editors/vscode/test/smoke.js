'use strict';
// Smoke test: runs extension.js against a mocked `vscode` module, the real discope CLI and samples/BuggyApi.
// Usage: node test/smoke.js <path-to-discope.dll>
const Module = require('module');
const fs = require('fs');
const path = require('path');
const assert = require('assert');

const root = path.resolve(__dirname, '../../..');
const sample = path.join(root, 'samples', 'BuggyApi');
const cliDll = process.argv[2];

class Position { constructor(line, character) { this.line = line; this.character = character; } }
class Range {
  constructor(a, b, c, d) {
    if (a instanceof Position) { this.start = a; this.end = b; } else { this.start = new Position(a, b); this.end = new Position(c, d); }
  }
}
class Location { constructor(uri, range) { this.uri = uri; this.range = range; } }
class Diagnostic { constructor(range, message, severity) { Object.assign(this, { range, message, severity }); } }
const Uri = {
  file: (p) => ({ fsPath: p, toString: () => 'file://' + p }),
  joinPath: (u, ...parts) => Uri.file(path.join(u.fsPath, ...parts)),
};

function walk(dir, ext, acc = []) {
  for (const e of fs.readdirSync(dir, { withFileTypes: true })) {
    if (['bin', 'obj', 'node_modules', '.git'].includes(e.name)) continue;
    const p = path.join(dir, e.name);
    if (e.isDirectory()) walk(p, ext, acc);
    else if (ext.some((x) => p.endsWith(x))) acc.push(Uri.file(p));
  }
  return acc;
}

function textDocument(fsPath) {
  const text = fs.readFileSync(fsPath, 'utf8');
  const lineStarts = [0];
  for (let i = 0; i < text.length; i++) if (text[i] === '\n') lineStarts.push(i + 1);
  return {
    getText: () => text,
    offsetAt: (p) => lineStarts[p.line] + p.character,
    positionAt: (o) => { let l = 0; while (l + 1 < lineStarts.length && lineStarts[l + 1] <= o) l++; return new Position(l, o - lineStarts[l]); },
  };
}

const published = new Map();
const commands = new Map();
const posted = [];
let webviewHandler;
let html;

const vscode = {
  Position, Range, Location, Diagnostic, Uri,
  DiagnosticSeverity: { Error: 0, Warning: 1, Information: 2 },
  DiagnosticTag: { Unnecessary: 1 },
  DiagnosticRelatedInformation: class { constructor(location, message) { Object.assign(this, { location, message }); } },
  StatusBarAlignment: { Left: 1 },
  ProgressLocation: { Notification: 15, Window: 10 },
  SymbolKind: { Class: 4, Interface: 10, Struct: 22 },
  ViewColumn: { One: 1, Beside: -2 },
  ThemeColor: class { constructor(id) { this.id = id; } },
  window: {
    createOutputChannel: () => ({ append() {}, appendLine() {}, show() {}, dispose() {} }),
    createStatusBarItem: () => ({ show() {}, dispose() {} }),
    withProgress: (_, task) => task({}, { onCancellationRequested() {} }),
    showErrorMessage: (m) => { throw new Error('unexpected error message: ' + m); },
    showWarningMessage: async () => undefined,
    showInformationMessage: async () => undefined,
    showQuickPick: async (items) => items[0],
    createWebviewPanel: (id, title) => ({
      title,
      webview: {
        cspSource: 'vscode-resource:',
        set html(v) { html = v; },
        get html() { return html; },
        postMessage: (m) => posted.push(m),
        onDidReceiveMessage: (h) => { webviewHandler = h; },
      },
      onDidDispose() {},
      reveal() {},
    }),
    showTextDocument: async (uri, opts) => { vscode._opened = { uri, opts }; },
  },
  languages: {
    createDiagnosticCollection: () => ({ set: (uri, items) => published.set(uri.fsPath, items), clear: () => published.clear(), dispose() {} }),
  },
  commands: {
    registerCommand: (id, fn) => { commands.set(id, fn); return { dispose() {} }; },
    executeCommand: async (id) => { if (id === 'vscode.executeWorkspaceSymbolProvider') throw new Error('no provider'); },
  },
  workspace: {
    getConfiguration: () => ({ get: (k) => ({ path: cliDll, environment: 'Development', framework: 'net8.0', showInfo: true, analyzeOnSave: false }[k]) }),
    findFiles: async (glob) => walk(sample, glob.includes('*.cs') && !glob.includes('proj') ? ['.cs'] : ['.csproj']),
    openTextDocument: async (uri) => textDocument(uri.fsPath),
    asRelativePath: (p) => path.relative(root, p),
    onDidSaveTextDocument: () => ({ dispose() {} }),
  },
};

const resolve = Module._resolveFilename;
Module._resolveFilename = function (request, ...rest) { return request === 'vscode' ? request : resolve.call(this, request, ...rest); };
require.cache.vscode = { id: 'vscode', filename: 'vscode', loaded: true, exports: vscode };

const extension = require('../extension.js');
const context = {
  subscriptions: [],
  extensionPath: path.resolve(__dirname, '..'),
  extensionUri: Uri.file(path.resolve(__dirname, '..')),
  workspaceState: { get: () => undefined, update: async () => {} },
};

(async () => {
  extension.activate(context);
  assert.deepStrictEqual([...commands.keys()].sort(), ['discope.analyze', 'discope.revealInGraph', 'discope.showGraph']);

  await commands.get('discope.analyze')();

  // Problems panel: findings land on the right files and parameters.
  const services = path.join(sample, 'Services', 'Services.cs');
  const controller = path.join(sample, 'Controllers', 'OrdersController.cs');
  const all = [...published.values()].flat();
  assert.strictEqual(all.length, 11, 'all 11 findings published');
  const missing = published.get(controller).find((d) => d.code === 'DI002');
  const controllerText = fs.readFileSync(controller, 'utf8').split('\n');
  assert.strictEqual(controllerText[missing.range.start.line].slice(missing.range.start.character, missing.range.end.character), 'email');
  const captive = published.get(services).find((d) => d.code === 'DI001' && d.message.includes('MemoryCache'));
  const servicesText = fs.readFileSync(services, 'utf8').split('\n');
  assert.strictEqual(servicesText[captive.range.start.line].slice(captive.range.start.character, captive.range.end.character), 'orders');
  assert.ok(captive.relatedInformation[0].message.startsWith('Fix: '));

  // Webview: strict CSP, nonce on every script, and the report is posted once the viewer is ready.
  assert.ok(html.includes("Content-Security-Policy"));
  assert.ok(!/<script>/.test(html), 'every script has a nonce');
  webviewHandler({ type: 'ready' });
  assert.strictEqual(posted.at(-1).type, 'load');
  assert.strictEqual(posted.at(-1).report.app.assembly, 'BuggyApi');

  // Go to source from the graph.
  webviewHandler({ type: 'openType', fullName: 'BuggyApi.Services.MemoryCache' });
  await new Promise((r) => setTimeout(r, 50));
  assert.strictEqual(vscode._opened.uri.fsPath, services);

  console.log(`OK: ${all.length} findings in ${published.size} files, webview loaded, go-to-source works.`);
})().catch((e) => { console.error(e); process.exit(1); });

'use strict';

// DiScope for VS Code. Runs the `discope` CLI, shows its report in a webview (the same viewer as the
// HTML report), and maps findings to the Problems panel on the constructor parameter that causes them.

const vscode = require('vscode');
const cp = require('child_process');
const crypto = require('crypto');
const fs = require('fs');
const os = require('os');
const path = require('path');

const PROJECT_GLOB = '**/*.{csproj,fsproj,vbproj}';
const EXCLUDE_GLOB = '**/{bin,obj,node_modules,.git}/**';
const SEVERITY = {
  error: vscode.DiagnosticSeverity.Error,
  warning: vscode.DiagnosticSeverity.Warning,
  info: vscode.DiagnosticSeverity.Information,
};

/** @type {vscode.WebviewPanel | undefined} */
let panel;
let lastReport;
let lastProject;
let pendingSelect;
let running;
let saveTimer;
let diagnostics, statusItem, output;
const locationCache = new Map();

function activate(context) {
  output = vscode.window.createOutputChannel('DiScope');
  diagnostics = vscode.languages.createDiagnosticCollection('discope');
  statusItem = vscode.window.createStatusBarItem(vscode.StatusBarAlignment.Left, 50);
  statusItem.command = 'discope.showGraph';

  context.subscriptions.push(
    output,
    diagnostics,
    statusItem,
    vscode.commands.registerCommand('discope.analyze', (uri) => analyze(context, { uri, show: true })),
    vscode.commands.registerCommand('discope.showGraph', () => (lastReport ? showGraph(context) : analyze(context, { show: true }))),
    vscode.commands.registerCommand('discope.revealInGraph', () => revealInGraph(context)),
    vscode.workspace.onDidSaveTextDocument((doc) => onSave(context, doc)),
  );
}

function deactivate() {
  if (running) running.kill();
}

// ---------------------------------------------------------------- analysis

async function analyze(context, { uri, show, silent } = {}) {
  const project = await pickProject(context, uri);
  if (!project) return undefined;
  if (running) running.kill();

  const run = () => runCli(project);
  let report;
  try {
    report = silent
      ? await vscode.window.withProgress({ location: vscode.ProgressLocation.Window, title: 'DiScope' }, run)
      : await vscode.window.withProgress(
          { location: vscode.ProgressLocation.Notification, title: `DiScope: analyzing ${path.basename(project)}`, cancellable: true },
          (_, token) => {
            token.onCancellationRequested(() => running && running.kill());
            return run();
          },
        );
  } catch (err) {
    if (err && err.cancelled) return undefined;
    showFailure(err);
    return undefined;
  }

  lastReport = report;
  lastProject = project;
  locationCache.clear();
  updateStatus(report);
  await publishDiagnostics(report, project);
  if (show || panel) showGraph(context);
  return report;
}

/**
 * `discope` from PATH, or the default global tools folder. Editors started from the Dock/Finder often don't
 * get ~/.dotnet/tools on their PATH (the .NET installer's /etc/paths.d entry uses an unexpanded "~").
 */
function resolveTool(configured) {
  if (configured && configured !== 'discope') return configured;
  const exe = process.platform === 'win32' ? 'discope.exe' : 'discope';
  const dirs = (process.env.PATH || '').split(path.delimiter).filter(Boolean);
  if (dirs.some((d) => fs.existsSync(path.join(d, exe)))) return 'discope';
  const globalTool = path.join(os.homedir(), '.dotnet', 'tools', exe);
  return fs.existsSync(globalTool) ? globalTool : 'discope';
}

function runCli(project) {
  const config = vscode.workspace.getConfiguration('discope');
  const tool = resolveTool(config.get('path'));
  const out = path.join(os.tmpdir(), `discope-vscode-${crypto.randomBytes(6).toString('hex')}.json`);
  const args = [project, '--format', 'json', '-o', out, '--fail-on', 'none', '--quiet'];
  const framework = config.get('framework');
  if (framework) args.push('-f', framework);
  args.push('-e', config.get('environment') || 'Development');

  // A path to discope.dll runs through `dotnet`.
  const [file, fileArgs] = tool.toLowerCase().endsWith('.dll') ? ['dotnet', [tool, ...args]] : [tool, args];
  output.appendLine(`> ${file} ${fileArgs.join(' ')}`);

  return new Promise((resolve, reject) => {
    let cancelled = false;
    const child = cp.execFile(file, fileArgs, { cwd: path.dirname(project), maxBuffer: 64 * 1024 * 1024, timeout: 10 * 60 * 1000 }, (error, stdout, stderr) => {
      running = undefined;
      if (stdout) output.append(stdout);
      if (stderr) output.append(stderr);
      if (cancelled) return reject({ cancelled: true });
      if (error && error.code === 'ENOENT') return reject({ notInstalled: true, tool });
      try {
        const report = JSON.parse(fs.readFileSync(out, 'utf8'));
        resolve(report);
      } catch (e) {
        reject({ message: (stderr || (error && error.message) || String(e)).trim() });
      } finally {
        fs.rm(out, { force: true }, () => {});
      }
    });
    const kill = child.kill.bind(child);
    child.kill = () => { cancelled = true; return kill(); };
    running = child;
  });
}

function showFailure(err) {
  if (err && err.notInstalled) {
    vscode.window
      .showErrorMessage(`DiScope: '${err.tool}' was not found. Install it with 'dotnet tool install -g DiScope', or set 'discope.path'.`, 'Install', 'Settings')
      .then((choice) => {
        if (choice === 'Install') {
          const terminal = vscode.window.createTerminal('DiScope');
          terminal.show();
          terminal.sendText('dotnet tool install -g DiScope');
        } else if (choice === 'Settings') {
          vscode.commands.executeCommand('workbench.action.openSettings', 'discope.path');
        }
      });
    return;
  }
  const message = ((err && err.message) || String(err)).replace(/^error:\s*/i, '');
  output.appendLine(message);
  vscode.window.showErrorMessage('DiScope: ' + message.split('\n')[0], 'Show Output').then((c) => c && output.show());
}

async function pickProject(context, uri) {
  if (uri && /\.(cs|fs|vb)proj$/i.test(uri.fsPath)) return uri.fsPath;

  const files = await vscode.workspace.findFiles(PROJECT_GLOB, EXCLUDE_GLOB, 500);
  if (!files.length) {
    vscode.window.showWarningMessage('DiScope: no .NET project found in this workspace.');
    return undefined;
  }

  // Apps (web, worker, exe) first: libraries can't be analyzed on their own.
  const items = files.map((f) => {
    let text = '';
    try { text = fs.readFileSync(f.fsPath, 'utf8'); } catch { /* unreadable */ }
    const isApp = /Sdk="Microsoft\.NET\.Sdk\.(Web|Worker|Razor|BlazorWebAssembly)"|<OutputType>\s*(Win)?Exe\s*<\/OutputType>/i.test(text);
    return { label: path.basename(f.fsPath), description: vscode.workspace.asRelativePath(path.dirname(f.fsPath)), fsPath: f.fsPath, isApp };
  });
  const apps = items.filter((i) => i.isApp);
  const remembered = context.workspaceState.get('discope.project');
  if (apps.length === 1) return apps[0].fsPath;

  const candidates = (apps.length ? apps : items).sort((a, b) => (a.fsPath === remembered ? -1 : b.fsPath === remembered ? 1 : a.label.localeCompare(b.label)));
  const choice = await vscode.window.showQuickPick(candidates, { placeHolder: 'Which app should DiScope analyze? (the project with Program.cs)' });
  if (choice) await context.workspaceState.update('discope.project', choice.fsPath);
  return choice && choice.fsPath;
}

function onSave(context, doc) {
  if (!lastProject || !/\.(cs|fs|vb)$/i.test(doc.fileName)) return;
  if (!vscode.workspace.getConfiguration('discope').get('analyzeOnSave')) return;
  const projectDir = path.dirname(lastProject) + path.sep;
  if (!doc.fileName.startsWith(projectDir)) return;
  clearTimeout(saveTimer);
  saveTimer = setTimeout(() => analyze(context, { uri: vscode.Uri.file(lastProject), silent: true }), 1500);
}

function updateStatus(report) {
  const s = report.summary || {};
  const parts = [];
  if (s.errors) parts.push(`${s.errors} error${s.errors === 1 ? '' : 's'}`);
  if (s.warnings) parts.push(`${s.warnings} warning${s.warnings === 1 ? '' : 's'}`);
  statusItem.text = `$(type-hierarchy) DiScope${parts.length ? ': ' + parts.join(', ') : ''}`;
  statusItem.tooltip = `${report.app.assembly}: ${s.services} services, ${s.errors} errors, ${s.warnings} warnings, ${s.infos} info. Click to show the graph.`;
  statusItem.backgroundColor = s.errors ? new vscode.ThemeColor('statusBarItem.errorBackground') : undefined;
  statusItem.show();
}

// ---------------------------------------------------------------- Problems panel

async function publishDiagnostics(report, project) {
  diagnostics.clear();
  const showInfo = vscode.workspace.getConfiguration('discope').get('showInfo');
  const byId = new Map(report.services.map((n) => [n.id, n]));
  const byFile = new Map();

  for (const d of report.diagnostics) {
    if (d.severity === 'info' && !showInfo) continue;
    const primary = byId.get((d.path && d.path[0]) || (d.nodes || [])[0]);
    let location = primary && primary.sourceType ? await locateType(primary.sourceType) : undefined;
    let range = location && location.range;

    // Point at the constructor parameter responsible when we can find it.
    const parameter = primary && responsibleParameter(primary, d);
    if (location && parameter) {
      const precise = await findParameter(location, parameter);
      if (precise) range = precise;
    }

    const uri = location ? location.uri : vscode.Uri.file(project);
    const diagnostic = new vscode.Diagnostic(range || new vscode.Range(0, 0, 0, 0), d.message, SEVERITY[d.severity] || vscode.DiagnosticSeverity.Information);
    diagnostic.source = 'DiScope';
    diagnostic.code = d.id;
    if (d.help) diagnostic.relatedInformation = [new vscode.DiagnosticRelatedInformation(new vscode.Location(uri, diagnostic.range), 'Fix: ' + d.help)];
    if (d.id === 'DI007') diagnostic.tags = [vscode.DiagnosticTag.Unnecessary];

    const key = uri.toString();
    if (!byFile.has(key)) byFile.set(key, { uri, items: [] });
    byFile.get(key).items.push(diagnostic);
  }

  for (const { uri, items } of byFile.values()) diagnostics.set(uri, items);
}

/** The constructor parameter of `node` that a diagnostic is about. */
function responsibleParameter(node, d) {
  const deps = node.dependencies || [];
  if (d.id === 'DI002' && d.missingType) {
    const dep = deps.find((x) => x.resolution === 'missing' && x.type === d.missingType);
    return dep && dep.site !== 'factory' ? dep.name : undefined;
  }
  if ((d.id === 'DI001' || d.id === 'DI003' || d.id === 'DI004') && d.path && d.path.length > 1) {
    const dep = deps.find((x) => (x.targets || []).includes(d.path[1]));
    return dep && dep.site !== 'factory' ? dep.name : undefined;
  }
  return undefined;
}

/** Find `name` used as a parameter (`Type name,` / `Type name)`) after the type's declaration. */
async function findParameter(location, name) {
  try {
    const doc = await vscode.workspace.openTextDocument(location.uri);
    const text = doc.getText();
    const start = doc.offsetAt(location.range.start);
    const re = new RegExp(`[\\w>\\]?]\\s+(@?${name.replace(/[^\w]/g, '')})\\s*[,)=]`, 'g');
    re.lastIndex = start;
    const m = re.exec(text);
    if (!m) return undefined;
    const offset = m.index + m[0].indexOf(m[1]);
    return new vscode.Range(doc.positionAt(offset), doc.positionAt(offset + m[1].length));
  } catch {
    return undefined;
  }
}

// ---------------------------------------------------------------- navigation

/** Find a type declaration: the workspace symbol provider (C# extension) first, then a text search. */
async function locateType(fullName) {
  if (locationCache.has(fullName)) return locationCache.get(fullName);
  const name = fullName.split('.').pop();
  const namespace = fullName.slice(0, Math.max(0, fullName.length - name.length - 1));
  let found;

  try {
    const symbols = (await vscode.commands.executeCommand('vscode.executeWorkspaceSymbolProvider', name)) || [];
    const kinds = new Set([vscode.SymbolKind.Class, vscode.SymbolKind.Interface, vscode.SymbolKind.Struct]);
    const matches = symbols.filter((s) => (s.name === name || s.name.startsWith(name + '<')) && (kinds.has(s.kind) || s.kind === undefined));
    const best = matches.find((s) => s.containerName && (namespace === s.containerName || namespace.endsWith(s.containerName))) || matches[0];
    if (best) found = best.location;
  } catch {
    // No symbol provider: fall through to the text search.
  }

  if (!found) {
    const re = new RegExp(`\\b(class|record|interface|struct)\\s+(${name})\\b`);
    const files = await vscode.workspace.findFiles('**/*.cs', EXCLUDE_GLOB, 5000);
    for (const file of files) {
      let text;
      try { text = fs.readFileSync(file.fsPath, 'utf8'); } catch { continue; }
      const m = re.exec(text);
      if (!m) continue;
      if (namespace && !text.includes(namespace.split('.').pop())) continue;
      const before = text.slice(0, m.index + m[0].indexOf(m[2]));
      const line = before.split('\n').length - 1;
      const col = before.length - before.lastIndexOf('\n') - 1;
      found = new vscode.Location(file, new vscode.Range(line, col, line, col + name.length));
      break;
    }
  }

  locationCache.set(fullName, found);
  return found;
}

async function openType(fullName) {
  const location = await locateType(fullName);
  if (!location) {
    vscode.window.showInformationMessage(`DiScope: '${fullName}' is not in this workspace (it may come from a package or the framework).`);
    return;
  }
  await vscode.window.showTextDocument(location.uri, { selection: location.range, viewColumn: vscode.ViewColumn.One });
}

async function revealInGraph(context) {
  const editor = vscode.window.activeTextEditor;
  if (!editor) return;
  const range = editor.document.getWordRangeAtPosition(editor.selection.active, /[A-Za-z_][A-Za-z0-9_]*/);
  const word = range && editor.document.getText(range);
  if (!word) return;

  const report = lastReport || (await analyze(context, { show: false }));
  if (!report) return;
  const shortName = (t) => (t || '').split('<')[0].split('.').pop();
  const node =
    report.services.find((n) => shortName(n.implementationType) === word && n.origin !== 'framework') ||
    report.services.find((n) => shortName(n.serviceType) === word && n.origin !== 'framework');
  if (!node) {
    vscode.window.showInformationMessage(`DiScope: '${word}' is not registered in ${report.app.assembly}'s service collection.`);
    return;
  }
  pendingSelect = node.id;
  showGraph(context);
}

// ---------------------------------------------------------------- webview

function showGraph(context) {
  if (!lastReport) return;
  const title = `DI graph · ${lastReport.app.assembly}`;

  if (panel) {
    panel.title = title;
    panel.reveal(undefined, true);
    post({ type: 'load', report: lastReport });
    flushSelect();
    return;
  }

  panel = vscode.window.createWebviewPanel('discope.graph', title, { viewColumn: vscode.ViewColumn.Beside, preserveFocus: true }, {
    enableScripts: true,
    retainContextWhenHidden: true,
    localResourceRoots: [vscode.Uri.joinPath(context.extensionUri, 'media')],
  });
  panel.iconPath = vscode.Uri.joinPath(context.extensionUri, 'media', 'icon.png');
  panel.webview.html = viewerHtml(context, panel.webview);
  panel.onDidDispose(() => { panel = undefined; }, null, context.subscriptions);
  panel.webview.onDidReceiveMessage((m) => {
    if (!m) return;
    if (m.type === 'ready') {
      post({ type: 'load', report: lastReport });
      flushSelect();
    } else if (m.type === 'openType' && m.fullName) {
      openType(m.fullName);
    }
  }, null, context.subscriptions);
}

function post(message) {
  if (panel) panel.webview.postMessage(message);
}

function flushSelect() {
  if (!pendingSelect) return;
  post({ type: 'select', id: pendingSelect });
  pendingSelect = undefined;
}

/** The shared viewer, with a strict CSP: only our own (nonce-tagged) inline scripts may run. */
function viewerHtml(context, webview) {
  const nonce = crypto.randomBytes(16).toString('base64');
  const html = fs.readFileSync(path.join(context.extensionPath, 'media', 'viewer.html'), 'utf8');
  const csp = [
    "default-src 'none'",
    `img-src ${webview.cspSource} data:`,
    `style-src ${webview.cspSource} 'unsafe-inline'`,
    `script-src 'nonce-${nonce}'`,
  ].join('; ');
  return html
    .replace('<head>', `<head>\n<meta http-equiv="Content-Security-Policy" content="${csp}">`)
    .replace(/<script>/g, `<script nonce="${nonce}">`);
}

module.exports = { activate, deactivate, _internal: { responsibleParameter, viewerHtml, resolveTool } };

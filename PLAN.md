# DiScope: DI Graph Visualizer for .NET

> Working name. A tool that scans an app's real `IServiceCollection`, draws the dependency graph,
> and flags captive dependencies, missing registrations, cycles and other DI mistakes.
> Ships as a CLI (`dotnet tool`), Roslyn analyzers (NuGet), a VS Code extension and a Visual Studio extension.

---

## 1. Goals

- **One engine, many front-ends.** All analysis lives in C#; editors only display results.
- **Accurate.** Read the *real* registrations (extension methods, Scrutor scanning, `TryAdd*`, library registrations), not just what's visible in source.
- **Fast feedback.** Static Roslyn analyzers give squiggles while typing; the runtime scan gives the full graph.
- **CI-friendly.** The CLI returns a non-zero exit code on errors.

Non-goals for v1: .NET 6/7 apps, Autofac or other third-party containers, and resolving
dependencies inside factory lambdas (shown as "unknown" for now).

---

## 2. Architecture

```
DiScope/
├─ src/
│  ├─ DiScope.Agent      net8.0, BCL only. Runs INSIDE the target app's process, dumps raw graph JSON
│  ├─ DiScope.Core       net8.0. Graph model + rules + exporters (json, mermaid, html, text)
│  ├─ DiScope.Cli        dotnet tool `discope`. Builds the project, launches the agent, runs Core
│  ├─ DiScope.Viewer     single self-contained HTML/JS file (embedded resource in Core)
│  └─ DiScope.Analyzers  netstandard2.0 Roslyn analyzers + code fixes (NuGet)
├─ editors/
│  ├─ vscode/            TypeScript extension, webview hosts the viewer
│  └─ visualstudio/      VisualStudio.Extensibility extension, tool window hosts the viewer (WebView2)
├─ samples/
│  └─ BuggyApi/          minimal API + controllers with deliberate DI bugs
└─ tests/
   ├─ DiScope.Core.Tests
   └─ DiScope.IntegrationTests   (runs the CLI against samples/BuggyApi, snapshot-tests the JSON)
```

Data flow:

```
discope ./MyApi.csproj
  → dotnet build
  → dotnet msbuild -getProperty:TargetPath        (find MyApi.dll)
  → dotnet exec --runtimeconfig MyApi.runtimeconfig.json
                --depsfile     MyApi.deps.json
                DiScope.Agent.dll --assembly MyApi.dll --out raw.json
  → Core: rules → diagnostics
  → export: html | json | mermaid | text
```

---

## 3. The Agent (the hard part)

### 3.1 Running inside the app's process
Use the same trick as `dotnet ef`: launch the agent with the **app's own** `runtimeconfig.json` and
`deps.json`. The agent then runs on the app's shared frameworks (incl. `Microsoft.AspNetCore.App`) and
resolves the app's NuGet dependencies, so there are no version conflicts.

- The agent references **nothing but the BCL**. It touches `IServiceCollection` / `ServiceDescriptor`
  **only via reflection**, so it works whichever `Microsoft.Extensions.*` version the app uses.
- Target `net8.0`. It rolls forward onto .NET 9/10 apps automatically.
- Set the working directory to the project directory (like `dotnet run`) so `appsettings.json` loads.
- `--environment` flag sets `ASPNETCORE_ENVIRONMENT` / `DOTNET_ENVIRONMENT` (default `Development`).

### 3.2 Capturing the service collection
Subscribe to `DiagnosticListener.AllListeners`, listener name `Microsoft.Extensions.Hosting`, then invoke
the app's entry point (`assembly.EntryPoint`) on a background thread.

| Event | Payload | Action |
|---|---|---|
| `HostBuilding` | `HostApplicationBuilder` (minimal APIs / `WebApplicationBuilder`, .NET 8+) | Keep a reference to `.Services`. **Don't throw yet**: `builder.Host` adapter changes are applied *after* this event, and they mutate the same collection. |
| `HostBuilding` | `HostBuilder` (classic generic host) | Append a `ConfigureServices` callback (delegate built via `Expression.Lambda`, since the types are only known by reflection) that captures the collection. |
| `HostBuilt` | `IHost` | Throw a private `StopTheHostException` so the app **never starts** (no Kestrel, no hosted services). |

Notes:
- In Development, `ValidateOnBuild` may throw *before* `HostBuilt` because of the very bugs we look for.
  That's fine: we already hold the collection reference. Treat "entry thread ended" or "HostBuilt fired"
  as the signal to snapshot.
- Apps that wrap `Main` in `try/catch` (the Serilog pattern) will swallow our exception. Again fine, because the
  agent waits on its own signal, writes the output, then calls `Environment.Exit(0)`.
- Timeout (default 30s) with a clear error if no host is ever built (e.g. the app doesn't use the generic host).
- Fallback mode for class libraries: `--entry MyLib.ServiceCollectionExtensions.AddMyLib` calls an
  `IServiceCollection` extension method on a fresh `ServiceCollection`.

### 3.3 Reading descriptors (reflection)
For each `ServiceDescriptor`, read `ServiceType`, `Lifetime`, and:
- non-keyed: `ImplementationType` / `ImplementationFactory` / `ImplementationInstance`
- keyed (.NET 8+, check `IsKeyedService` first because the non-keyed getters **throw** on keyed descriptors):
  `ServiceKey`, `KeyedImplementationType` / `KeyedImplementationFactory` / `KeyedImplementationInstance`

Kind = `Type | Factory | Instance`. Mark `isFramework` when the service *and* implementation types come from
`Microsoft.*` / `System.*` assemblies. A typical ASP.NET app has 200+ of these, hidden by default.

### 3.4 Constructor selection (mirror Microsoft.Extensions.DependencyInjection)
- Public instance constructors, longest first; pick the **longest one whose parameters are all resolvable**.
- If none is fully resolvable, use the longest one and report its unresolvable parameters as missing.

A parameter is resolvable when it is:
- a registered service type (exact closed type, or an open generic definition is registered: `IRepo<>` → `Repo<>`)
- `IEnumerable<T>`: always resolvable, with edges to **all** non-keyed registrations of `T`
- built-in: `IServiceProvider`, `IServiceScopeFactory`, `IServiceProviderIsService`,
  `IServiceProviderIsKeyedService`, `IKeyedServiceProvider`
- `[FromKeyedServices(key)]` with a matching keyed registration, or `[ServiceKey]`
- optional (has a default value)

Edge target for a single `T` = the **last** registration of `T` (last one wins).

### 3.5 Extra roots (things that consume DI but aren't registered)
- **Controllers** (`ControllerBase` subclasses / `[ApiController]` in the app assembly). Added as `Controller`
  nodes so missing dependencies in controller constructors are caught.
- **Middleware** classes (`Invoke`/`InvokeAsync` parameters), **SignalR hubs** and **Razor Pages** in later versions.
- Minimal API handler parameters: v2 (needs endpoint metadata or Roslyn).

### 3.6 Raw output (schema v1)
```json
{
  "schemaVersion": 1,
  "app": { "assembly": "BuggyApi", "environment": "Development", "runtime": ".NET 8.0.x" },
  "services": [
    {
      "id": "s12",
      "serviceType": "BuggyApi.ICache",
      "implementationType": "BuggyApi.MemoryCache",
      "lifetime": "Singleton",
      "kind": "Type",
      "key": null,
      "isOpenGeneric": false,
      "isFramework": false,
      "registrationIndex": 12,
      "constructor": {
        "parameters": [
          { "name": "orders", "type": "BuggyApi.IOrderRepository",
            "resolution": "service", "targets": ["s15"] }
        ]
      }
    }
  ]
}
```
`resolution` ∈ `service | enumerable | builtin | keyed | optional | missing | unknown` (factories).

---

## 4. Core rules

| ID | Severity | Rule |
|---|---|---|
| DI001 | Error | **Captive dependency**: a singleton depends on a scoped service, *including transitively through transients* (Singleton → Transient → Scoped). Report the full path. |
| DI002 | Error | **Missing registration**: a constructor parameter can't be resolved. |
| DI003 | Error | **Cycle**: Tarjan SCC over the edges; report each SCC of size > 1 and each self-loop. |
| DI004 | Warning | **Transient captured by singleton**: it effectively lives forever. Raise to warning if it is `IDisposable`, otherwise info. |
| DI005 | Warning | **Shadowed registration**: `T` is registered several times but something injects a single `T`, so only the last one is used. |
| DI006 | Warning | **Duplicate registration**: same service + implementation + lifetime registered twice. |
| DI007 | Info | **Possibly unused**: nothing in the graph depends on it. The message says it may still be resolved manually or by minimal API handlers. |
| DI008 | Info | **Factory with unknown dependencies**: listed so users know the graph is incomplete there. |

Only report issues where the consumer or the provider is **user code**. Framework-to-framework issues are ignored.

Diagnostic shape: `{ id, severity, title, message, nodes: [ids], path: [ids] }`.

Later rules (v2): scoped service resolved from the root provider (IL scan for `app.Services.GetRequiredService`
of scoped types), `HttpClient` injected into a singleton, `DbContext` registered as singleton, async disposal issues.

---

## 5. CLI

```
discope [project|sln|dll] [options]

  --format html|json|mermaid|text   (default: html, plus a text summary to the console)
  --out <path>                      output file (default: ./discope-report.html)
  --open                            open the HTML report in the browser
  --no-build                        skip `dotnet build`
  -c, --configuration <cfg>         default Debug
  -f, --framework <tfm>             for multi-targeted projects
  --environment <name>              default Development
  --include-framework               include Microsoft.*/System.* services in the output
  --fail-on error|warning|none      exit code policy for CI (default: error)
  --timeout <seconds>               default 30
```

- Use simple manual argument parsing or System.CommandLine (2.x stable).
- Finding the dll: `dotnet msbuild <proj> -getProperty:TargetPath` (supported since the .NET 8 SDK).
- The text summary looks like the compiler's: `error DI001: Singleton 'MemoryCache' captures scoped 'OrderRepository' (MemoryCache → OrderRepository)`.
  GitHub Actions and Azure DevOps understand this format.

---

## 6. Viewer (single HTML file, no external dependencies)

Self-contained so it works offline and inside VS Code / Visual Studio webviews with a strict CSP.
The JSON is injected into a placeholder: `const DATA = /*__DISCOPE_DATA__*/null;`

Layout:
```
┌───────────────────────────────────────────────────────────────────────────┐
│ BuggyApi · 3 errors · 4 warnings · 2 info   [search…] [☐ framework] [fit] │
├──────────────┬──────────────────────────────────────────┬─────────────────┤
│ Diagnostics  │                Graph (SVG)               │ Selected node   │
│ ● DI001 …    │   [OrdersController]──▶[IOrderService]   │ IOrderService   │
│ ● DI002 …    │             │                  │         │ → OrderService  │
│ ▲ DI005 …    │             ▼                  ▼         │ Scoped · Type   │
│ ○ DI007 …    │       [ICache]════════▶[IOrderRepo]      │ Ctor params …   │
│              │       (red edge = captive)               │ Used by …       │
└──────────────┴──────────────────────────────────────────┴─────────────────┘
```

- **Layout:** layered, left to right (consumers on the left, dependencies on the right). Longest-path layering after
  breaking cycles with DFS, then a few barycenter sweeps to reduce edge crossings. Cubic-bezier edges.
- **Colors:** by lifetime (Singleton / Scoped / Transient / Controller). A red border on nodes with errors and red edges
  for captive paths. Theme tokens with light and dark modes, plus VS Code theme variables when hosted there.
- **Interactions:** pan and zoom; click a node to see details; **double-click for "blast radius"** (show only its transitive
  dependencies and dependents); click a diagnostic to highlight its path and zoom to it; search; framework toggle.
- **Editor bridge:** a "Go to source" button posts `{ type: "openType", fullName }` to the host when it exists:
  - VS Code: `acquireVsCodeApi().postMessage(...)`
  - Visual Studio (WebView2): `window.chrome.webview.postMessage(...)`
  - Plain browser: the button is hidden.

Performance target: 300 visible nodes stay smooth. Above that, cluster by namespace (v2).

---

## 7. Roslyn analyzers (`DiScope.Analyzers`)

These give static, instant feedback in any IDE (Visual Studio, VS Code with C# Dev Kit, Rider) and in CI builds.
- Find `services.AddSingleton/AddScoped/AddTransient<TService, TImpl>()` (+ `Add*(typeof(..))`, `TryAdd*`, keyed)
  calls in the same compilation and build a partial graph.
- Report DI001, DI002 and DI003 on the **constructor parameter** of the offending implementation (squiggle where the fix goes).
- Code fixes: "Change lifetime to Scoped", "Register missing service as Scoped/Transient/Singleton".
- Warn only when sure: if the project calls unknown extension methods, downgrade DI002 to info
  (the runtime scan is the source of truth).

---

## 8. Editor extensions

### VS Code (TypeScript)
- Commands: `DiScope: Analyze project`, `DiScope: Show graph`, `DiScope: Show service under cursor`.
- Runs `discope --format json`, opens a webview with the viewer, and maps diagnostics to the **Problems** panel.
- "Go to source": `vscode.commands.executeCommand('vscode.executeWorkspaceSymbolProvider', typeName)` → open the file at that symbol.
- Re-analyze on save of `Program.cs` / `*ServiceCollectionExtensions.cs` (debounced, optional).
- Publish to the **VS Code Marketplace** and **Open VSX** (Cursor, VSCodium). Fully buildable and testable on macOS.

### Visual Studio (C#)
- `Microsoft.VisualStudio.Extensibility` SDK (out-of-process, .NET 8). Tool window hosting WebView2 with the viewer.
- Command in Solution Explorer context menu: *Analyze DI with DiScope*.
- "Go to source" uses the Roslyn workspace to navigate to the type.
- Diagnostics shown in the Error List.
- Publish to the **Visual Studio Marketplace**. **Windows-only to build and debug**, so use a Windows machine or VM.

### Rider (later, optional)
Kotlin plugin with a JCEF browser panel hosting the same viewer.

---

## 9. Sample app: `samples/BuggyApi`

Minimal API + one controller, no external NuGet packages. Each bug is labeled with the rule it should trigger:

| Bug | Expected |
|---|---|
| `AddSingleton<ICache, MemoryCache>` where `MemoryCache(IOrderRepository)` and the repo is Scoped | DI001 |
| `AddSingleton<IReportService>` → `AddTransient<IFormatter>` → `AddScoped<IUserContext>` | DI001 (transitive) |
| `OrdersController(IEmailSender)`, with `IEmailSender` never registered | DI002 |
| `AService(IBService)` + `BService(IAService)` | DI003 |
| `AddSingleton<IClock, Clock>` + `AddTransient<IGuidGen, GuidGen : IDisposable>` injected into it | DI004 |
| `INotifier` registered twice (Email, Sms) and injected as a single `INotifier` | DI005 |
| `AddScoped<IAuditLog, AuditLog>` twice | DI006 |
| `AddTransient<ILegacyService, LegacyService>` used by nobody | DI007 |
| `AddScoped<IPaymentGateway>(sp => new StripeGateway(...))` | DI008 |
| Open generic `IRepository<>` → `Repository<>`, keyed `[FromKeyedServices("fast")]` | resolved correctly, no diagnostics |

The integration tests run the CLI on this sample and snapshot the JSON diagnostics.

---

## 10. Milestones

| # | Milestone | Done when |
|---|---|---|
| M1 | Agent + Core model + JSON output | `discope samples/BuggyApi --format json` lists every user service with correct edges |
| M2 | Rules DI001–DI008 + text output + exit codes | All bugs in the sample table are reported, and nothing else |
| M3 | HTML viewer | `--open` shows the graph; blast radius, search, diagnostic highlight work |
| M4 | Package the CLI as a `dotnet tool` | `dotnet tool install -g DiScope` works from a local feed; tested on a real-world app (e.g. eShop) |
| M5 | VS Code extension | Graph webview + Problems panel + go to source; published as preview |
| M6 | Roslyn analyzers + code fixes | Squiggles for DI001 to DI003 in the sample; NuGet package |
| M7 | Visual Studio extension | Tool window + Error List on Windows; Marketplace preview |
| M8 | v2 | Factory lambda IL scan, minimal API handlers, root-provider resolution rule, namespace clustering, Rider plugin |

M1 to M6 can all be done on macOS. Only M7 needs Windows.

---

## 11. Risks and mitigations

| Risk | Mitigation |
|---|---|
| Running the app's `Main` has side effects before `Build()` (DB migrations, network calls in top-level code) | Document it; `--entry` mode for libraries; environment override; most apps only do registration before `Build()` |
| Apps that never use the generic host | Clear error message + `--entry` fallback |
| Huge graphs (500+ services) | Framework hidden by default, search, blast-radius focus, namespace clustering in v2 |
| False positives from static analyzers | Analyzers are conservative; the runtime scan is the source of truth |
| Behavior differences between DI versions (constructor selection, keyed services) | Integration tests against .NET 8, 9 and 10 sample builds |
| Name clash with an existing tool | Check NuGet and the Marketplaces before publishing; rename if needed |

# DiScope for VS Code

See your .NET app's dependency injection graph without leaving the editor, and fix the DI bugs it finds.

DiScope captures the **real** `IServiceCollection` of your ASP.NET Core or Worker app (including everything that
library extension methods register), then:

- shows the **interactive graph** in a panel: lifetimes by color, captive paths in red, missing services dashed;
- puts its findings in the **Problems** panel, **on the constructor parameter** that causes them;
- jumps from any service in the graph to its source (**Go to source**), and from the editor to the graph
  (**Reveal in DI Graph**).

| Rule | Finds |
|---|---|
| DI001 | Captive dependency: a singleton or middleware holding a scoped service, even through transients |
| DI002 | Missing registration, in services, controllers, hubs, Razor Pages and middleware |
| DI003 | Circular dependency |
| DI004 | Transient (disposable) captured by a singleton |
| DI005 / DI006 | Shadowed and duplicate registrations |
| DI007 / DI008 / DI009 | Possibly unused, opaque factories, types that can't be constructed |

## Requirements

The `discope` .NET tool and the .NET 8 SDK or newer:

```bash
dotnet tool install -g DiScope
```

## Usage

- **DiScope: Analyze Dependency Injection** from the Command Palette, or right-click a `.csproj` in the Explorer.
- **DiScope: Reveal in DI Graph** from the editor's context menu, on a class name.
- The status bar shows the error and warning counts. Click it to reopen the graph.

## Settings

| Setting | Default | |
|---|---|---|
| `discope.path` | `discope` | The tool, or a path to `discope.dll` |
| `discope.framework` | | Target framework for multi-targeted projects |
| `discope.environment` | `Development` | Environment the app runs in while its services are captured |
| `discope.analyzeOnSave` | `false` | Re-analyze when a C# file of the project is saved |
| `discope.showInfo` | `true` | Show info-level findings in Problems |

# Contributing to DiScope

Thanks for helping! Bug reports, false positives, new rules and fixes are all welcome.

## Reporting a bug or a false positive

Open an issue with:

- the DiScope version (`discope --version`) and your .NET SDK (`dotnet --version`);
- the output of `discope <your project> --format text`;
- if you can, the smallest set of registrations that reproduces it. A few lines of `services.Add...` calls is perfect.

**DiScope reported something that isn't a bug** (a false positive)? That's a bug in DiScope. Please report it.

## Building and testing

You need the .NET 8 and .NET 10 SDKs.

```bash
dotnet build
dotnet test                                                       # unit + end-to-end tests
dotnet run --project src/DiScope.Cli -- samples/BuggyApi --open   # try your change
node editors/vscode/test/smoke.js "$PWD/src/DiScope.Cli/bin/Debug/net8.0/discope.dll"   # VS Code extension
```

## How the code is organized

| Project | Responsibility |
|---|---|
| `src/DiScope.Agent` | Runs **inside** the analyzed app. Must only use the BCL and reflection, never a reference to `Microsoft.Extensions.*`. |
| `src/Shared/GraphModel.cs` | The graph format shared by the agent and Core. Changing it is a schema change. |
| `src/DiScope.Core` | Rules (`Analysis/DiagnosticRules.cs`), exporters, and the viewer (`Viewer/viewer.html`). |
| `src/DiScope.Cli` | The `discope` tool: build, launch the agent, write the report. |
| `editors/vscode` | VS Code extension. Plain JavaScript, no build step. `media/viewer.html` is copied from Core on every build. Don't edit it there. |

## Adding a rule

1. Add an ID in `RuleIds` and a method in `DiagnosticRules`. Report only issues where at least one node is user code
   (`GraphIndex.IsUserCode`).
2. Write a clear `Message` (what is wrong, with short type names) and a `Help` (how to fix it).
3. Add unit tests in `tests/DiScope.Core.Tests/DiagnosticRulesTests.cs` using `TestGraph`, including a test showing the
   rule stays quiet on healthy code.
4. If it's a common real-world mistake, add an example to `samples/BuggyApi` and to `BuggyApiTests`.
5. Document it in the rules table in `README.md`.

## Pull requests

- Keep them focused: one fix or feature per PR.
- Match the existing style. `dotnet build` should produce no warnings.
- Add or update tests, and make sure `dotnet test` passes.
- Add a line to `CHANGELOG.md` under *Unreleased*.

## Releasing (maintainers)

1. Move the *Unreleased* notes in `CHANGELOG.md` under a new version heading.
2. Tag and push: `git tag v0.2.0 && git push origin v0.2.0`.

The Release workflow runs the tests, packs the tool and the VS Code extension with the tag's version, attaches both
to the GitHub release, and publishes each one where its repository secret is set:

| Secret | Publishes to |
|---|---|
| `NUGET_API_KEY` | NuGet.org (the `discope` tool) |
| `VSCE_PAT` | VS Code Marketplace (the `publisher` in `editors/vscode/package.json` must match) |
| `OVSX_PAT` | Open VSX (Cursor, VSCodium) |

To retry a release, run the Release workflow manually (Actions → Release → Run workflow) with the existing tag.

By contributing, you agree that your contributions are licensed under the [MIT License](LICENSE).

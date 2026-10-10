# Changelog

All notable changes to this project are documented here. The format is based on
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this project uses [Semantic Versioning](https://semver.org/).

## [Unreleased]

### Changed

- Viewer: edges are routed through reserved lanes and never pass under services; cycles are drawn as reversed edges.
- Tested against 67 real-world ASP.NET Core, Worker and Orleans apps (20,000+ services); false positives fixed:
  - DI005 no longer flags intentional replacements of framework defaults (e.g. `AuthenticationStateProvider`),
    and now also reports app registrations that replace each other when nothing injects them.
  - DI006 ignores duplicates added by libraries (e.g. options configurators registered per provider).
  - DI002 ignores dependencies inferred from library factories (branches the app never takes) and keyed lookups
    whose key is computed at runtime.
  - DI007 ignores services that extend library types (validators, authentication handlers...) and services
    registered several times.
  - Identical findings on several registrations are merged into one.
- Startup code that throws before `builder.Build()` gets a hint about environment variables and `DISCOPE=1`.

### Fixed

- Apps that catch the host stop and call `Environment.Exit` were reported as failures.

## [0.1.0] - 2026-10-10

### Added

- `discope` dotnet tool: analyzes the real `IServiceCollection` of .NET 8+ apps (minimal hosting, `HostBuilder`,
  Worker services) by running an agent inside the app's process and stopping it before the host is built.
- Rules DI001–DI009: captive dependencies (direct, through transients, in middleware), missing registrations
  (services, controllers, hubs, Razor Pages, middleware), cycles, transients captured by singletons, shadowed and
  duplicate registrations, possibly unused services, opaque factories, types that can't be constructed.
- Dependencies of factory registrations are inferred from their IL.
- HTML (interactive graph), JSON, Mermaid and compiler-style text output, with `--fail-on` exit codes for CI.
- VS Code extension: graph panel, Problems integration on the responsible constructor parameter, go to source,
  reveal in graph.

[Unreleased]: https://github.com/Monem-Benjeddou/DiScope/compare/v0.1.0...HEAD
[0.1.0]: https://github.com/Monem-Benjeddou/DiScope/releases/tag/v0.1.0

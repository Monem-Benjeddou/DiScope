# Changelog

All notable changes to this project are documented here. The format is based on
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this project uses [Semantic Versioning](https://semver.org/).

## [Unreleased]

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

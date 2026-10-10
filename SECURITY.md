# Security Policy

## Supported versions

Security fixes go into the latest release.

## How DiScope runs your code

DiScope executes your application's entry point (up to `builder.Build()`) in order to capture its service
registrations. Only run it on code you trust, just as you would with `dotnet run` or `dotnet ef`.

## Reporting a vulnerability

Please **don't** open a public issue. Report it privately through
[GitHub security advisories](https://github.com/Monem-Benjeddou/DiScope/security/advisories/new).
You should get a response within a week. Please give us a reasonable time to release a fix before you disclose it.

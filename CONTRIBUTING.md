# Contributing to PackageMedic

Thank you for helping make .NET dependency diagnostics clearer and safer.

## Development setup

Install the SDK selected by `global.json`, then run:

```console
dotnet restore PackageMedic.sln --locked-mode
dotnet build --configuration Release
dotnet test --configuration Release
dotnet pack src/PackageMedic.Cli --configuration Release --output artifacts/packages
```

When intentionally changing a NuGet dependency, regenerate and review the committed
content-hash lockfiles with `dotnet restore PackageMedic.sln --use-lock-file --force-evaluate`.

Warnings are errors, nullable analysis is enabled, and output should remain deterministic across platforms.

## Changes to diagnostics

- Prefer no diagnostic over a speculative one.
- Add a real SDK-style fixture for every meaningful MSBuild/CPM edge case.
- Test text/JSON behavior and relevant exit codes.
- Keep the MVP read-only: do not write project, props, assets, or lock files.
- Document new codes or changed semantics under `docs/diagnostics`.

Open an issue before a broad architecture change. Pull requests should explain the user-visible behavior, false-positive considerations, and commands used for verification. Do not include private-feed configuration, credentials, generated `bin`/`obj` output, or unrelated formatting changes.

All contributors must follow [CODE_OF_CONDUCT.md](CODE_OF_CONDUCT.md).

# PackageMedic website

The animated landing page and complete web documentation for PackageMedic. It
follows the repository `VERSION` channel and covers installation, commands,
configuration, baselines, the GitHub Action, reports, diagnostics, security,
troubleshooting, and the read-only safety model.

## Prerequisites

- Node.js `>=22.13.0`

## Quick start

```bash
npm ci --ignore-scripts
npm run dev
```

## Commands

- `npm run dev`: start the local preview
- `npm run build`: create the static GitHub Pages build in `out/`
- `npm run lint`: run the code-quality checks
- `npm test`: build and smoke-test the rendered landing page and every documentation route
- `npm run audit:lockfile`: require exact direct versions, npm-registry HTTPS sources, SHA-512 integrity, and an explicit install-script allowlist
- `npm run audit:security`: fail on any known dependency vulnerability
- `npm run audit:signatures`: verify registry signatures for installed packages

CI installs from the committed lockfile with lifecycle scripts disabled. The lockfile
policy intentionally fails if a new dependency introduces an install script, a non-npm
source, or a missing/weak integrity value so the change has to be reviewed explicitly.

## Updating the release

Change the repository-level `VERSION` file. The .NET package, executable, site
label/channel, install command, and terminal example all reuse that single value.

## Links

- [PackageMedic repository](https://github.com/GonzMeza/package-medic)
- [PackageMedic on NuGet](https://www.nuget.org/packages/PackageMedic.Tool)

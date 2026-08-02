# Architecture

PackageMedic separates the CLI contract from analysis so diagnostics can be tested without a terminal or a live restore.

1. `ProjectDiscovery` resolves the optional target and finds `.csproj`, `.sln`, and `.slnx` inputs.
2. `RestoreRunner` invokes `dotnet restore` unless disabled, captures NU diagnostics, and redacts credential-shaped output.
3. `MsBuildProjectEvaluator` asks the active SDK for evaluated properties and items as JSON. It evaluates each target framework where needed, so imports, conditions, and Central Package Management participate.
4. `AssetsFileReader` reads `project.assets.json`, normalizes direct/resolved/transitive package identities, and imports NuGet log entries.
5. `DiagnosticEngine` runs independent PM001–PM005 rules over normalized project models.
6. The CLI deterministically renders text or JSON and applies the configured failure threshold.

The process is read-only with respect to dependency declarations. Restore is a distinct, visible boundary and is the only step expected to access configured NuGet feeds. MSBuild evaluation uses query switches and does not invoke build targets.

## False-positive policy

Rules consume effective evaluated MSBuild items. PackageMedic does not parse project declarations with regular expressions. XML is used only to recover source line numbers for already-evaluated items and to discover projects in solution formats. PM001 emits only when no affected evaluated project directly references the package and no transitive-pinning-enabled affected project resolves it.

## Runtime choice

The tool targets `net8.0`, while `global.json` pins the repository build to the .NET 9 feature band available when the MVP was created. This deliberately separates the tool runtime from the SDK used to interpret a target repository. The active `dotnet` SDK must still support the project's requested SDK and MSBuild query output.

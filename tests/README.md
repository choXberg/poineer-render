# POIneer.Render Tests

This directory contains the automated test projects for **POIneer.Render**. Tests are separated by purpose so that responsibilities stay clear and CI can remain lightweight.

## Structure

```text
tests/
├─ POIneer.Render.UnitTests/        # fast unit tests for isolated domain/application behavior
├─ POIneer.Render.IntegrationTests/ # infrastructure tests for SQLite, Flyway, filesystem, and processes
├─ POIneer.Render.ContractTests/    # contract and compatibility tests
└─ POIneer.Render.TestHelpers/      # shared test utilities; not a test project itself
```

## Test Project Responsibilities

- **Unit tests** validate isolated core logic. They should be fast, deterministic, and free of external process or machine-specific dependencies.
- **Integration tests** validate infrastructure behavior such as SQLite persistence, Flyway invocation, filesystem handling, and process execution.
- **Contract tests** protect stable contracts such as DTO shape, database behavior, or migration expectations when those contracts become externally relevant.
- **Test helpers** provide shared utilities such as temporary directory helpers and loggers. They should not contain tests of their own and should be excluded from coverage expectations where appropriate.

## Running Tests

Run all tests through the solution:

```bash
dotnet test POIneerRender.sln
```

Run a single test project when iterating locally:

```bash
dotnet test tests/POIneer.Render.UnitTests/POIneer.Render.UnitTests.csproj
```

Generate coverage through the repository helper:

```bash
./scripts/coverage.sh
```

## Guidelines for New Tests

- Use xUnit and FluentAssertions.
- Prefer descriptive test names.
- Keep tests deterministic and isolated.
- Use temporary directories instead of developer-machine paths.
- Prefer `ITemporaryDirectoryFactory` when production behavior depends on temporary filesystem handling.
- Use `NullLogger<T>.Instance` unless a test explicitly asserts log output.
- Add path-sensitive Flyway/SQLite coverage when changing migration or path resolution behavior.
- Put new tests in the narrowest matching project; if a test does not need infrastructure, prefer unit tests.

## Region manifest fixtures

Renderer/server JSON contract fixtures and their expected outcomes live under
`contracts/region-manifest/v1/`. Run the standalone schema and filename checks
with PowerShell 7.4 or later (separate from `dotnet test`):

```powershell
pwsh -NoProfile -File contracts/region-manifest/Test-RegionManifest.ps1
```

See [the contract README](../contracts/region-manifest/README.md) for fixture
semantics and timestamp format validation. These checks need no storage access.

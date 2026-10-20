# Repository Guidelines

## Project Structure & Module Organization

`ZPL-Print-Manager-Especificacao.md` is the Brazilian Portuguese product specification. `ZplPrintManager.slnx` contains six application projects and three xUnit projects:

- `src/ZplPrintManager.Core/`: portable processing rules, state recovery, payload validation, and coordination.
- `src/ZplPrintManager.Contracts/`: shared configuration and versioned IPC messages.
- `src/ZplPrintManager.Infrastructure/`: SQLite, FTP/FTPS, TCP, and administration.
- `src/ZplPrintManager.Infrastructure.Windows/`: DPAPI, pipe authorization, and isolated RAW printing.
- `src/ZplPrintManager.Service/`: service host, workers, and logging.
- `src/ZplPrintManager.Manager/`: WinForms interface.
- `tests/`: Core, Infrastructure, and Windows suites; `installer/` contains Inno Setup and service lifecycle scripts; `docs/` contains operational guidance and acceptance checks.

The service owns database writes and production printing. The manager uses authenticated local Named Pipes.

## Build, Test, and Development Commands

Use the SDK pinned in `global.json`.

```bash
dotnet restore ZplPrintManager.slnx --locked-mode
./scripts/test.sh
dotnet build ZplPrintManager.slnx -c Release --no-restore
dotnet format ZplPrintManager.slnx --no-restore
```

The test script runs portable suites on Ubuntu. Cross-building Windows projects does not execute WinForms or the spooler. On Windows, run `scripts/publish.ps1 -Version 0.1.0` to produce separate self-contained executables, then compile `installer/ZplPrintManager.iss` with Inno Setup 6.4.3.

## Coding Style & Naming Conventions

Follow `.editorconfig`: UTF-8, LF, four-space indentation, and two spaces for JSON/YAML. Use PascalCase types/public members, camelCase locals/parameters, and `Async` suffixes. Match namespaces to `ZplPrintManager.*` projects. Keep Core independent of Windows APIs and Infrastructure implementations. Preserve Portuguese user-facing messages and documentation.

## Testing Guidelines

Use xUnit and behavior-oriented names such as `ArchiveFailure_RetriesOnlyMove`. Tests must use temporary SQLite databases and controlled loopback FTP/TCP fixtures, never production endpoints. Cover failure boundaries, uncertainty, deduplication, authorization, and recovery. There is no numeric coverage threshold. Run Windows-specific tests on Windows and document hardware validation in `docs/HOMOLOGACAO.md`.

## Commit & Pull Request Guidelines

No established commit history exists yet. Use concise imperative subjects, such as `Add persistent print queue`. PRs should explain behavior changes, reference specification sections/issues, report validation and platform limitations, and include screenshots for UI changes. Update dependency locks deliberately. Never commit credentials, operational databases, spool files, sensitive logs, or generated artifacts. Never treat transmission acceptance as proof of physical printing.

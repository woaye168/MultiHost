# Repository Guidelines

## Project Structure & Module Organization

MultiHost uses .NET 10, WPF, and WinForms interop on Windows. Application sources live under `src/` (`App.xaml`, `MultiHost.csproj`, `app.manifest` included), grouped by responsibility:

- `src/Ui/` — `MainWindow.xaml` and its partial classes implement the card grid and commands. `InstanceCard` hosts persistent slots; `CreateInstanceWindow`, `SettingsWindow`, `InstanceConfigWindow`, and `LogWindow` implement dialogs.
- `src/Embedding/` — `EmbedController.cs`, `EmbedSlot.cs`, `WindowEmbedder.cs`, `WindowNative.cs`, and `Win32Api.cs` handle window embedding and native interop.
- `src/Processes/` — `ProcessManager.cs` and `SessionNative.cs` manage process sessions and Job Objects.
- `src/Configuration/` — `InstanceModel.cs`, `WorkspaceConfiguration.cs`, `InstanceCreationService.cs`, `ShellSettingsStore.cs`, and `ThemeManager.cs` handle configuration, templates, and settings.
- `src/Cli/` — `CliRunner.cs`, `GuiPipe.cs`, `CliAcceptance.cs`, and `GuiElevation.cs` connect CLI to GUI.
- `src/Logging/` — `LogService.cs`.
- `Tests/` contains xUnit tests, acceptance scripts, fixtures, and reports. UI resources use XAML; `bin/` and `obj/` are generated.

## Build, Test, and Development Commands

Use Windows and .NET 10 SDK; run from the root:

- `dotnet build src/MultiHost.csproj -c Release` — restore dependencies and build the application.
- `dotnet run --project src/MultiHost.csproj` — launch the GUI, requesting elevation through Windows UAC.
- `dotnet test Tests/MultiHost.Tests.csproj` — run the xUnit suite.
- `powershell -File .\Tests\IsolatedAcceptance.ps1 -ReportPath .\Tests\acceptance-report.json` — check isolated fixture apps and CLI operations. `-InProcess` bypasses IPC and reports skipped transport checks.

## Coding Style & Naming Conventions

Use four-space indentation, file-scoped namespaces, and separate-line braces. Use PascalCase for types and methods, camelCase for parameters and locals, and `_camelCase` for private fields. Suffix asynchronous methods with `Async`. Nullable references and implicit usings are enabled. Preserve Chinese UI text and comments. No formatter or linter is configured; keep formatting changes focused.

## Testing Guidelines

Use TDD: add a failing behavior test, verify failure, implement, and rerun checks. Use xUnit `[Fact]`/`[Theory]` in `Tests/*Tests.cs`; name tests like `UnsafeFoldersAreRejected`. Cover configuration preservation, ownership, path boundaries, cancellation, and lifecycle. No coverage threshold is configured. Users handle manual UI testing; prefer unit and compiled CLI acceptance. Acceptance scripts use disposable applications and isolated workspaces; never use user sessions. Run `Tests/AppStartupAcceptance.ps1` from an administrator terminal; test elevation with injected launchers, never automated UAC. Serialize tests that change global `AppPaths`.

## Commit & Pull Request Guidelines

This checkout lacks Git metadata; commit conventions cannot be verified. Use imperative subjects, such as `Fix window ownership validation`. PRs should describe behavior changes, link issues, list validation commands and results, and include screenshots for UI changes.

## Configuration & Safety

The default workspace is `%LocalAppData%\MultiHost\Workspace`; the GUI supports workspace/template selection. Set `MULTIHOST_WORKSPACE` for isolated runs. Runtime settings use `instances.json` and `shell_settings.json`. Keep defaults application-neutral: pass only configured arguments, retain malformed configuration files, and preserve path validation and Job Object ownership checks.

# Repository Guidelines

## Project Overview
This repository currently contains the product requirements for a system that lets a phone remotely view and control a running PowerShell session on a PC. Phase 1 focuses only on the PC desktop client (Windows `.exe`). The requirements and architecture are documented in `项目需求.md`.

## Project Structure & Module Organization
- `项目需求.md`: product scope, architecture, and functional requirements.
- Phase 1 scope: PC session manager/UI only.
- Planned later modules: mobile clients (iOS/Android/HarmonyOS) and a server-side authentication/permission service for QR + 2FA control handoff.
- When code is added, keep the PC app in its own top-level directory (for example `pc/`) and document any deviations in a README.

## Build, Test, and Development Commands
PC client commands (see `pc/README.md` for details):
- `dotnet build pc/PhoneShell.sln`
- `dotnet run --project pc/src/PhoneShell.App/PhoneShell.App.csproj`

There are no tests configured yet. When adding tests, update `pc/README.md` and this section with the test command.

## Coding Style & Naming Conventions
- Documentation: keep Markdown files concise and aligned with the existing Chinese requirements document.
- Code: use English identifiers and follow the official C#/.NET style guide. Use `PascalCase` for types and public members, `camelCase` for locals/parameters, and suffix interfaces with `I` (for example `ISessionManager`).
- Prefer descriptive names over abbreviations; keep filenames ASCII unless a file is primarily Chinese documentation.

## Testing Guidelines
No testing framework is configured yet. When you add tests, place them next to their module (for example `server/tests/` or `pc/tests/`) and include a clear test command in the module README.

## Commit & Pull Request Guidelines
This directory is not a Git repository, so no commit conventions are established. If/when Git is initialized, use short, imperative commit subjects (for example “Add QR binding flow”) and keep PRs focused. PRs should include a summary, linked issue(s), test results, and UI screenshots when applicable.

## Security & Configuration Tips
QR binding and 2FA are core requirements. Keep secrets out of the repo and document required configuration via an `.env.example` or `config/README.md` once the server component exists.

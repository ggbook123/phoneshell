# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Build & Run

```bash
# Windows WPF app (requires Windows 10/11 + .NET SDK 8.0+)
dotnet build pc/PhoneShell.sln
dotnet run --project pc/src/PhoneShell.App/PhoneShell.App.csproj

# .NET Headless server (cross-platform, .NET 8.0+)
dotnet build linux/PhoneShell.Linux.sln
dotnet run --project linux/PhoneShell.Headless/PhoneShell.Headless.csproj -- --mode server --port 9090

# Node.js server rewrite (linux2/, requires Node.js 18+)
cd linux2 && npm install && npm run dev        # dev with tsx
cd linux2 && npm run build && npm start         # production

# Node.js web panel (linux2/web/, Vue 3 + Vite → single HTML file)
cd linux2/web && npm install && npm run build   # outputs to linux2/web/dist/

# Run all tests (xunit)
dotnet test pc/tests/PhoneShell.Core.Tests/PhoneShell.Core.Tests.csproj

# Run a single test class or method
dotnet test pc/tests/PhoneShell.Core.Tests/PhoneShell.Core.Tests.csproj --filter "FullyQualifiedName~MessageSerializerTests"
dotnet test pc/tests/PhoneShell.Core.Tests/PhoneShell.Core.Tests.csproj --filter "FullyQualifiedName~GroupStoreTests.SomeMethodName"

# Publish self-contained Linux binaries
dotnet publish linux/PhoneShell.Headless/PhoneShell.Headless.csproj -c Release -r linux-x64 --self-contained true -p:PublishSingleFile=true -o linux/publish/linux-x64
```

## Project Overview

PhoneShell lets a phone remotely control terminal sessions (PowerShell, CMD, WSL, bash, etc.) on one or more PCs. One PC can be designated as a "relay server" (hub) that other PCs and mobile clients connect to. Mobile clients (HarmonyOS) connect through the relay server to access any registered PC's terminal.

Requirements are in `项目需求.md`.

## Architecture

There are two independent server implementations, plus a shared Core library:

### .NET stack: `pc/` + `linux/`

Three-project structure sharing `PhoneShell.Core`:

- **PhoneShell.Core** (`pc/src/PhoneShell.Core/`, net8.0) — Platform-agnostic class library. Contains terminal abstractions (`ITerminalSession`, `IShellLocator`), WebSocket protocol types (`Protocol/Messages.cs`, `MessageSerializer`), networking (`RelayServer`, `RelayClient`), services (`AiChatService`, `GroupStore`, `PermissionChecker`, `QrPayloadBuilder`), and models.
  - `Terminals/Windows/` — `ConPtySession` (Windows ConPTY), `WindowsShellLocator` (PowerShell/CMD/WSL/Git Bash).
  - `Terminals/Linux/` — `PtySession` (Linux PTY via forkpty P/Invoke), `LinuxShellLocator` (reads /etc/shells).
  - `TerminalPlatformFactory` auto-selects platform implementations via `RuntimeInformation.IsOSPlatform()`.

- **PhoneShell.App** (`pc/src/PhoneShell.App/`, net8.0-windows, WPF) — Windows-only UI shell. `MainWindow.xaml` wires WebView2 (xterm.js), `MainViewModel.cs` is the central orchestrator for AI chat, command parsing, shell selection, and server settings.

- **PhoneShell.Headless** (`linux/PhoneShell.Headless/`, net8.0) — Cross-platform headless console app. `HeadlessHost.cs` orchestrates terminal sessions + relay networking. Features are independently toggleable modules: `[terminal]`, `[relay-server]`, `[relay-client]`, `[web-panel]`, `[ai-chat]`. Toggle via CLI (`--enable-relay-server`, `--disable-terminal`) or `config.json` `"modules"` section.

**Important build note:** The Headless `.csproj` references `PhoneShell.Core` via a pre-built DLL (`linux/publish/PhoneShell.Core.dll`), not a project reference. Building via `linux/PhoneShell.Linux.sln` handles both projects correctly. If building Headless standalone, you must first build Core and copy the DLL to `linux/publish/`.

### Node.js stack: `linux2/`

TypeScript rewrite of the Linux server (`linux2/src/`) with a Vue 3 web management panel (`linux2/web/`). Uses `node-pty` for terminal sessions, `ws` for WebSocket relay, and mirrors the same JSON protocol as the .NET stack.

The web panel is built as a single HTML file (via `vite-plugin-singlefile`) and embedded/served by the Node.js server.

### HarmonyOS client: `harmony/`

HarmonyOS (ArkTS) mobile client. Uses DevEco Studio for development. Connects to the relay server to view and control PC terminal sessions.

### Terminal pipeline

Host app → `TerminalSessionManager` → `ITerminalSession` (ConPtySession / PtySession) → shell process. Output flows back through `OutputReceived` event → `TerminalOutputBuffer` → WebView2/xterm.js (WPF) or relay broadcast (Headless).

### AI chat pipeline

User message → `AiChatService` (OpenAI-compatible API) → response parsed for `` ```command `` fenced blocks → commands sent to terminal via `ExecuteTerminalCommand` delegate. Auto-exec loop repeats up to 10 steps.

### Networking & group system

- **Relay Server mode:** WebSocket listener (`HttpListener`), manages group membership via shared GroupSecret, forwards terminal I/O. Supports mobile binding and authorization requests.
- **Client mode:** `ClientWebSocket` with auto-reconnect, joins group via `group.join.request` using GroupSecret.
- **Protocol:** JSON messages with `type` discriminator (e.g., `group.*`, `mobile.*`, `auth.*`, `terminal.*`). See `pc/src/PhoneShell.Core/Protocol/Messages.cs`.
- **Mobile binding:** One mobile device binds to a group as admin. Bound mobile receives authorization requests for sensitive operations (opening remote terminals, server migration, kicking members).

### Key threading concern

`HttpClient.SendAsync` and `TerminalOutputStabilizer.WaitForStableOutputAsync` resume on thread pool threads. All `ObservableCollection` mutations and property changes after any `await` must go through `Dispatcher.InvokeAsync()` in WPF app. Headless has no UI thread constraint.

## Configuration

Headless server config priority (highest to lowest): CLI args → environment variables → `config.json`.

Environment variables: `PHONESHELL_MODE`, `PHONESHELL_NAME`, `PHONESHELL_PUBLIC_HOST`, `PHONESHELL_PORT`, `PHONESHELL_RELAY_URL`, `PHONESHELL_RELAY_TOKEN`, `PHONESHELL_GROUP_SECRET`.

Config file locations:
- Windows: `data/` under the app base directory.
- Linux headless: `~/.config/phoneshell/` (follows XDG_CONFIG_HOME).
- Files: `device.json`, `ai-settings.json`, `server-settings.json`, `group.json`, `group-membership.json`, `config.json`.

## Coding Conventions

- English identifiers, C#/.NET style: `PascalCase` for types/public members, `camelCase` for locals/parameters, `I` prefix for interfaces.
- TypeScript code in `linux2/` follows standard TS conventions.
- Documentation markdown files may be in Chinese (aligned with `项目需求.md`).
- AI commands use `` ```command `` fenced blocks (parsed by regex in `ParseCommandBlocks`).

## Key dependencies

- Custom NuGet source configured in root `NuGet.Config` pointing to `.nuget/packages/` (offline-first, all NuGet packages are vendored).
- `Microsoft.Web.WebView2` — WebView2 for xterm.js terminal (WPF only).
- `QRCoder` — QR code generation.
- `node-pty` — PTY for Node.js server (linux2).

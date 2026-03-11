# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Build & Run

```bash
# Windows WPF app
dotnet build pc/PhoneShell.sln
dotnet run --project pc/src/PhoneShell.App/PhoneShell.App.csproj

# Linux/cross-platform headless
dotnet build linux/PhoneShell.Linux.sln
dotnet run --project linux/PhoneShell.Headless/PhoneShell.Headless.csproj -- --mode server --port 9000

# Run tests
dotnet test pc/tests/PhoneShell.Core.Tests/PhoneShell.Core.Tests.csproj
```

Windows 10/11 with .NET SDK 8.0+ required for WPF app. Linux headless runs on any platform with .NET 8.0+. Unit tests use xunit.

## Project Overview

PhoneShell lets a phone remotely control terminal sessions (PowerShell, CMD, WSL, bash, etc.) on one or more PCs. One PC can be designated as a "relay server" (hub) that other PCs and mobile clients connect to. Mobile clients (HarmonyOS planned) connect through the relay server to access any registered PC's terminal.

Requirements are in `项目需求.md`. HarmonyOS client code lives in `harmony/`.

## Architecture

Three-project structure: `PhoneShell.Core` (platform-agnostic class library) + `PhoneShell.App` (WPF UI shell) + `PhoneShell.Headless` (cross-platform console app).

### PhoneShell.Core (`pc/src/PhoneShell.Core/`, net8.0)
Platform-independent core containing:
- **Terminals/** — `ITerminalSession` / `IShellLocator` interfaces, `TerminalSessionManager`, `TerminalSnapshot` record, `TerminalPlatformFactory` (auto-selects platform implementations).
  - `Terminals/Windows/` — `ConPtySession` (Windows ConPTY), `WindowsShellLocator` (PowerShell/CMD/WSL/Git Bash).
  - `Terminals/Linux/` — `PtySession` (Linux PTY via forkpty P/Invoke), `LinuxShellLocator` (reads /etc/shells).
- **Protocol/** — WebSocket message types (`Messages.cs`) and `MessageSerializer` for PC↔server↔mobile communication (device registration, terminal I/O, control ownership, group management, mobile binding, authorization).
- **Networking/** — `RelayServer` (HttpListener + WebSocket server mode with group management) and `RelayClient` (ClientWebSocket client mode with auto-reconnect and group joining).
- **Services/** — `AiChatService`, `AiSettingsStore`, `DeviceIdentityStore`, `GroupStore`, `PermissionChecker`, `QrPayloadBuilder`, `ServerSettingsStore`, `TerminalOutputBuffer`, `TerminalOutputStabilizer`, `VirtualScreen`.
- **Models/** — `AiSettings`, `ChatMessage`, `ControlOwner`, `DeviceIdentity`, `GroupInfo` (GroupInfo, GroupMember, MemberRole, GroupMembership), `ServerSettings`.

### PhoneShell.App (`pc/src/PhoneShell.App/`, net8.0-windows, WPF)
Windows-only UI shell containing:
- `MainWindow.xaml/.cs` — Wires WebView2 (xterm.js), TerminalSessionManager, and ViewModel.
- `ViewModels/MainViewModel.cs` — Central orchestrator. Owns AI chat loop, command parsing, shell selection, server settings, and all bindable state.
- `Services/QrCodeService.cs` — QR code generation (depends on WPF BitmapImage).
- `Services/TerminalSnapshotService.cs` — Captures xterm.js screen state via WebView2.
- `Utilities/` — WPF helpers (RelayCommand, AsyncRelayCommand, ObservableObject, InvertBoolConverter).

### PhoneShell.Headless (`linux/PhoneShell.Headless/`, net8.0)
Cross-platform headless console app (primarily for Linux servers, also works on Windows):
- `Program.cs` — Entry point with CLI argument parsing, `--setup` interactive wizard, `--help`.
- `HeadlessHost.cs` — Core orchestrator. Manages terminal sessions + relay networking without GUI. Conditionally starts modules based on config.
- `HeadlessConfig.cs` — Configuration model with modular feature toggles and CLI override support.

#### Module Architecture
Headless features are independently toggleable via config or CLI flags:
- **[terminal]** — PTY/ConPTY session management (default: enabled).
- **[relay-server]** — WebSocket relay hub mode (default: disabled).
- **[relay-client]** — Connect to existing relay server (default: enabled).
- **[web-panel]** — Web management UI (TODO, default: disabled).
- **[ai-chat]** — AI assistant integration (TODO, default: disabled).

Toggle via CLI: `--enable-relay-server`, `--disable-terminal`, etc.
Toggle via config.json `"modules"` section.

### Terminal pipeline
`MainWindow` / `HeadlessHost` → `TerminalSessionManager` → `ITerminalSession` (ConPtySession on Windows, PtySession on Linux) → shell process. Output flows back through `OutputReceived` event → `TerminalOutputBuffer` → WebView2/xterm.js (WPF) or relay broadcast (Headless).

`TerminalPlatformFactory` selects the correct `ITerminalSession` and `IShellLocator` implementation based on `RuntimeInformation.IsOSPlatform()`.

### AI chat pipeline
User message → `AiChatService` (OpenAI-compatible API) → response parsed for `` ```command `` blocks → commands sent to terminal via `ExecuteTerminalCommand` delegate. Auto-exec loop repeats up to 10 steps.

### Networking
- **Relay Server mode:** PC starts WebSocket listener, accepts other PC and mobile connections, manages group membership via shared GroupSecret, forwards terminal I/O between clients. Supports mobile binding and authorization requests.
- **Client mode:** PC connects to a relay server, joins group via `group.join.request` using GroupSecret, forwards I/O bidirectionally. Supports remote terminal events for PC-to-PC terminal access.
- **Group system:** Devices join groups using a shared secret. The server creates and persists group data (`data/group.json`). Clients store minimal membership info (`data/group-membership.json`). GroupSecret replaces the legacy AuthToken for authentication.
- **Mobile binding:** One mobile device can bind to a group as admin. Bound mobile receives authorization requests for sensitive operations (e.g., opening remote terminals, server migration, kicking members).
- **Protocol:** JSON messages with `type` discriminator. See `Protocol/Messages.cs` for all message types including group.*, mobile.*, and auth.* messages.

### Key threading concern
`HttpClient.SendAsync` and `TerminalOutputStabilizer.WaitForStableOutputAsync` resume on thread pool threads. All `ObservableCollection` mutations and property changes after any `await` must go through `Dispatcher.InvokeAsync()` (WPF app only; headless has no UI thread).

## Key Files

- `Core/Terminals/ITerminalSession.cs` — Terminal session abstraction (Start, Write, Resize, OutputReady).
- `Core/Terminals/IShellLocator.cs` — Shell detection abstraction (ShellInfo record, available shells list).
- `Core/Terminals/TerminalPlatformFactory.cs` — Creates platform-appropriate session and shell locator.
- `Core/Terminals/Windows/ConPtySession.cs` — Windows ConPTY implementation.
- `Core/Terminals/Windows/WindowsShellLocator.cs` — Detects PowerShell, CMD, WSL distros, Git Bash.
- `Core/Terminals/Linux/PtySession.cs` — Linux PTY implementation via forkpty() P/Invoke.
- `Core/Terminals/Linux/LinuxShellLocator.cs` — Reads /etc/shells, detects bash/zsh/fish etc.
- `Core/Networking/RelayServer.cs` — WebSocket server for hub mode with group management.
- `Core/Networking/RelayClient.cs` — WebSocket client with auto-reconnect and group joining.
- `Core/Protocol/Messages.cs` — All WebSocket message type definitions (device, terminal, group, mobile bind, auth).
- `Core/Models/GroupInfo.cs` — Group data model (GroupInfo, GroupMember, MemberRole, GroupMembership).
- `Core/Services/GroupStore.cs` — Group data persistence (server-side group.json, client-side group-membership.json).
- `Core/Services/PermissionChecker.cs` — Determines if actions require mobile authorization.
- `Core/Services/AiChatService.cs` — OpenAI-compatible chat with multi-shell awareness.
- `App/ViewModels/MainViewModel.cs` — Central WPF orchestrator.
- `App/MainWindow.xaml` — UI layout with Server Settings, Shell Selector, AI Chat, terminal.
- `App/Assets/terminal.html` — xterm.js terminal UI loaded via WebView2.
- `Headless/Program.cs` — Headless entry point with CLI and interactive setup.
- `Headless/HeadlessHost.cs` — Headless orchestrator (terminal sessions + relay).
- `Headless/HeadlessConfig.cs` — Config model with module toggles.

## Coding Conventions

- English identifiers, C#/.NET style: `PascalCase` for types/public members, `camelCase` for locals/parameters, `I` prefix for interfaces.
- Documentation markdown files may be in Chinese (aligned with `项目需求.md`).
- AI commands use `` ```command `` fenced blocks (parsed by regex in `ParseCommandBlocks`).
- Runtime data paths:
  - Windows: `data/` under the app base directory: `device.json`, `ai-settings.json`, `server-settings.json`, `group.json`, `group-membership.json`, `ai-debug.log`.
  - Linux headless: `~/.config/phoneshell/` (follows XDG_CONFIG_HOME convention), same file names in `data/` subdirectory.

## NuGet Dependencies

- `Microsoft.Web.WebView2` — WebView2 control for xterm.js terminal (WPF app only)
- `QRCoder` — QR code generation for mobile pairing (WPF app only)

Custom NuGet source configured in root `NuGet.Config` pointing to `.nuget/packages/`.

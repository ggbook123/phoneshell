# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Build & Run

```bash
dotnet build pc/PhoneShell.sln
dotnet run --project pc/src/PhoneShell.App/PhoneShell.App.csproj
```

No test framework is configured yet. Windows 10/11 with .NET SDK 8.0+ required.

## Project Overview

PhoneShell lets a phone remotely control terminal sessions (PowerShell, CMD, WSL, bash, etc.) on one or more PCs. One PC can be designated as a "relay server" (hub) that other PCs and mobile clients connect to. Mobile clients (HarmonyOS planned) connect through the relay server to access any registered PC's terminal.

Requirements are in `项目需求.md`. HarmonyOS client code lives in `harmony/`.

## Architecture

Two-project solution: `PhoneShell.Core` (platform-agnostic class library) + `PhoneShell.App` (WPF UI shell).

### PhoneShell.Core (`pc/src/PhoneShell.Core/`, net8.0)
Platform-independent core containing:
- **Terminals/** — `ITerminalSession` / `IShellLocator` interfaces, `TerminalSessionManager`, `TerminalSnapshot` record. Windows implementations in `Terminals/Windows/` (ConPTY, shell detection for PowerShell/CMD/WSL/Git Bash). Linux/macOS folders reserved for future PTY implementations.
- **Protocol/** — WebSocket message types (`Messages.cs`) and `MessageSerializer` for PC↔server↔mobile communication (device registration, terminal I/O, control ownership).
- **Networking/** — `RelayServer` (HttpListener + WebSocket server mode) and `RelayClient` (ClientWebSocket client mode with auto-reconnect).
- **Services/** — `AiChatService`, `AiSettingsStore`, `DeviceIdentityStore`, `QrPayloadBuilder`, `ServerSettingsStore`, `TerminalOutputBuffer`, `TerminalOutputStabilizer`, `VirtualScreen`.
- **Models/** — `AiSettings`, `ChatMessage`, `ControlOwner`, `DeviceIdentity`, `ServerSettings`.

### PhoneShell.App (`pc/src/PhoneShell.App/`, net8.0-windows, WPF)
Windows-only UI shell containing:
- `MainWindow.xaml/.cs` — Wires WebView2 (xterm.js), TerminalSessionManager, and ViewModel.
- `ViewModels/MainViewModel.cs` — Central orchestrator. Owns AI chat loop, command parsing, shell selection, server settings, and all bindable state.
- `Services/QrCodeService.cs` — QR code generation (depends on WPF BitmapImage).
- `Services/TerminalSnapshotService.cs` — Captures xterm.js screen state via WebView2.
- `Utilities/` — WPF helpers (RelayCommand, AsyncRelayCommand, ObservableObject, InvertBoolConverter).

### Terminal pipeline
`MainWindow` → `TerminalSessionManager` → `ITerminalSession` (ConPtySession on Windows) → shell process. Output flows back through `OutputReceived` event → `TerminalOutputBuffer` → WebView2/xterm.js.

### AI chat pipeline
User message → `AiChatService` (OpenAI-compatible API) → response parsed for `` ```command `` blocks → commands sent to terminal via `ExecuteTerminalCommand` delegate. Auto-exec loop repeats up to 10 steps.

### Networking
- **Relay Server mode:** PC starts WebSocket listener, accepts other PC and mobile connections, maintains device registry, forwards terminal I/O between clients.
- **Client mode:** PC connects to a relay server, registers its terminal, forwards I/O bidirectionally.
- **Protocol:** JSON messages with `type` discriminator. See `Protocol/Messages.cs` for all message types.

### Key threading concern
`HttpClient.SendAsync` and `TerminalOutputStabilizer.WaitForStableOutputAsync` resume on thread pool threads. All `ObservableCollection` mutations and property changes after any `await` must go through `Dispatcher.InvokeAsync()`.

## Key Files

- `Core/Terminals/ITerminalSession.cs` — Terminal session abstraction (Start, Write, Resize, OutputReady).
- `Core/Terminals/IShellLocator.cs` — Shell detection abstraction (ShellInfo record, available shells list).
- `Core/Terminals/Windows/ConPtySession.cs` — Windows ConPTY implementation.
- `Core/Terminals/Windows/WindowsShellLocator.cs` — Detects PowerShell, CMD, WSL distros, Git Bash.
- `Core/Networking/RelayServer.cs` — WebSocket server for hub mode.
- `Core/Networking/RelayClient.cs` — WebSocket client with auto-reconnect.
- `Core/Protocol/Messages.cs` — All WebSocket message type definitions.
- `Core/Services/AiChatService.cs` — OpenAI-compatible chat with multi-shell awareness.
- `App/ViewModels/MainViewModel.cs` — Central WPF orchestrator.
- `App/MainWindow.xaml` — UI layout with Server Settings, Shell Selector, AI Chat, terminal.
- `App/Assets/terminal.html` — xterm.js terminal UI loaded via WebView2.

## Coding Conventions

- English identifiers, C#/.NET style: `PascalCase` for types/public members, `camelCase` for locals/parameters, `I` prefix for interfaces.
- Documentation markdown files may be in Chinese (aligned with `项目需求.md`).
- AI commands use `` ```command `` fenced blocks (parsed by regex in `ParseCommandBlocks`).
- Runtime data goes in `data/` under the app base directory: `device.json`, `ai-settings.json`, `server-settings.json`, `ai-debug.log`.

## NuGet Dependencies

- `Microsoft.Web.WebView2` — WebView2 control for xterm.js terminal
- `QRCoder` — QR code generation for mobile pairing

Custom NuGet source configured in root `NuGet.Config` pointing to `.nuget/packages/`.

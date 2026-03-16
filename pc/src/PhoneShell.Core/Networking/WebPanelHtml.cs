namespace PhoneShell.Core.Networking;

/// <summary>
/// Contains the full web panel HTML as a const string.
/// The panel is a single-page application with embedded CSS and JS that communicates
/// with the relay server via REST APIs and WebSocket for real-time updates.
/// xterm.js resources are loaded from separate HTTP routes served by WebPanelModule.
/// </summary>
internal static class WebPanelHtml
{
    internal const string PanelHtml = @"<!DOCTYPE html>
<html lang=""en"">
<head>
<meta charset=""utf-8""/>
<meta name=""viewport"" content=""width=device-width, initial-scale=1""/>
<title>PhoneShell — Command Center</title>
<link rel=""icon"" href=""data:image/svg+xml,<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 100 100'><rect rx='12' width='100' height='100' fill='%230A0E14'/><text x='50' y='68' text-anchor='middle' font-size='52' fill='%2300D4AA'>❯</text></svg>""/>
<link rel=""stylesheet"" href=""/panel/xterm.min.css""/>
<style>
/* === Command Center Dark Design System === */
:root {
  --s0: #0A0E14; --s1: #0F1319; --s2: #151A22; --s3: #1A2029; --s4: #1F2733;
  --b1: #1E2530; --b2: #2A3341;
  --accent: #00D4AA; --accent-dim: #00A888; --accent-subtle: #0D3D35;
  --danger: #FF4757; --warning: #FFBE0B; --info: #4DA6FF;
  --t1: #E8ECF1; --t2: #A0AABB; --t3: #5C6878;
  --radius: 8px; --radius-sm: 6px;
  --font: 'Inter', -apple-system, 'Segoe UI', sans-serif;
  --mono: 'JetBrains Mono', 'Cascadia Code', 'Fira Code', 'Consolas', monospace;
  --sidebar-w: 280px;
  --topbar-h: 52px;
  --statusbar-h: 28px;
}

*, *::before, *::after { box-sizing: border-box; margin: 0; padding: 0; }

html, body {
  height: 100%; width: 100%;
  font-family: var(--font);
  font-size: 13px;
  color: var(--t1);
  background: var(--s0);
  overflow: hidden;
  -webkit-font-smoothing: antialiased;
}

/* Scrollbar */
::-webkit-scrollbar { width: 6px; height: 6px; }
::-webkit-scrollbar-track { background: transparent; }
::-webkit-scrollbar-thumb { background: rgba(255,255,255,0.08); border-radius: 3px; }
::-webkit-scrollbar-thumb:hover { background: rgba(255,255,255,0.15); }

/* === Layout === */
#app {
  display: flex; flex-direction: column;
  height: 100vh; width: 100vw;
}

/* Top bar */
.topbar {
  height: var(--topbar-h); min-height: var(--topbar-h);
  display: flex; align-items: center; gap: 12px;
  padding: 0 16px;
  background: var(--s1);
  border-bottom: 1px solid var(--b1);
  z-index: 100;
}

.topbar-logo {
  width: 32px; height: 32px;
  background: var(--accent);
  border-radius: var(--radius-sm);
  display: flex; align-items: center; justify-content: center;
  font-size: 18px; font-weight: 700; color: var(--s0);
  font-family: var(--mono);
  flex-shrink: 0;
}

.topbar-title {
  font-size: 15px; font-weight: 600; color: var(--t1);
  white-space: nowrap;
}

.topbar-version {
  font-size: 11px; color: var(--t3);
  font-family: var(--mono);
  background: var(--s3);
  padding: 2px 8px; border-radius: 10px;
}

.topbar-spacer { flex: 1; }

.topbar-status {
  display: flex; align-items: center; gap: 6px;
  font-size: 12px; color: var(--t2);
}

.status-dot {
  width: 8px; height: 8px; border-radius: 50%;
  background: var(--t3);
  transition: background 0.3s;
}

.status-dot.online { background: var(--accent); box-shadow: 0 0 6px rgba(0,212,170,0.4); }
.status-dot.offline { background: var(--danger); }

/* Sidebar toggle button (mobile) */
.sidebar-toggle {
  display: none;
  background: none; border: none; color: var(--t2);
  cursor: pointer; padding: 4px; font-size: 18px; line-height: 1;
}

/* Body area */
.body-area {
  display: flex; flex: 1; min-height: 0;
}

/* Sidebar */
.sidebar {
  width: var(--sidebar-w); min-width: var(--sidebar-w);
  background: var(--s1);
  border-right: 1px solid var(--b1);
  display: flex; flex-direction: column;
  overflow: hidden;
  transition: transform 0.25s ease, opacity 0.25s ease;
}

.sidebar-section {
  padding: 12px;
  border-bottom: 1px solid var(--b1);
}

.sidebar-section-title {
  font-size: 10px; font-weight: 600;
  text-transform: uppercase; letter-spacing: 0.8px;
  color: var(--t3); margin-bottom: 8px;
}

/* Server info card */
.server-info {
  display: flex; flex-direction: column; gap: 4px;
}

.server-info-row {
  display: flex; justify-content: space-between;
  font-size: 12px;
}

.server-info-label { color: var(--t3); }
.server-info-value { color: var(--t2); font-family: var(--mono); font-size: 11px; }

/* Device list */
.device-list {
  flex: 1; overflow-y: auto;
  padding: 4px 8px;
}

.group-member-list {
  max-height: 180px;
  overflow-y: auto;
}

.device-card {
  padding: 10px 12px;
  border-radius: var(--radius-sm);
  cursor: pointer;
  margin-bottom: 4px;
  border: 1px solid transparent;
  transition: all 0.15s;
}

.device-card:hover {
  background: var(--s2);
  border-color: var(--b1);
}

.device-card.active {
  background: var(--accent-subtle);
  border-color: var(--accent-dim);
}

.device-card-header {
  display: flex; align-items: center; gap: 8px;
}

.device-card-icon {
  width: 28px; height: 28px; border-radius: var(--radius-sm);
  background: var(--s3);
  display: flex; align-items: center; justify-content: center;
  font-size: 13px; flex-shrink: 0;
}

.device-card-info { flex: 1; min-width: 0; }

.device-card-name {
  font-size: 13px; font-weight: 500; color: var(--t1);
  white-space: nowrap; overflow: hidden; text-overflow: ellipsis;
}

.device-card-os {
  font-size: 11px; color: var(--t3);
}

.device-card-status {
  width: 6px; height: 6px; border-radius: 50%;
  background: var(--accent); flex-shrink: 0;
}

.device-card-status.offline { background: var(--t3); }

/* Main content */
.main-content {
  flex: 1; min-width: 0;
  display: flex; flex-direction: column;
  background: var(--s0);
  overflow: hidden;
}

/* Dashboard view */
.dashboard {
  padding: 24px;
  overflow-y: auto;
  flex: 1;
}

.dashboard-header {
  margin-bottom: 24px;
}

.dashboard-title {
  font-size: 20px; font-weight: 600; color: var(--t1);
  margin-bottom: 4px;
}

.dashboard-subtitle {
  font-size: 13px; color: var(--t3);
}

/* Stats grid */
.stats-grid {
  display: grid;
  grid-template-columns: repeat(auto-fit, minmax(200px, 1fr));
  gap: 12px;
  margin-bottom: 24px;
}

.stat-card {
  background: var(--s1);
  border: 1px solid var(--b1);
  border-radius: var(--radius);
  padding: 16px;
}

.stat-card-label {
  font-size: 11px; text-transform: uppercase; letter-spacing: 0.6px;
  color: var(--t3); margin-bottom: 8px;
}

.stat-card-value {
  font-size: 28px; font-weight: 700;
  font-family: var(--mono);
  color: var(--t1);
}

.stat-card-value.accent { color: var(--accent); }

/* Device detail view */
.device-detail {
  padding: 24px;
  overflow-y: auto;
  flex: 1;
}

.device-detail-header {
  display: flex; align-items: center; gap: 16px;
  margin-bottom: 20px;
  padding-bottom: 16px;
  border-bottom: 1px solid var(--b1);
}

.device-detail-icon {
  width: 48px; height: 48px;
  background: var(--s2);
  border: 1px solid var(--b1);
  border-radius: var(--radius);
  display: flex; align-items: center; justify-content: center;
  font-size: 22px;
}

.device-detail-info { flex: 1; }

.device-detail-name {
  font-size: 18px; font-weight: 600; color: var(--t1);
}

.device-detail-meta {
  font-size: 12px; color: var(--t3);
  font-family: var(--mono);
}

.detail-section {
  margin-bottom: 20px;
}

.detail-section-title {
  font-size: 12px; font-weight: 600;
  text-transform: uppercase; letter-spacing: 0.6px;
  color: var(--t3); margin-bottom: 10px;
}

.shell-grid {
  display: flex; flex-wrap: wrap; gap: 8px;
}

.shell-btn {
  display: inline-flex; align-items: center; gap: 6px;
  padding: 8px 16px;
  background: var(--s2);
  border: 1px solid var(--b1);
  border-radius: var(--radius-sm);
  color: var(--t1); font-size: 13px;
  cursor: pointer; transition: all 0.15s;
  font-family: var(--font);
}

.shell-btn:hover {
  background: var(--accent-subtle);
  border-color: var(--accent-dim);
  color: var(--accent);
}

.shell-btn-icon {
  font-size: 14px; opacity: 0.7;
}

/* Session list in detail */
.session-list { display: flex; flex-direction: column; gap: 6px; }

.session-card {
  display: flex; align-items: center; gap: 10px;
  padding: 10px 14px;
  background: var(--s2);
  border: 1px solid var(--b1);
  border-radius: var(--radius-sm);
  cursor: pointer;
  transition: all 0.15s;
}

.session-card:hover {
  border-color: var(--accent-dim);
  background: var(--accent-subtle);
}

.session-card-icon { font-size: 14px; color: var(--accent); }
.session-card-info { flex: 1; }
.session-card-title { font-size: 13px; font-weight: 500; color: var(--t1); }
.session-card-id { font-size: 11px; color: var(--t3); font-family: var(--mono); }
.session-card-arrow { color: var(--t3); font-size: 12px; }

.no-sessions {
  font-size: 12px; color: var(--t3);
  padding: 12px; text-align: center;
  background: var(--s2); border-radius: var(--radius-sm);
}

/* Terminal view */
.terminal-view {
  flex: 1; display: flex; flex-direction: column;
  min-height: 0;
}

.terminal-toolbar {
  display: flex; align-items: center; gap: 8px;
  padding: 8px 12px;
  background: var(--s1);
  border-bottom: 1px solid var(--b1);
  min-height: 40px;
}

.terminal-toolbar-title {
  font-size: 12px; color: var(--t2);
  font-family: var(--mono);
  flex: 1;
}

.btn {
  display: inline-flex; align-items: center; gap: 4px;
  padding: 5px 12px;
  border-radius: var(--radius-sm);
  border: 1px solid var(--b1);
  background: var(--s2);
  color: var(--t2); font-size: 12px;
  cursor: pointer; transition: all 0.15s;
  font-family: var(--font);
}

.btn:hover { background: var(--s3); color: var(--t1); border-color: var(--b2); }
.btn-danger:hover { background: rgba(255,71,87,0.15); color: var(--danger); border-color: var(--danger); }
.btn-accent { background: var(--accent-subtle); border-color: var(--accent-dim); color: var(--accent); }
.btn-accent:hover { background: var(--accent); color: var(--s0); }

.terminal-container {
  flex: 1; min-height: 0;
  padding: 0;
  background: var(--s0);
  position: relative;
}

#xterm-container {
  width: 100%; height: 100%;
  position: absolute; top: 0; left: 0;
}

/* xterm overrides */
.xterm { padding: 8px 4px 4px 8px; }
.xterm, .xterm .xterm-viewport { background-color: var(--s0) !important; }
.xterm .xterm-viewport::-webkit-scrollbar { width: 6px; }
.xterm .xterm-viewport::-webkit-scrollbar-track { background: transparent; }
.xterm .xterm-viewport::-webkit-scrollbar-thumb { background: rgba(255,255,255,0.08); border-radius: 3px; }
.xterm .xterm-viewport::-webkit-scrollbar-thumb:hover { background: rgba(255,255,255,0.15); }
.xterm .xterm-selection div { background: rgba(0,212,170,0.2) !important; }

/* Status bar */
.statusbar {
  height: var(--statusbar-h); min-height: var(--statusbar-h);
  display: flex; align-items: center;
  padding: 0 12px;
  background: var(--s1);
  border-top: 1px solid var(--b1);
  font-size: 11px; color: var(--t3);
  gap: 16px;
  z-index: 100;
}

.statusbar-item {
  display: flex; align-items: center; gap: 4px;
}

.statusbar-accent { color: var(--accent); }

/* Pairing overlay */
.pairing-overlay {
  position: fixed; inset: 0;
  background: var(--s0);
  display: flex; align-items: center; justify-content: center;
  z-index: 1000;
}

.pairing-card {
  background: var(--s1);
  border: 1px solid var(--b1);
  border-radius: var(--radius);
  padding: 32px;
  width: 380px;
  max-width: 92vw;
  text-align: center;
}

.pairing-logo {
  width: 56px; height: 56px;
  background: var(--accent);
  border-radius: 12px;
  display: flex; align-items: center; justify-content: center;
  font-size: 28px; font-weight: 700; color: var(--s0);
  font-family: var(--mono);
  margin: 0 auto 16px;
}

.pairing-title {
  font-size: 18px; font-weight: 600; color: var(--t1);
  margin-bottom: 4px;
}

.pairing-subtitle {
  font-size: 13px; color: var(--t3);
  margin-bottom: 16px;
}

.pairing-qr {
  width: 200px; height: 200px;
  margin: 0 auto 12px;
  background: var(--s0);
  border: 1px solid var(--b1);
  border-radius: var(--radius);
  display: flex; align-items: center; justify-content: center;
}

.pairing-qr img {
  width: 100%; height: 100%;
  object-fit: contain;
  padding: 10px;
}

.pairing-meta {
  font-size: 11px; color: var(--t3);
  margin-bottom: 10px;
  font-family: var(--mono);
  word-break: break-all;
}

.pairing-status {
  font-size: 12px; color: var(--t2);
  margin-bottom: 14px;
  min-height: 18px;
}

.pairing-error {
  color: var(--danger);
  font-size: 12px;
  margin-top: 8px;
  min-height: 16px;
}

.pairing-btn {
  width: 100%;
  padding: 10px 0;
  background: var(--accent);
  border: none; border-radius: var(--radius-sm);
  color: var(--s0); font-size: 14px; font-weight: 600;
  cursor: pointer; transition: background 0.2s;
  font-family: var(--font);
}

.pairing-btn:hover { background: var(--accent-dim); }
.pairing-btn:disabled { opacity: 0.6; cursor: not-allowed; }

/* Hidden view utility */
.view-hidden { display: none !important; }

/* Back button */
.back-btn {
  display: inline-flex; align-items: center; gap: 4px;
  background: none; border: none;
  color: var(--t3); font-size: 12px;
  cursor: pointer; padding: 4px 0;
  margin-bottom: 12px;
  font-family: var(--font);
  transition: color 0.15s;
}

.back-btn:hover { color: var(--accent); }

/* Empty state */
.empty-state {
  display: flex; flex-direction: column;
  align-items: center; justify-content: center;
  height: 100%; color: var(--t3);
  text-align: center; padding: 40px;
}

.empty-state-icon {
  font-size: 40px; margin-bottom: 16px;
  opacity: 0.5;
}

.empty-state-text {
  font-size: 14px; margin-bottom: 4px; color: var(--t2);
}

.empty-state-hint {
  font-size: 12px;
}

/* Loading spinner */
.spinner {
  width: 20px; height: 20px;
  border: 2px solid var(--b1);
  border-top-color: var(--accent);
  border-radius: 50%;
  animation: spin 0.8s linear infinite;
}

@keyframes spin { to { transform: rotate(360deg); } }

/* Responsive */
@media (max-width: 768px) {
  :root { --sidebar-w: 260px; }
  .sidebar-toggle { display: block; }
  .sidebar {
    position: fixed; top: var(--topbar-h); bottom: var(--statusbar-h);
    left: 0; z-index: 50;
    transform: translateX(-100%);
  }
  .sidebar.open {
    transform: translateX(0);
    box-shadow: 4px 0 24px rgba(0,0,0,0.5);
  }
  .dashboard { padding: 16px; }
  .stats-grid { grid-template-columns: 1fr; }
}
</style>
</head>
<body>
<div id=""app"">
  <!-- Pairing overlay -->
  <div id=""pairingOverlay"" class=""pairing-overlay view-hidden"">
    <div class=""pairing-card"">
      <div class=""pairing-logo"">❯</div>
      <div class=""pairing-title"">PhoneShell</div>
      <div id=""pairingSubtitle"" class=""pairing-subtitle"">请使用手机客户端扫码</div>
      <div class=""pairing-qr"">
        <img id=""pairingQr"" alt=""QR Code"" />
      </div>
      <div id=""pairingMeta"" class=""pairing-meta""></div>
      <div id=""pairingStatus"" class=""pairing-status""></div>
      <div id=""pairingError"" class=""pairing-error""></div>
    </div>
  </div>

  <!-- Top bar -->
  <div class=""topbar"">
    <button class=""sidebar-toggle"" onclick=""toggleSidebar()"">☰</button>
    <div class=""topbar-logo"">❯</div>
    <div class=""topbar-title"">PhoneShell</div>
    <div class=""topbar-version"">headless</div>
    <div class=""topbar-spacer""></div>
    <div class=""topbar-status"">
      <div id=""connDot"" class=""status-dot""></div>
      <span id=""connText"">Connecting…</span>
    </div>
  </div>

  <!-- Body -->
  <div class=""body-area"">
    <!-- Sidebar -->
    <div id=""sidebar"" class=""sidebar"">
      <div class=""sidebar-section"">
        <div class=""sidebar-section-title"">Server</div>
        <div class=""server-info"">
          <div class=""server-info-row"">
            <span class=""server-info-label"">Uptime</span>
            <span id=""srvUptime"" class=""server-info-value"">—</span>
          </div>
          <div class=""server-info-row"">
            <span class=""server-info-label"">Clients</span>
            <span id=""srvClients"" class=""server-info-value"">—</span>
          </div>
          <div class=""server-info-row"">
            <span class=""server-info-label"">Devices</span>
            <span id=""srvDevices"" class=""server-info-value"">—</span>
          </div>
        </div>
      </div>
      <div class=""sidebar-section"">
        <div class=""sidebar-section-title"">Group</div>
        <div class=""server-info"">
          <div class=""server-info-row"">
            <span class=""server-info-label"">Group ID</span>
            <span id=""groupIdValue"" class=""server-info-value"">—</span>
          </div>
          <div class=""server-info-row"">
            <span class=""server-info-label"">Mobile</span>
            <span id=""groupMobileValue"" class=""server-info-value"">—</span>
          </div>
        </div>
      </div>
      <div class=""sidebar-section"">
        <div class=""sidebar-section-title"">Group Members</div>
        <div id=""groupMemberList"" class=""device-list group-member-list""></div>
      </div>
      <div class=""sidebar-section"" style=""flex:1;display:flex;flex-direction:column;min-height:0;border-bottom:none;"">
        <div class=""sidebar-section-title"">Devices</div>
        <div id=""deviceList"" class=""device-list""></div>
      </div>
    </div>

    <!-- Main -->
    <div class=""main-content"">
      <!-- Dashboard -->
      <div id=""viewDashboard"" class=""dashboard"">
        <div class=""dashboard-header"">
          <div class=""dashboard-title"">Dashboard</div>
          <div class=""dashboard-subtitle"">PhoneShell Command Center — Headless Server</div>
        </div>
        <div class=""stats-grid"">
          <div class=""stat-card"">
            <div class=""stat-card-label"">Uptime</div>
            <div id=""statUptime"" class=""stat-card-value"">—</div>
          </div>
          <div class=""stat-card"">
            <div class=""stat-card-label"">Connected Clients</div>
            <div id=""statClients"" class=""stat-card-value accent"">0</div>
          </div>
          <div class=""stat-card"">
            <div class=""stat-card-label"">Registered Devices</div>
            <div id=""statDevices"" class=""stat-card-value accent"">0</div>
          </div>
          <div class=""stat-card"">
            <div class=""stat-card-label"">Active Sessions</div>
            <div id=""statSessions"" class=""stat-card-value accent"">0</div>
          </div>
        </div>
        <div class=""detail-section"">
          <div class=""detail-section-title"">Connected Devices</div>
          <div id=""dashDeviceCards""></div>
          <div id=""dashNoDevices"" class=""no-sessions"">No devices connected</div>
        </div>
        <div class=""detail-section"">
          <div class=""detail-section-title"">Group Members</div>
          <div id=""dashGroupCards""></div>
          <div id=""dashNoGroupMembers"" class=""no-sessions"">No group members</div>
        </div>
      </div>

      <!-- Device detail -->
      <div id=""viewDevice"" class=""device-detail view-hidden"">
        <button class=""back-btn"" onclick=""showDashboard()"">← Back to Dashboard</button>
        <div class=""device-detail-header"">
          <div id=""detailIcon"" class=""device-detail-icon"">🖥</div>
          <div class=""device-detail-info"">
            <div id=""detailName"" class=""device-detail-name""></div>
            <div id=""detailMeta"" class=""device-detail-meta""></div>
          </div>
        </div>
        <div class=""detail-section"">
          <div class=""detail-section-title"">Available Shells</div>
          <div id=""detailShells"" class=""shell-grid""></div>
        </div>
        <div class=""detail-section"">
          <div class=""detail-section-title"">Active Sessions</div>
          <div id=""detailSessions"" class=""session-list""></div>
          <div id=""detailNoSessions"" class=""no-sessions"">No active sessions. Open a shell above to start.</div>
        </div>
      </div>

      <!-- Terminal -->
      <div id=""viewTerminal"" class=""terminal-view view-hidden"">
        <div class=""terminal-toolbar"">
          <button class=""btn"" onclick=""closeTerminal()"">← Back</button>
          <span id=""termTitle"" class=""terminal-toolbar-title""></span>
          <button class=""btn btn-danger"" onclick=""closeTerminalSession()"">Close Session</button>
        </div>
        <div class=""terminal-container"">
          <div id=""xterm-container""></div>
        </div>
      </div>
    </div>
  </div>

  <!-- Status bar -->
  <div class=""statusbar"">
    <div class=""statusbar-item"">
      <span class=""statusbar-accent"">❯</span>
      <span>PhoneShell</span>
    </div>
    <div class=""statusbar-item"" id=""sbConnection"">Disconnected</div>
    <div style=""flex:1""></div>
    <div class=""statusbar-item"" id=""sbInfo""></div>
  </div>
</div>

<script src=""/panel/xterm.min.js""></script>
<script src=""/panel/addon-fit.min.js""></script>
<script>
(function() {
  'use strict';

  // --- State ---
  var authToken = '';
  var pairingInfo = null;
  var panelLoginRequestId = '';
  var panelLoginTimer = null;
  var pairingPollTimer = null;
  var loginQrPayload = '';
  var groupMembers = [];
  var groupInfo = null;
  var ws = null;
  var wsConnected = false;
  var devices = [];
  var activeDeviceId = null;
  var activeSessions = [];
  var activeTermSession = null;
  var activeTermDevice = null;
  var term = null;
  var fitAddon = null;
  var currentView = 'dashboard'; // dashboard | device | terminal
  var statusPollTimer = null;
  var groupPollTimer = null;
  var serverStatus = null;
  var historyPageChars = 20000;
  var historyLoading = false;
  var historyComplete = false;
  var historyBeforeSeq = 0;
  var historyChunks = [];
  var pendingOutput = '';

  // --- Pairing / Login ---

  function showPairing(info) {
    var el = document.getElementById('pairingOverlay');
    el.classList.remove('view-hidden');
    setPairingError('');
    if (info) {
      pairingInfo = info;
      updatePairingMeta(info);
    } else {
      document.getElementById('pairingMeta').textContent = '';
    }
  }

  function hidePairing() {
    document.getElementById('pairingOverlay').classList.add('view-hidden');
  }

  function updatePairingMeta(info) {
    var meta = [];
    if (info.groupId) meta.push('Group: ' + info.groupId);
    if (info.serverUrl) meta.push('Server: ' + info.serverUrl);
    document.getElementById('pairingMeta').textContent = meta.join(' · ');
  }

  function setPairingStatus(text) {
    document.getElementById('pairingStatus').textContent = text || '';
  }

  function setPairingError(text) {
    document.getElementById('pairingError').textContent = text || '';
  }

  function setPairingSubtitle(text) {
    var el = document.getElementById('pairingSubtitle');
    if (el) el.textContent = text || '';
  }

  function showBindQr(info) {
    setPairingSubtitle('请使用手机客户端扫码绑定');
    setPairingStatus('等待手机扫码加入群组');
    var qr = document.getElementById('pairingQr');
    qr.src = '/api/panel/qr.png?ts=' + Date.now();
  }

  function showLoginQr(payload) {
    setPairingSubtitle('请使用绑定的手机扫描二维码');
    setPairingStatus('网站已绑定手机，请使用已绑定手机扫码登录');
    var qr = document.getElementById('pairingQr');
    qr.src = '/api/panel/login/qr.png?payload=' + encodeURIComponent(payload) + '&ts=' + Date.now();
  }

  // Fetch pairing info, then decide which QR to show
  function refreshPairingInfo() {
    return fetch('/api/panel/pairing')
      .then(function(r) { return r.json(); })
      .then(function(info) {
        if (!info.requiresAuth) {
          // No auth required — skip pairing
          hidePairing();
          stopPairingPolling();
          init();
          return;
        }
        showPairing(info);
        if (!info.hasGroup) {
          setPairingStatus('服务器未初始化群组');
          return;
        }
        if (!info.hasBoundMobile) {
          // Show bind QR + poll for binding
          showBindQr(info);
          // Pre-create a login session so the first bind can auto-approve panel access.
          startLoginFlow();
          startPairingPolling();
        } else {
          // Already bound — auto-start login flow
          stopPairingPolling();
          startLoginFlow();
        }
      })
      .catch(function() {
        showPairing(null);
        setPairingStatus('无法连接服务器');
        setPairingError('请检查网络或服务状态');
      });
  }

  // Start the login flow: create a login session, show login QR
  function startLoginFlow() {
    if (panelLoginRequestId) return;
    setPairingError('');
    setPairingStatus('正在创建登录会话…');
    fetch('/api/panel/login/start')
      .then(function(r) { return r.json(); })
      .then(function(data) {
        panelLoginRequestId = data.requestId || '';
        loginQrPayload = data.loginQrPayload || '';
        if (loginQrPayload) {
          showLoginQr(loginQrPayload);
        }
        applyLoginStatus(data);
        startPanelLoginPolling();
      })
      .catch(function() {
        setPairingError('登录请求失败，请稍后重试。');
      });
  }

  function startPairingPolling() {
    if (pairingPollTimer) return;
    pairingPollTimer = setInterval(function() {
      fetch('/api/panel/pairing')
        .then(function(r) { return r.json(); })
        .then(function(info) {
          if (!info.requiresAuth) {
            hidePairing();
            stopPairingPolling();
            init();
            return;
          }
          pairingInfo = info;
          if (info.hasBoundMobile) {
            // Mobile just bound — switch to login flow
            stopPairingPolling();
            startLoginFlow();
          }
        })
        .catch(function() {});
    }, 4000);
  }

  function stopPairingPolling() {
    if (pairingPollTimer) {
      clearInterval(pairingPollTimer);
      pairingPollTimer = null;
    }
  }

  function startPanelLoginPolling() {
    if (panelLoginTimer) return;
    panelLoginTimer = setInterval(pollPanelLoginStatus, 2000);
  }

  function stopPanelLoginPolling() {
    if (panelLoginTimer) {
      clearInterval(panelLoginTimer);
      panelLoginTimer = null;
    }
  }

  function pollPanelLoginStatus() {
    if (!panelLoginRequestId) return;
    fetch('/api/panel/login/status/' + encodeURIComponent(panelLoginRequestId))
      .then(function(r) { return r.json(); })
      .then(function(data) {
        applyLoginStatus(data);
      })
      .catch(function() {});
  }

  function applyLoginStatus(data) {
    if (!data || !data.status) return;
    if (data.status === 'approved') {
      authToken = data.token || '';
      panelLoginRequestId = '';
      loginQrPayload = '';
      stopPanelLoginPolling();
      stopPairingPolling();
      hidePairing();
      init();
      return;
    }
    if (data.status === 'rejected') {
      panelLoginRequestId = '';
      loginQrPayload = '';
      stopPanelLoginPolling();
      setPairingError('手机端已拒绝登录请求');
      // Auto-restart login flow after a brief delay
      setTimeout(startLoginFlow, 3000);
      return;
    }
    if (data.status === 'expired') {
      panelLoginRequestId = '';
      loginQrPayload = '';
      stopPanelLoginPolling();
      setPairingError('登录会话已过期，正在刷新…');
      // Auto-restart with a new login session
      setTimeout(startLoginFlow, 1000);
      return;
    }
    if (data.status === 'awaiting_scan') {
      setPairingStatus('请使用绑定的手机扫描二维码');
      // Update QR if payload is available
      if (data.loginQrPayload && data.loginQrPayload !== loginQrPayload) {
        loginQrPayload = data.loginQrPayload;
        showLoginQr(loginQrPayload);
      }
    } else if (data.status === 'awaiting_mobile') {
      setPairingStatus('等待手机扫码加入群组');
    } else if (data.status === 'awaiting_approval') {
      setPairingStatus('请在手机端确认登录');
    }
  }

  function resetToPairing(message) {
    authToken = '';
    panelLoginRequestId = '';
    loginQrPayload = '';
    stopPanelLoginPolling();
    stopPairingPolling();
    disconnectWs();
    if (statusPollTimer) {
      clearInterval(statusPollTimer);
      statusPollTimer = null;
    }
    if (groupPollTimer) {
      clearInterval(groupPollTimer);
      groupPollTimer = null;
    }
    showPairing(pairingInfo);
    if (message) setPairingError(message);
    refreshPairingInfo();
  }

  // --- API ---
  function fetchApi(path) {
    var headers = { 'Accept': 'application/json' };
    if (authToken) headers['X-PhoneShell-Token'] = authToken;
    return fetch(path, { headers: headers })
      .then(function(r) {
        if (r.status === 401) {
          resetToPairing('认证已失效，请重新扫码');
          throw new Error('unauthorized');
        }
        if (!r.ok) throw new Error('HTTP ' + r.status);
        return r.json();
      });
  }

  // --- WebSocket ---
  function connectWs() {
    if (!canUseWs()) return;
    if (ws && (ws.readyState === 0 || ws.readyState === 1)) return;

    var proto = location.protocol === 'https:' ? 'wss:' : 'ws:';
    var url = proto + '//' + location.host + '/ws/';
    if (authToken) {
      url += '?token=' + encodeURIComponent(authToken);
    }

    try {
      ws = new WebSocket(url);
    } catch(e) {
      setConnState(false);
      scheduleReconnect();
      return;
    }

    ws.onopen = function() {
      setConnState(true);
      // Request device list
      wsSend({ type: 'device.list.request' });
    };

    ws.onclose = function() {
      setConnState(false);
      scheduleReconnect();
    };

    ws.onerror = function() {
      setConnState(false);
    };

    ws.onmessage = function(ev) {
      try {
        var msg = JSON.parse(ev.data);
        handleWsMessage(msg);
      } catch(e) {}
    };
  }

  function disconnectWs() {
    if (!ws) return;
    try {
      ws.close();
    } catch(e) {}
    ws = null;
    wsConnected = false;
  }

  function wsSend(obj) {
    if (ws && ws.readyState === 1) {
      ws.send(JSON.stringify(obj));
    }
  }

  var reconnectTimeout = null;
  function scheduleReconnect() {
    if (reconnectTimeout) return;
    if (!canUseWs()) return;
    reconnectTimeout = setTimeout(function() {
      reconnectTimeout = null;
      connectWs();
    }, 3000);
  }

  function canUseWs() {
    if (authToken) return true;
    if (pairingInfo && pairingInfo.requiresAuth) return false;
    return true;
  }

  function setConnState(connected) {
    wsConnected = connected;
    var dot = document.getElementById('connDot');
    var text = document.getElementById('connText');
    var sb = document.getElementById('sbConnection');
    if (connected) {
      dot.className = 'status-dot online';
      text.textContent = 'Connected';
      sb.textContent = 'Connected';
    } else {
      dot.className = 'status-dot offline';
      text.textContent = 'Disconnected';
      sb.textContent = 'Disconnected';
    }
  }

  function resetHistoryState() {
    historyLoading = false;
    historyComplete = false;
    historyBeforeSeq = 0;
    historyChunks = [];
    pendingOutput = '';
  }

  function requestHistoryPage() {
    if (!term || historyLoading || historyComplete) return;
    if (!activeTermSession || !activeTermDevice) return;
    historyLoading = true;
    wsSend({
      type: 'terminal.history.request',
      deviceId: activeTermDevice,
      sessionId: activeTermSession,
      beforeSeq: historyBeforeSeq,
      maxChars: historyPageChars
    });
  }

  function applyHistoryBuffer() {
    if (!term) return;
    var history = historyChunks.join('');
    var merged = history + pendingOutput;
    pendingOutput = '';
    term.reset();
    if (merged) {
      term.write(merged);
    }
  }

  function handleHistoryResponse(msg) {
    if (msg.deviceId !== activeTermDevice || msg.sessionId !== activeTermSession) return;
    historyLoading = false;
    if (msg.data) {
      historyChunks.unshift(msg.data);
    }
    if (msg.hasMore) {
      historyBeforeSeq = msg.nextBeforeSeq || 0;
      requestHistoryPage();
      return;
    }
    historyComplete = true;
    applyHistoryBuffer();
  }

  function handleWsMessage(msg) {
    switch(msg.type) {
      case 'device.list':
        devices = msg.devices || [];
        renderDeviceList();
        renderDashboardDevices();
        break;
      case 'session.list':
        if (msg.deviceId === activeDeviceId) {
          activeSessions = msg.sessions || [];
          renderSessions();
        }
        break;
      case 'terminal.opened':
        if (activeTermDevice === msg.deviceId) {
          activeTermSession = msg.sessionId;
          document.getElementById('termTitle').textContent =
            getDeviceName(msg.deviceId) + ' — ' + msg.sessionId + ' (' + msg.cols + '×' + msg.rows + ')';
          if (term) {
            term.resize(msg.cols, msg.rows);
            term.focus();
          }
          resetHistoryState();
          requestHistoryPage();
        }
        break;
      case 'terminal.output':
        if (term && msg.sessionId === activeTermSession) {
          if (!historyComplete) {
            pendingOutput += msg.data || '';
          } else {
            term.write(msg.data);
          }
        }
        break;
      case 'terminal.history.response':
        handleHistoryResponse(msg);
        break;
      case 'terminal.closed':
        if (msg.sessionId === activeTermSession) {
          if (term) {
            term.write('\r\n\x1b[90m[Session closed]\x1b[0m\r\n');
          }
          activeTermSession = null;
          // Refresh sessions
          if (activeDeviceId) {
            wsSend({ type: 'session.list.request', deviceId: activeDeviceId });
          }
        }
        break;
      case 'error':
        console.error('Server error:', msg.code, msg.message);
        if (term && currentView === 'terminal') {
          term.write('\r\n\x1b[31m[Error: ' + escapeHtml(msg.message) + ']\x1b[0m\r\n');
        }
        break;
    }
  }

  // --- Status polling ---
  function pollStatus() {
    fetchApi('/api/status')
      .then(function(data) {
        serverStatus = data;
        renderStatus();
      })
      .catch(function() {});
  }

  function pollGroup() {
    fetchApi('/api/group')
      .then(function(data) {
        groupInfo = data || null;
        groupMembers = (data && data.members) ? data.members : [];
        renderGroupInfo();
        renderGroupMembers();
        renderDashboardGroupMembers();
      })
      .catch(function() {});
  }

  function renderStatus() {
    if (!serverStatus) return;
    var uptime = formatUptime(serverStatus.uptimeSeconds);
    document.getElementById('srvUptime').textContent = uptime;
    document.getElementById('srvClients').textContent = serverStatus.connectedClientCount;
    document.getElementById('srvDevices').textContent = serverStatus.registeredDeviceCount;
    document.getElementById('statUptime').textContent = uptime;
    document.getElementById('statClients').textContent = serverStatus.connectedClientCount;
    document.getElementById('statDevices').textContent = serverStatus.registeredDeviceCount;

    // Count active sessions from device detail if available
    var sessionCount = activeSessions ? activeSessions.length : 0;
    document.getElementById('statSessions').textContent = sessionCount;

    document.getElementById('sbInfo').textContent =
      serverStatus.registeredDeviceCount + ' device(s), ' +
      serverStatus.connectedClientCount + ' client(s)';
  }

  function renderGroupInfo() {
    var groupIdEl = document.getElementById('groupIdValue');
    var mobileEl = document.getElementById('groupMobileValue');
    if (!groupInfo) {
      groupIdEl.textContent = '—';
      mobileEl.textContent = '—';
      return;
    }
    groupIdEl.textContent = groupInfo.groupId || '—';
    if (!groupInfo.boundMobileId) {
      mobileEl.textContent = 'Not bound';
    } else {
      var mobileOnline = groupMembers.some(function(m) {
        return m.deviceId === groupInfo.boundMobileId && m.isOnline;
      });
      mobileEl.textContent = mobileOnline ? 'Online' : 'Offline';
    }
  }

  function formatUptime(seconds) {
    if (!seconds && seconds !== 0) return '—';
    var d = Math.floor(seconds / 86400);
    var h = Math.floor((seconds % 86400) / 3600);
    var m = Math.floor((seconds % 3600) / 60);
    var s = seconds % 60;
    if (d > 0) return d + 'd ' + h + 'h ' + m + 'm';
    if (h > 0) return h + 'h ' + m + 'm ' + s + 's';
    if (m > 0) return m + 'm ' + s + 's';
    return s + 's';
  }

  // --- Render ---
  function renderDeviceList() {
    var container = document.getElementById('deviceList');
    if (devices.length === 0) {
      container.innerHTML = '<div class=""no-sessions"">No devices</div>';
      return;
    }
    container.innerHTML = devices.map(function(d) {
      var isActive = d.deviceId === activeDeviceId;
      var icon = getDeviceIcon(d.os);
      return '<div class=""device-card' + (isActive ? ' active' : '') + '"" onclick=""selectDevice(\'' + escJs(d.deviceId) + '\')"" title=""' + escHtmlAttr(d.deviceId) + '"">' +
        '<div class=""device-card-header"">' +
          '<div class=""device-card-icon"">' + icon + '</div>' +
          '<div class=""device-card-info"">' +
            '<div class=""device-card-name"">' + escapeHtml(d.displayName) + '</div>' +
            '<div class=""device-card-os"">' + escapeHtml(d.os) + '</div>' +
          '</div>' +
          '<div class=""device-card-status""></div>' +
        '</div>' +
      '</div>';
    }).join('');
  }

  function renderGroupMembers() {
    var container = document.getElementById('groupMemberList');
    if (!container) return;
    if (!groupMembers || groupMembers.length === 0) {
      container.innerHTML = '<div class=""no-sessions"">No members</div>';
      return;
    }
    container.innerHTML = groupMembers.map(function(m) {
      var icon = getDeviceIcon(m.os);
      var role = m.role || 'Member';
      var statusClass = m.isOnline ? '' : ' offline';
      var name = m.displayName || m.deviceId;
      return '<div class=""device-card"" title=""' + escHtmlAttr(m.deviceId) + '"">' +
        '<div class=""device-card-header"">' +
          '<div class=""device-card-icon"">' + icon + '</div>' +
          '<div class=""device-card-info"">' +
            '<div class=""device-card-name"">' + escapeHtml(name) + '</div>' +
            '<div class=""device-card-os"">' + escapeHtml(role) + ' · ' + escapeHtml(m.os || '') + '</div>' +
          '</div>' +
          '<div class=""device-card-status' + statusClass + '""></div>' +
        '</div>' +
      '</div>';
    }).join('');
  }

  function renderDashboardDevices() {
    var container = document.getElementById('dashDeviceCards');
    var noDevices = document.getElementById('dashNoDevices');
    if (devices.length === 0) {
      container.innerHTML = '';
      noDevices.style.display = '';
      return;
    }
    noDevices.style.display = 'none';
    container.innerHTML = devices.map(function(d) {
      var icon = getDeviceIcon(d.os);
      var shellCount = d.availableShells ? d.availableShells.length : 0;
      return '<div class=""session-card"" onclick=""selectDevice(\'' + escJs(d.deviceId) + '\')"" title=""' + escHtmlAttr(d.deviceId) + '"">' +
        '<span class=""session-card-icon"">' + icon + '</span>' +
        '<div class=""session-card-info"">' +
          '<div class=""session-card-title"">' + escapeHtml(d.displayName) + '</div>' +
          '<div class=""session-card-id"">' + escapeHtml(d.os) + ' · ' + shellCount + ' shell(s)</div>' +
        '</div>' +
        '<span class=""session-card-arrow"">→</span>' +
      '</div>';
    }).join('');
  }

  function renderDashboardGroupMembers() {
    var container = document.getElementById('dashGroupCards');
    var noMembers = document.getElementById('dashNoGroupMembers');
    if (!container || !noMembers) return;
    if (!groupMembers || groupMembers.length === 0) {
      container.innerHTML = '';
      noMembers.style.display = '';
      return;
    }
    noMembers.style.display = 'none';
    container.innerHTML = groupMembers.map(function(m) {
      var icon = getDeviceIcon(m.os);
      var role = m.role || 'Member';
      var status = m.isOnline ? 'Online' : 'Offline';
      var name = m.displayName || m.deviceId;
      return '<div class=""session-card"">' +
        '<span class=""session-card-icon"">' + icon + '</span>' +
        '<div class=""session-card-info"">' +
          '<div class=""session-card-title"">' + escapeHtml(name) + '</div>' +
          '<div class=""session-card-id"">' + escapeHtml(role) + ' · ' + escapeHtml(status) + '</div>' +
        '</div>' +
      '</div>';
    }).join('');
  }

  function renderSessions() {
    var container = document.getElementById('detailSessions');
    var noSessions = document.getElementById('detailNoSessions');
    if (!activeSessions || activeSessions.length === 0) {
      container.innerHTML = '';
      noSessions.style.display = '';
      return;
    }
    noSessions.style.display = 'none';
    container.innerHTML = activeSessions.map(function(s) {
      return '<div class=""session-card"" onclick=""openSession(\'' + escJs(s.sessionId) + '\')"" title=""' + escHtmlAttr(s.sessionId) + '"">' +
        '<span class=""session-card-icon"">⬤</span>' +
        '<div class=""session-card-info"">' +
          '<div class=""session-card-title"">' + escapeHtml(s.title || s.shellId) + '</div>' +
          '<div class=""session-card-id"">' + escapeHtml(s.sessionId) + '</div>' +
        '</div>' +
        '<span class=""session-card-arrow"">→</span>' +
      '</div>';
    }).join('');
  }

  // --- Views ---
  window.showDashboard = function() {
    currentView = 'dashboard';
    activeDeviceId = null;
    document.getElementById('viewDashboard').classList.remove('view-hidden');
    document.getElementById('viewDevice').classList.add('view-hidden');
    document.getElementById('viewTerminal').classList.add('view-hidden');
    renderDeviceList();
    closeSidebar();
    destroyTerm();
  };

  window.selectDevice = function(deviceId) {
    activeDeviceId = deviceId;
    currentView = 'device';
    var dev = devices.find(function(d) { return d.deviceId === deviceId; });
    if (!dev) return;

    document.getElementById('detailName').textContent = dev.displayName;
    document.getElementById('detailMeta').textContent = dev.os + ' — ' + dev.deviceId;
    document.getElementById('detailIcon').textContent = getDeviceIcon(dev.os);

    // Render shells
    var shellGrid = document.getElementById('detailShells');
    var shells = dev.availableShells || [];
    if (shells.length === 0) {
      shellGrid.innerHTML = '<div class=""no-sessions"">No shells available</div>';
    } else {
      shellGrid.innerHTML = shells.map(function(s) {
        return '<button class=""shell-btn"" onclick=""openShell(\'' + escJs(deviceId) + '\', \'' + escJs(s) + '\')"">' +
          '<span class=""shell-btn-icon"">❯</span> ' + escapeHtml(s) +
        '</button>';
      }).join('');
    }

    // Request sessions
    activeSessions = [];
    renderSessions();
    wsSend({ type: 'session.list.request', deviceId: deviceId });

    document.getElementById('viewDashboard').classList.add('view-hidden');
    document.getElementById('viewDevice').classList.remove('view-hidden');
    document.getElementById('viewTerminal').classList.add('view-hidden');
    renderDeviceList();
    closeSidebar();
    destroyTerm();
  };

  window.openShell = function(deviceId, shellId) {
    activeTermDevice = deviceId;
    activeTermSession = null;
    showTerminalView(deviceId, shellId);
    wsSend({ type: 'terminal.open', deviceId: deviceId, shellId: shellId });
  };

  window.openSession = function(sessionId) {
    activeTermDevice = activeDeviceId;
    activeTermSession = sessionId;
    var dev = devices.find(function(d) { return d.deviceId === activeDeviceId; });
    showTerminalView(activeDeviceId, '');
    document.getElementById('termTitle').textContent =
      (dev ? dev.displayName : activeDeviceId) + ' — ' + sessionId;
    // Subscribe to output by sending a resize (which sets subscription)
    if (term) {
      wsSend({
        type: 'terminal.resize',
        deviceId: activeDeviceId,
        sessionId: sessionId,
        cols: term.cols,
        rows: term.rows
      });
    }
    resetHistoryState();
    requestHistoryPage();
  };

  function showTerminalView(deviceId, shellId) {
    currentView = 'terminal';
    var dev = devices.find(function(d) { return d.deviceId === deviceId; });
    document.getElementById('termTitle').textContent =
      (dev ? dev.displayName : deviceId) + ' — Opening ' + (shellId || 'session') + '…';

    document.getElementById('viewDashboard').classList.add('view-hidden');
    document.getElementById('viewDevice').classList.add('view-hidden');
    document.getElementById('viewTerminal').classList.remove('view-hidden');
    closeSidebar();

    initTerm();
  }

  window.closeTerminal = function() {
    if (activeDeviceId) {
      selectDevice(activeDeviceId);
    } else {
      showDashboard();
    }
  };

  window.closeTerminalSession = function() {
    if (activeTermSession && activeTermDevice) {
      wsSend({
        type: 'terminal.close',
        deviceId: activeTermDevice,
        sessionId: activeTermSession
      });
    }
    closeTerminal();
  };

  // --- Terminal ---
  function initTerm() {
    destroyTerm();
    var container = document.getElementById('xterm-container');

    term = new Terminal({
      cursorBlink: true,
      cursorStyle: 'bar',
      cursorWidth: 2,
      fontSize: 14,
      fontFamily: ""'Cascadia Code', 'Cascadia Mono', 'Fira Code', 'JetBrains Mono', 'Consolas', monospace"",
      fontWeight: '400',
      fontWeightBold: '600',
      letterSpacing: 0.3,
      lineHeight: 1.25,
      scrollback: 1000000,
      theme: {
        background: '#0A0E14',
        foreground: '#CBD3E0',
        cursor: '#00D4AA',
        cursorAccent: '#0A0E14',
        selectionBackground: 'rgba(0, 212, 170, 0.2)',
        selectionForeground: '#FFFFFF',
        black: '#1A2029',
        red: '#FF4757',
        green: '#00D4AA',
        yellow: '#FFBE0B',
        blue: '#4DA6FF',
        magenta: '#C084FC',
        cyan: '#22D3EE',
        white: '#CBD3E0',
        brightBlack: '#3A4452',
        brightRed: '#FF6B7A',
        brightGreen: '#3DFFCF',
        brightYellow: '#FFD060',
        brightBlue: '#7CC4FF',
        brightMagenta: '#D8A9FF',
        brightCyan: '#5EE8F7',
        brightWhite: '#F0F4F8'
      }
    });

    fitAddon = new FitAddon.FitAddon();
    term.loadAddon(fitAddon);
    term.open(container);
    resetHistoryState();

    // Fit after a brief delay to ensure container is sized
    setTimeout(function() {
      fitAddon.fit();
      term.focus();
    }, 100);

    // Input handler
    term.onData(function(data) {
      if (activeTermSession && activeTermDevice) {
        wsSend({
          type: 'terminal.input',
          deviceId: activeTermDevice,
          sessionId: activeTermSession,
          data: data
        });
      }
    });

    // Resize handler
    term.onResize(function(size) {
      if (activeTermSession && activeTermDevice) {
        wsSend({
          type: 'terminal.resize',
          deviceId: activeTermDevice,
          sessionId: activeTermSession,
          cols: size.cols,
          rows: size.rows
        });
      }
    });

    // Observe container resize
    var ro = new ResizeObserver(function() {
      if (fitAddon && currentView === 'terminal') {
        try { fitAddon.fit(); } catch(e) {}
      }
    });
    ro.observe(container);
    term._panelResizeObserver = ro;
  }

  function destroyTerm() {
    if (term) {
      if (term._panelResizeObserver) {
        term._panelResizeObserver.disconnect();
        term._panelResizeObserver = null;
      }
      term.dispose();
      term = null;
      fitAddon = null;
    }
    var container = document.getElementById('xterm-container');
    if (container) container.innerHTML = '';
    activeTermSession = null;
    resetHistoryState();
  }

  // --- Sidebar ---
  window.toggleSidebar = function() {
    document.getElementById('sidebar').classList.toggle('open');
  };

  function closeSidebar() {
    document.getElementById('sidebar').classList.remove('open');
  }

  // --- Helpers ---
  function getDeviceName(deviceId) {
    var dev = devices.find(function(d) { return d.deviceId === deviceId; });
    return dev ? dev.displayName : deviceId;
  }

  function getDeviceIcon(os) {
    if (!os) return '🖥';
    var lower = os.toLowerCase();
    if (lower.indexOf('windows') >= 0 || lower.indexOf('win') >= 0) return '🪟';
    if (lower.indexOf('linux') >= 0) return '🐧';
    if (lower.indexOf('mac') >= 0 || lower.indexOf('darwin') >= 0) return '🍎';
    if (lower.indexOf('harmony') >= 0 || lower.indexOf('android') >= 0 || lower.indexOf('ios') >= 0) return '📱';
    return '🖥';
  }

  function escapeHtml(s) {
    if (!s) return '';
    return s.replace(/&/g,'&amp;').replace(/</g,'&lt;').replace(/>/g,'&gt;').replace(/""/g,'&quot;');
  }

  function escHtmlAttr(s) {
    return escapeHtml(s);
  }

  function escJs(s) {
    if (!s) return '';
    return s.replace(/\\/g, '\\\\').replace(/'/g, ""\\'"");
  }

  // --- Init ---
  function init() {
    connectWs();
    pollStatus();
    pollGroup();
    statusPollTimer = setInterval(pollStatus, 5000);
    groupPollTimer = setInterval(pollGroup, 5000);
  }

  // Boot: always require fresh scan (no localStorage persistence)
  function boot() {
    // Clean up any residual token from old code version that used localStorage
    try { localStorage.removeItem('phoneshell_panel_token'); } catch(e) {}
    // Token only lives in JS variable — every page load starts fresh
    authToken = '';
    refreshPairingInfo();
  }

  boot();

  // Handle browser back-forward cache (bfcache) — force re-auth when page is restored
  window.addEventListener('pageshow', function(event) {
    if (event.persisted) {
      resetToPairing('');
    }
  });
})();
</script>
</body>
</html>";
}

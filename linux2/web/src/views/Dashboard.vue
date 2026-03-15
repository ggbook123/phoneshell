<template>
  <div class="dashboard">
    <header>
      <h1>PhoneShell</h1>
      <div class="header-info">
        <span :class="['connection-dot', ws.connected.value ? 'online' : 'offline']"></span>
        <span>{{ ws.connected.value ? labels.connected : labels.reconnecting }}</span>
      </div>
    </header>

    <div class="main-content">
      <!-- Sidebar: Device & Session list -->
      <aside class="sidebar">
        <div class="sidebar-section">
          <h3>{{ labels.languageHeader }}</h3>
          <ul class="lang-list">
            <li :class="{ active: language === 'en' }" @click="setLanguage('en')">
              {{ labels.languageEnglish }}
            </li>
            <li :class="{ active: language === 'zh' }" @click="setLanguage('zh')">
              {{ labels.languageChinese }}
            </li>
          </ul>
        </div>

        <div class="sidebar-section">
          <h3>{{ labels.devices }}</h3>
          <ul class="device-list">
            <li v-for="device in devices" :key="device.deviceId"
                :class="{ active: selectedDeviceId === device.deviceId }"
                @click="selectDevice(device.deviceId)">
              <span class="device-name">
                {{ device.displayName }}
                <span v-if="device.role" :class="['mode-badge', `mode-${(device.role || '').toLowerCase()}`]">
                  {{ device.role }}
                </span>
              </span>
              <span class="device-os">{{ device.os }}</span>
            </li>
          </ul>
        </div>

        <div v-if="selectedDeviceId" class="sidebar-section">
          <h3>
            {{ labels.sessions }}
            <button class="btn-new" @click="openNewTerminal" :title="labels.newTerminalTitle">+</button>
          </h3>
          <ul class="session-list">
            <li v-for="session in sessions" :key="session.sessionId"
                :class="{ active: activeSessionId === session.sessionId }"
                @click="switchSession(session.sessionId)">
              <span class="session-title">{{ session.title || session.sessionId }}</span>
              <button class="btn-close" @click.stop="closeSession(session.sessionId)" :title="labels.closeSession">&times;</button>
            </li>
          </ul>
          <p v-if="sessions.length === 0" class="empty-hint">{{ labels.noActiveSessions }}</p>
        </div>

        <div v-if="selectedDevice" class="sidebar-section">
          <h3>{{ labels.newTerminal }}</h3>
          <ul class="shell-list">
            <li v-for="shell in selectedDevice.availableShells" :key="shell"
                @click="openTerminalWithShell(shell)">
              {{ shell }}
            </li>
          </ul>
        </div>
      </aside>

      <!-- Terminal area -->
      <div class="terminal-area">
        <div v-if="activeSessionId && selectedDeviceId" class="terminal-toolbar">
          <button class="btn-compact" @click="toggleCompact">
            {{ isCompact ? labels.desktopMode : labels.compactMode }}
          </button>
        </div>
        <Terminal
          v-if="activeSessionId && selectedDeviceId"
          :key="activeSessionId"
          :session-id="activeSessionId"
          :device-id="selectedDeviceId"
          :ws="ws"
          :compact="isCompact"
        />
        <div v-else class="terminal-placeholder">
          <p>{{ labels.selectDeviceHint }}</p>
        </div>
      </div>
    </div>
  </div>
</template>

<script setup lang="ts">
import { ref, onMounted, computed } from 'vue';
import { useWebSocket } from '../composables/useWebSocket';
import { useLanguage } from '../composables/useLanguage';
import Terminal from './Terminal.vue';

const props = defineProps<{ token: string }>();

interface DeviceInfo {
  deviceId: string;
  displayName: string;
  os: string;
  isOnline: boolean;
  availableShells: string[];
  role?: string; // "Server", "Member", "Mobile"
}

interface SessionInfo {
  sessionId: string;
  shellId: string;
  title: string;
}

const ws = useWebSocket(props.token);
const devices = ref<DeviceInfo[]>([]);
const sessions = ref<SessionInfo[]>([]);
const selectedDeviceId = ref<string | null>(null);
const activeSessionId = ref<string | null>(null);
const isCompact = ref(false);

const { language, setLanguage } = useLanguage();
const labels = computed(() => language.value === 'zh'
  ? {
      connected: '已连接',
      reconnecting: '重新连接中...',
      languageHeader: '语言 / Language',
      languageEnglish: 'English',
      languageChinese: '中文',
      devices: '设备',
      sessions: '会话',
      newTerminal: '新终端',
      newTerminalTitle: '新终端',
      closeSession: '关闭会话',
      compactMode: '手机适配',
      desktopMode: '电脑适配',
      noActiveSessions: '暂无会话',
      selectDeviceHint: '选择设备并打开终端会话',
    }
  : {
      connected: 'Connected',
      reconnecting: 'Reconnecting...',
      languageHeader: 'Language / 语言',
      languageEnglish: 'English',
      languageChinese: '中文',
      devices: 'Devices',
      sessions: 'Sessions',
      newTerminal: 'New Terminal',
      newTerminalTitle: 'New terminal',
      closeSession: 'Close session',
      compactMode: 'Compact',
      desktopMode: 'Desktop',
      noActiveSessions: 'No active sessions',
      selectDeviceHint: 'Select a device and open a terminal session',
    });

const selectedDevice = computed(() =>
  devices.value.find(d => d.deviceId === selectedDeviceId.value)
);

onMounted(async () => {
  // Fetch devices via REST
  try {
    const res = await fetch('/api/devices', {
      headers: { 'Authorization': `Bearer ${props.token}` },
    });
    devices.value = await res.json();
    // Fetch group info to get member roles
    try {
      const groupRes = await fetch('/api/group', {
        headers: { 'Authorization': `Bearer ${props.token}` },
      });
      if (groupRes.ok) {
        const groupData = await groupRes.json();
        const members: Array<{deviceId: string; role: string}> = groupData.members || [];
        for (const member of members) {
          const device = devices.value.find(d => d.deviceId === member.deviceId);
          if (device) device.role = member.role;
        }
      }
    } catch {}
    if (devices.value.length > 0) {
      selectDevice(devices.value[0].deviceId);
    }
  } catch {}

  // WebSocket message handlers
  ws.on('device.list', (msg: any) => {
    devices.value = msg.devices;
  });

  ws.on('group.member.list', (msg: any) => {
    const members: Array<{deviceId: string; role: string}> = msg.members || [];
    for (const member of members) {
      const device = devices.value.find(d => d.deviceId === member.deviceId);
      if (device) device.role = member.role;
    }
  });

  ws.on('group.member.joined', (msg: any) => {
    const member = msg.member;
    if (member) {
      const device = devices.value.find(d => d.deviceId === member.deviceId);
      if (device) device.role = member.role;
    }
  });

  ws.on('session.list', (msg: any) => {
    if (msg.deviceId === selectedDeviceId.value) {
      sessions.value = msg.sessions;
    }
  });

  ws.on('terminal.opened', (msg: any) => {
    activeSessionId.value = msg.sessionId;
    // Refresh session list
    if (selectedDeviceId.value) {
      ws.send({ type: 'session.list.request', deviceId: selectedDeviceId.value });
    }
  });

  ws.on('terminal.closed', (msg: any) => {
    if (activeSessionId.value === msg.sessionId) {
      activeSessionId.value = null;
    }
    sessions.value = sessions.value.filter(s => s.sessionId !== msg.sessionId);
  });
});

function selectDevice(deviceId: string) {
  selectedDeviceId.value = deviceId;
  activeSessionId.value = null;
  sessions.value = [];
  // Request session list via WS
  ws.send({ type: 'session.list.request', deviceId });
}

function switchSession(sessionId: string) {
  activeSessionId.value = sessionId;
}

function openNewTerminal() {
  if (!selectedDeviceId.value) return;
  const device = devices.value.find(d => d.deviceId === selectedDeviceId.value);
  const defaultShell = device?.availableShells?.[0] || '';
  openTerminalWithShell(defaultShell);
}

function openTerminalWithShell(shellId: string) {
  if (!selectedDeviceId.value) return;
  ws.send({
    type: 'terminal.open',
    deviceId: selectedDeviceId.value,
    shellId,
  });
}

function closeSession(sessionId: string) {
  if (!selectedDeviceId.value) return;
  ws.send({
    type: 'terminal.close',
    deviceId: selectedDeviceId.value,
    sessionId,
  });
}

function toggleCompact() {
  isCompact.value = !isCompact.value;
}
</script>

<style scoped>
.dashboard { display: flex; flex-direction: column; height: 100vh; }
header {
  display: flex; align-items: center; justify-content: space-between;
  padding: 8px 16px; background: #0f3460; border-bottom: 1px solid #1a1a4e;
}
header h1 { font-size: 1.2rem; color: #00d4ff; }
.header-info { display: flex; align-items: center; gap: 8px; font-size: 0.85rem; }
.connection-dot {
  width: 8px; height: 8px; border-radius: 50%; display: inline-block;
}
.connection-dot.online { background: #2ecc71; }
.connection-dot.offline { background: #e74c3c; }
.main-content { display: flex; flex: 1; overflow: hidden; }
.sidebar {
  width: 220px; min-width: 220px; background: #16213e;
  border-right: 1px solid #1a1a4e; overflow-y: auto; padding: 8px 0;
}
.sidebar-section { padding: 8px 12px; }
.sidebar-section h3 {
  font-size: 0.8rem; text-transform: uppercase; color: #888;
  margin-bottom: 8px; display: flex; align-items: center; justify-content: space-between;
}
.btn-new {
  background: #00d4ff; color: #000; border: none; border-radius: 4px;
  width: 22px; height: 22px; cursor: pointer; font-size: 1rem; line-height: 1;
}
.device-list, .session-list, .shell-list, .lang-list { list-style: none; }
.device-list li, .session-list li, .shell-list li, .lang-list li {
  padding: 6px 8px; border-radius: 4px; cursor: pointer;
  font-size: 0.85rem; margin-bottom: 2px;
}
.session-list li {
  display: flex; align-items: center; justify-content: space-between;
}
.session-title { flex: 1; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.btn-close {
  background: transparent; color: #666; border: none; border-radius: 4px;
  width: 20px; height: 20px; cursor: pointer; font-size: 1rem; line-height: 1;
  flex-shrink: 0; margin-left: 4px;
}
.btn-close:hover { background: #e74c3c; color: #fff; }
.device-list li:hover, .session-list li:hover, .shell-list li:hover { background: #1a1a4e; }
.device-list li.active, .session-list li.active, .lang-list li.active { background: #0f3460; color: #00d4ff; }
.device-name { display: block; }
.device-os { display: block; font-size: 0.75rem; color: #666; }
.mode-badge {
  display: inline-block; font-size: 0.65rem; padding: 1px 5px; border-radius: 3px;
  margin-left: 6px; vertical-align: middle; font-weight: 600;
}
.mode-server { background: #00d4ff; color: #000; }
.mode-member { background: #2a2a4e; color: #888; }
.mode-mobile { background: #2ecc71; color: #000; }
.empty-hint { font-size: 0.8rem; color: #555; }
.terminal-area { flex: 1; display: flex; background: #000; }
.terminal-area { position: relative; }
.terminal-toolbar {
  position: absolute; top: 8px; right: 10px; z-index: 2;
}
.btn-compact {
  background: #0f3460; color: #00d4ff; border: 1px solid #1a1a4e;
  border-radius: 6px; padding: 4px 8px; font-size: 0.75rem; cursor: pointer;
}
.btn-compact:hover { background: #16213e; }
.terminal-placeholder {
  flex: 1; display: flex; align-items: center; justify-content: center;
  color: #555;
}
</style>

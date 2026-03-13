import { ref, onUnmounted } from 'vue';

export interface WsMessage {
  type: string;
  [key: string]: any;
}

export function useWebSocket(token: string) {
  const connected = ref(false);
  const messages = ref<WsMessage[]>([]);
  let ws: WebSocket | null = null;
  let reconnectTimer: ReturnType<typeof setTimeout> | null = null;
  let heartbeatTimer: ReturnType<typeof setInterval> | null = null;
  let disposed = false;

  const handlers = new Map<string, ((msg: WsMessage) => void)[]>();

  function on(type: string, handler: (msg: WsMessage) => void) {
    if (!handlers.has(type)) handlers.set(type, []);
    handlers.get(type)!.push(handler);
  }

  function off(type: string, handler: (msg: WsMessage) => void) {
    const list = handlers.get(type);
    if (list) {
      const idx = list.indexOf(handler);
      if (idx >= 0) list.splice(idx, 1);
    }
  }

  function buildWsUrl(): string {
    const loc = window.location;
    const proto = loc.protocol === 'https:' ? 'wss:' : 'ws:';
    return `${proto}//${loc.host}/ws/?token=${encodeURIComponent(token)}`;
  }

  function connect() {
    if (disposed) return;
    try {
      ws = new WebSocket(buildWsUrl());
    } catch {
      scheduleReconnect();
      return;
    }

    ws.onopen = () => {
      connected.value = true;
      startHeartbeat();
    };

    ws.onmessage = (event) => {
      try {
        const msg = JSON.parse(event.data) as WsMessage;
        messages.value.push(msg);
        const list = handlers.get(msg.type);
        if (list) list.forEach(h => h(msg));
        // Wildcard handler
        const allList = handlers.get('*');
        if (allList) allList.forEach(h => h(msg));
      } catch {}
    };

    ws.onclose = () => {
      connected.value = false;
      stopHeartbeat();
      scheduleReconnect();
    };

    ws.onerror = () => {
      try { ws?.close(); } catch {}
    };
  }

  function send(msg: object) {
    if (ws?.readyState === WebSocket.OPEN) {
      ws.send(JSON.stringify(msg));
    }
  }

  function scheduleReconnect() {
    if (disposed) return;
    reconnectTimer = setTimeout(() => connect(), 3000);
  }

  function startHeartbeat() {
    heartbeatTimer = setInterval(() => {
      if (ws?.readyState === WebSocket.OPEN) {
        // Server sends ping frames, we just check connection is alive
      }
    }, 30000);
  }

  function stopHeartbeat() {
    if (heartbeatTimer) {
      clearInterval(heartbeatTimer);
      heartbeatTimer = null;
    }
  }

  function disconnect() {
    disposed = true;
    if (reconnectTimer) clearTimeout(reconnectTimer);
    stopHeartbeat();
    try { ws?.close(); } catch {}
    ws = null;
  }

  onUnmounted(() => disconnect());

  // Auto-connect
  connect();

  return { connected, messages, send, on, off, disconnect };
}

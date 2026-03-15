<template>
  <div class="terminal-container" ref="containerRef"></div>
</template>

<script setup lang="ts">
import { ref, onMounted, onBeforeUnmount, watch, computed } from 'vue';
import { Terminal } from '@xterm/xterm';
import { FitAddon } from '@xterm/addon-fit';
import '@xterm/xterm/css/xterm.css';
import type { useWebSocket } from '../composables/useWebSocket';

const props = defineProps<{
  sessionId: string;
  deviceId: string;
  ws: ReturnType<typeof useWebSocket>;
  compact?: boolean;
  compactCols?: number;
  compactRows?: number;
}>();

const containerRef = ref<HTMLDivElement | null>(null);
let term: Terminal | null = null;
let fitAddon: FitAddon | null = null;
let resizeObserver: ResizeObserver | null = null;
const compactCols = computed(() => props.compactCols ?? 80);
const compactRows = computed(() => props.compactRows ?? 24);

function handleOutput(msg: any) {
  if (msg.sessionId === props.sessionId && msg.deviceId === props.deviceId && msg.data) {
    term?.write(msg.data);
  }
}

function handleClosed(msg: any) {
  if (msg.sessionId === props.sessionId) {
    term?.write('\r\n\x1b[33m[Session closed]\x1b[0m\r\n');
  }
}

onMounted(() => {
  if (!containerRef.value) return;

  term = new Terminal({
    allowProposedApi: true,
    cursorBlink: true,
    fontSize: 14,
    fontFamily: "'Cascadia Code', 'Fira Code', 'JetBrains Mono', monospace",
    theme: {
      background: '#0a0a1a',
      foreground: '#e0e0e0',
      cursor: '#00d4ff',
      selectionBackground: '#264f78',
    },
  });

  fitAddon = new FitAddon();
  term.loadAddon(fitAddon);
  term.open(containerRef.value);

  // Suppress terminal query responses that can leak into the shell prompt
  try {
    const parser = term.parser;
    parser.registerOscHandler(10, () => true);
    parser.registerOscHandler(11, () => true);
    parser.registerOscHandler(12, () => true);
    parser.registerOscHandler(4, () => true);
    parser.registerCsiHandler({ final: 'c' }, () => true);
  } catch {
    // Ignore if proposed API is unavailable
  }

  function stripTerminalResponses(data: string): string {
    let cleaned = data;
    cleaned = cleaned.replace(/\x1b\](?:10|11|12|4);[^\x07\x1b]*(?:\x07|\x1b\\)/g, '');
    cleaned = cleaned.replace(/\x1b\[[0-9;?]*c/g, '');
    return cleaned;
  }

  function sendResize(cols: number, rows: number) {
    props.ws.send({
      type: 'terminal.resize',
      deviceId: props.deviceId,
      sessionId: props.sessionId,
      cols,
      rows,
    });
  }

  function applyCompactSize() {
    if (!term) return;
    term.resize(compactCols.value, compactRows.value);
    sendResize(compactCols.value, compactRows.value);
  }

  function applyFitSize() {
    if (!term) return;
    fitAddon?.fit();
    sendResize(term.cols, term.rows);
  }

  function applySizeMode(initial: boolean = false) {
    if (props.compact) {
      applyCompactSize();
      return;
    }
    if (initial) {
      applyFitSize();
    } else {
      fitAddon?.fit();
      if (term) sendResize(term.cols, term.rows);
    }
  }

  // Fit to container or apply compact mode
  requestAnimationFrame(() => applySizeMode(true));

  // Handle user input
  term.onData((data: string) => {
    const cleaned = stripTerminalResponses(data);
    if (!cleaned) return;
    props.ws.send({
      type: 'terminal.input',
      deviceId: props.deviceId,
      sessionId: props.sessionId,
      data: cleaned,
    });
  });

  // Handle resize
  resizeObserver = new ResizeObserver(() => {
    requestAnimationFrame(() => {
      if (props.compact) return;
      fitAddon?.fit();
      if (term) sendResize(term.cols, term.rows);
    });
  });
  resizeObserver.observe(containerRef.value);

  // Listen for output
  props.ws.on('terminal.output', handleOutput);
  props.ws.on('terminal.closed', handleClosed);

  // Focus
  term.focus();

  watch(
    () => props.compact,
    () => {
      if (!term) return;
      applySizeMode();
    }
  );
});

onBeforeUnmount(() => {
  props.ws.off('terminal.output', handleOutput);
  props.ws.off('terminal.closed', handleClosed);
  resizeObserver?.disconnect();
  term?.dispose();
  term = null;
  fitAddon = null;
});
</script>

<style scoped>
.terminal-container {
  flex: 1;
  padding: 4px;
  background: #0a0a1a;
}
.terminal-container :deep(.xterm) {
  height: 100%;
}
</style>

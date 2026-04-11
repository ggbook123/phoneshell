<template>
  <div class="terminal-container" ref="containerRef"></div>
</template>

<script setup lang="ts">
import { ref, onMounted, onBeforeUnmount, watch, computed, nextTick } from 'vue';
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
const compactCols = computed(() => props.compactCols);
const compactRows = computed(() => props.compactRows);
const latestBufferPageChars = 240000;
const maxRenderedChars = 5000000;
let latestBufferLoading = false;
let latestBufferLoaded = false;
let latestBufferRequested = false;
let latestBufferRequestId = '';
let latestBufferSeq = 0;
let renderedBuffer = '';
let pendingOutputs: Array<{ seq: number; data: string }> = [];

function trimRenderedBuffer(data: string): string {
  if (data.length <= maxRenderedChars) {
    return data;
  }
  return data.slice(data.length - maxRenderedChars);
}

function handleOutput(msg: any) {
  if (msg.sessionId === props.sessionId && msg.deviceId === props.deviceId && msg.data) {
    const outputSeq = Number(msg.outputSeq || 0);
    if (!latestBufferLoaded) {
      pendingOutputs.push({ seq: outputSeq, data: msg.data });
      return;
    }
    if (outputSeq === 0 || outputSeq <= latestBufferSeq) {
      return;
    }
    latestBufferSeq = Math.max(latestBufferSeq, outputSeq);
    renderedBuffer = trimRenderedBuffer(renderedBuffer + msg.data);
    term?.write(msg.data);
  }
}

function handleClosed(msg: any) {
  if (msg.sessionId === props.sessionId) {
    term?.write('\r\n\x1b[33m[Session closed]\x1b[0m\r\n');
  }
}

function resetBufferState() {
  latestBufferLoading = false;
  latestBufferLoaded = false;
  latestBufferRequested = false;
  latestBufferRequestId = '';
  latestBufferSeq = 0;
  renderedBuffer = '';
  pendingOutputs = [];
}

function buildBufferRequestId() {
  return `${Date.now()}_${Math.floor(Math.random() * 1000000)}`;
}

function requestLatestBuffer() {
  if (!term || latestBufferRequested) return;
  latestBufferRequested = true;
  latestBufferLoading = true;
  latestBufferLoaded = false;
  latestBufferSeq = 0;
  latestBufferRequestId = buildBufferRequestId();
  pendingOutputs = [];
  props.ws.send({
    type: 'terminal.buffer.request',
    deviceId: props.deviceId,
    sessionId: props.sessionId,
    requestId: latestBufferRequestId,
    beforeCursor: '',
    maxChars: latestBufferPageChars,
  });
}

function replaceTerminalBuffer(data: string) {
  if (!term) return;
  renderedBuffer = trimRenderedBuffer(data);
  term.reset();
  if (renderedBuffer) {
    term.write(renderedBuffer);
  }
}

function handleBufferResponse(msg: any) {
  if (msg.sessionId !== props.sessionId || msg.deviceId !== props.deviceId) return;
  if ((msg.mode || 'latest') !== 'latest') return;
  const requestId = typeof msg.requestId === 'string' ? msg.requestId : '';
  if (latestBufferRequestId && requestId && requestId !== latestBufferRequestId) {
    return;
  }
  latestBufferLoading = false;
  latestBufferLoaded = true;
  latestBufferRequested = false;
  latestBufferRequestId = '';
  latestBufferSeq = Number(msg.snapshotOutputSeq || 0);
  replaceTerminalBuffer(typeof msg.data === 'string' ? msg.data : '');

  const queuedOutputs = pendingOutputs;
  pendingOutputs = [];
  for (const item of queuedOutputs) {
    if (!item.data) continue;
    if (item.seq > 0 && item.seq <= latestBufferSeq) continue;
    if (item.seq > 0) {
      latestBufferSeq = Math.max(latestBufferSeq, item.seq);
    }
    renderedBuffer = trimRenderedBuffer(renderedBuffer + item.data);
    term?.write(item.data);
  }
}

onMounted(() => {
  if (!containerRef.value) return;

  term = new Terminal({
    allowProposedApi: true,
    cursorBlink: true,
    fontSize: 14,
    fontFamily: "'Cascadia Code', 'Fira Code', 'JetBrains Mono', monospace",
    scrollback: 1000000,
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
    // Device attribute / status replies
    cleaned = cleaned.replace(/\x1b\[[0-9;?<>]*c/g, '');
    cleaned = cleaned.replace(/\x1b\[[0-9;?<>]*n/g, '');
    // Drop DSR cursor position replies (ESC [ row ; col R)
    cleaned = cleaned.replace(/\x1b\[[0-9;?<>]*R/g, '');
    // Window ops replies (ESC [ ... t)
    cleaned = cleaned.replace(/\x1b\[[0-9;?<>]*t/g, '');
    // DECRQM replies (ESC [ ? ... $ y)
    cleaned = cleaned.replace(/\x1b\[[0-9;?<>]*\$y/g, '');
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
    if (compactCols.value && compactRows.value) {
      term.resize(compactCols.value, compactRows.value);
      sendResize(compactCols.value, compactRows.value);
      return;
    }
    fitAddon?.fit();
    sendResize(term.cols, term.rows);
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
  props.ws.on('terminal.buffer.response', handleBufferResponse);

  resetBufferState();
  requestLatestBuffer();

  // Focus
  term.focus();

  watch(
    () => props.compact,
    async () => {
      if (!term) return;
      await nextTick();
      requestAnimationFrame(() => applySizeMode());
    }
  );
});

onBeforeUnmount(() => {
  props.ws.off('terminal.output', handleOutput);
  props.ws.off('terminal.closed', handleClosed);
  props.ws.off('terminal.buffer.response', handleBufferResponse);
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

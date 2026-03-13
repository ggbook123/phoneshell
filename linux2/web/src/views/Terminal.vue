<template>
  <div class="terminal-container" ref="containerRef"></div>
</template>

<script setup lang="ts">
import { ref, onMounted, onBeforeUnmount, watch } from 'vue';
import { Terminal } from '@xterm/xterm';
import { FitAddon } from '@xterm/addon-fit';
import '@xterm/xterm/css/xterm.css';
import type { useWebSocket } from '../composables/useWebSocket';

const props = defineProps<{
  sessionId: string;
  deviceId: string;
  ws: ReturnType<typeof useWebSocket>;
}>();

const containerRef = ref<HTMLDivElement | null>(null);
let term: Terminal | null = null;
let fitAddon: FitAddon | null = null;
let resizeObserver: ResizeObserver | null = null;

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

  // Fit to container
  requestAnimationFrame(() => {
    fitAddon?.fit();
    // Send initial resize
    props.ws.send({
      type: 'terminal.resize',
      deviceId: props.deviceId,
      sessionId: props.sessionId,
      cols: term!.cols,
      rows: term!.rows,
    });
  });

  // Handle user input
  term.onData((data: string) => {
    props.ws.send({
      type: 'terminal.input',
      deviceId: props.deviceId,
      sessionId: props.sessionId,
      data,
    });
  });

  // Handle resize
  resizeObserver = new ResizeObserver(() => {
    requestAnimationFrame(() => {
      fitAddon?.fit();
      if (term) {
        props.ws.send({
          type: 'terminal.resize',
          deviceId: props.deviceId,
          sessionId: props.sessionId,
          cols: term.cols,
          rows: term.rows,
        });
      }
    });
  });
  resizeObserver.observe(containerRef.value);

  // Listen for output
  props.ws.on('terminal.output', handleOutput);
  props.ws.on('terminal.closed', handleClosed);

  // Focus
  term.focus();
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

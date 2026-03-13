<template>
  <div class="qr-login">
    <div class="login-container">
      <h1>PhoneShell</h1>
      <p class="subtitle">Remote Terminal Control</p>

      <!-- Step 1: Check pairing status -->
      <div v-if="step === 'loading'" class="status-box">
        <p>Connecting...</p>
      </div>

      <!-- Step 2: Show bind QR (no mobile bound yet) -->
      <div v-else-if="step === 'bind'" class="qr-box">
        <p>Scan with PhoneShell app to bind your phone</p>
        <img :src="bindQrUrl" alt="Bind QR Code" class="qr-image" />
        <p class="hint">After binding, the page will update automatically.</p>
      </div>

      <!-- Step 3: Show login QR (mobile bound, awaiting scan) -->
      <div v-else-if="step === 'login'" class="qr-box">
        <p>Scan with your bound phone to log in</p>
        <img v-if="loginQrUrl" :src="loginQrUrl" alt="Login QR Code" class="qr-image" />
        <p v-else class="hint">Generating login QR...</p>
      </div>

      <!-- Step 4: Waiting for approval -->
      <div v-else-if="step === 'approval'" class="status-box">
        <p>Waiting for approval on your phone...</p>
        <div class="spinner"></div>
      </div>

      <!-- Error -->
      <div v-else-if="step === 'error'" class="status-box error">
        <p>{{ errorMessage }}</p>
        <button @click="startLogin">Retry</button>
      </div>
    </div>
  </div>
</template>

<script setup lang="ts">
import { ref, onMounted, onUnmounted } from 'vue';

const emit = defineEmits<{ authenticated: [token: string] }>();

const step = ref<'loading' | 'bind' | 'login' | 'approval' | 'error'>('loading');
const errorMessage = ref('');
const bindQrUrl = ref('');
const loginQrUrl = ref('');
let pollTimer: ReturnType<typeof setInterval> | null = null;
let loginRequestId = '';

onMounted(() => startLogin());
onUnmounted(() => { if (pollTimer) clearInterval(pollTimer); });

async function startLogin() {
  step.value = 'loading';
  errorMessage.value = '';

  try {
    // Always create login session first — so it exists for auto-approve on first bind
    const loginRes = await fetch('/api/panel/login/start');
    const loginData = await loginRes.json();
    loginRequestId = loginData.requestId;

    if (loginData.status === 'awaiting_mobile') {
      // No mobile bound yet — show bind QR, poll login status for auto-approve
      const pairingRes = await fetch('/api/panel/pairing');
      const pairing = await pairingRes.json();
      if (pairing.qrPayload) {
        step.value = 'bind';
        bindQrUrl.value = `/api/panel/qr.png?payload=${encodeURIComponent(pairing.qrPayload)}`;
      } else {
        step.value = 'approval';
      }
      // Poll login status — will auto-approve when mobile binds
      startLoginPoll();
      return;
    }

    if (loginData.status === 'awaiting_scan' && loginData.loginQrPayload) {
      // Mobile is bound — show login QR for phone to scan
      step.value = 'login';
      loginQrUrl.value = `/api/panel/login/qr.png?payload=${encodeURIComponent(loginData.loginQrPayload)}`;
    } else {
      step.value = 'approval';
    }

    startLoginPoll();
  } catch (err) {
    step.value = 'error';
    errorMessage.value = 'Failed to connect to server.';
  }
}

function startLoginPoll() {
  if (pollTimer) clearInterval(pollTimer);
  pollTimer = setInterval(async () => {
    try {
      const res = await fetch(`/api/panel/login/status/${loginRequestId}`);
      if (!res.ok) {
        // Session not found (e.g., expired) — restart login flow
        if (pollTimer) clearInterval(pollTimer);
        pollTimer = null;
        startLogin();
        return;
      }
      const data = await res.json();

      if (data.status === 'approved' && data.token) {
        if (pollTimer) clearInterval(pollTimer);
        pollTimer = null;
        emit('authenticated', data.token);
        return;
      }

      if (data.status === 'awaiting_scan' && data.loginQrPayload) {
        step.value = 'login';
        loginQrUrl.value = `/api/panel/login/qr.png?payload=${encodeURIComponent(data.loginQrPayload)}`;
      } else if (data.status === 'awaiting_approval') {
        step.value = 'approval';
      } else if (data.status === 'rejected') {
        if (pollTimer) clearInterval(pollTimer);
        step.value = 'error';
        errorMessage.value = data.message || 'Login rejected.';
      } else if (data.status === 'expired') {
        if (pollTimer) clearInterval(pollTimer);
        pollTimer = null;
        startLogin();
      }
    } catch {}
  }, 1500);
}
</script>

<style scoped>
.qr-login {
  display: flex; align-items: center; justify-content: center;
  min-height: 100vh; padding: 20px;
}
.login-container {
  text-align: center; max-width: 400px; width: 100%;
}
h1 { font-size: 2rem; color: #00d4ff; margin-bottom: 8px; }
.subtitle { color: #888; margin-bottom: 32px; }
.qr-box { background: #16213e; border-radius: 12px; padding: 24px; }
.qr-box p { margin-bottom: 16px; }
.qr-image {
  width: 256px; height: 256px; border-radius: 8px;
  background: #fff; padding: 8px; image-rendering: pixelated;
}
.hint { color: #666; font-size: 0.85rem; margin-top: 16px; }
.status-box { background: #16213e; border-radius: 12px; padding: 32px; }
.status-box.error { border: 1px solid #e74c3c; }
.spinner {
  width: 32px; height: 32px; border: 3px solid #333;
  border-top-color: #00d4ff; border-radius: 50%;
  animation: spin 1s linear infinite; margin: 16px auto 0;
}
@keyframes spin { to { transform: rotate(360deg); } }
button {
  margin-top: 16px; padding: 10px 24px; border: none;
  background: #00d4ff; color: #000; border-radius: 6px;
  cursor: pointer; font-size: 1rem;
}
button:hover { background: #00b8d9; }
</style>

<template>
  <div id="phoneshell-app">
    <QrLogin v-if="!authenticated" @authenticated="onAuthenticated" />
    <Dashboard v-else :token="token" />
  </div>
</template>

<script setup lang="ts">
import { ref, onMounted } from 'vue';
import QrLogin from './views/QrLogin.vue';
import Dashboard from './views/Dashboard.vue';

const authenticated = ref(false);
const token = ref('');

onMounted(async () => {
  const url = new URL(window.location.href);
  const tokenFromUrl = url.searchParams.get('token') || '';
  const stored = localStorage.getItem('phoneshell_token') || '';
  const effective = tokenFromUrl.trim() || stored.trim();

  if (tokenFromUrl) {
    localStorage.setItem('phoneshell_token', tokenFromUrl);
    url.searchParams.delete('token');
    window.history.replaceState({}, '', url.pathname + url.search + url.hash);
  }

  if (effective) {
    // Always re-verify — server returns valid:false to force scan flow
    try {
      const res = await fetch('/api/panel/verify', {
        headers: { 'Authorization': `Bearer ${effective}` },
      });
      const data = await res.json();
      if (data.valid) {
        token.value = effective;
        authenticated.value = true;
        return;
      }
    } catch {}
    localStorage.removeItem('phoneshell_token');
  }
});

function onAuthenticated(newToken: string) {
  token.value = newToken;
  localStorage.setItem('phoneshell_token', newToken);
  authenticated.value = true;
}
</script>

<style>
* { margin: 0; padding: 0; box-sizing: border-box; }
body { font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, sans-serif; background: #1a1a2e; color: #e0e0e0; }
#phoneshell-app { min-height: 100vh; }
</style>

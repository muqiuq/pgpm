<script setup lang="ts">
import { onBeforeUnmount, onMounted, ref } from 'vue'

/**
 * The sync indicator's place in the app bar (docs/01 §3, docs/03 §12.6). Phase 2 shows live / syncing / offline /
 * pending changes / conflicts here and opens the sync dialog; until then it shows whether the browser is online.
 */
const online = ref(typeof navigator === 'undefined' ? true : navigator.onLine)
const update = () => (online.value = navigator.onLine)

onMounted(() => {
  window.addEventListener('online', update)
  window.addEventListener('offline', update)
})
onBeforeUnmount(() => {
  window.removeEventListener('online', update)
  window.removeEventListener('offline', update)
})
</script>

<template>
  <span
    v-tooltip.bottom="'Synchronisation arrives in phase 2'"
    class="sync-indicator"
    :class="online ? 'online' : 'offline'"
    role="status"
    :aria-label="online ? 'Online' : 'Offline'"
    data-testid="sync-indicator"
  >
    <span class="dot" aria-hidden="true" />
    <span class="label">{{ online ? 'Online' : 'Offline' }}</span>
  </span>
</template>

<style scoped>
.sync-indicator {
  display: inline-flex;
  align-items: center;
  gap: var(--pgpm-space-2);
  padding: 0 var(--pgpm-space-2);
  height: 28px;
  border-radius: 999px;
  font-size: 0.857rem;
  color: var(--pgpm-muted);
  white-space: nowrap;
}

.dot {
  width: 8px;
  height: 8px;
  border-radius: 50%;
}

.online .dot {
  background: var(--pgpm-ok);
}

.offline {
  background: var(--pgpm-warn-soft);
  color: var(--pgpm-warn);
}

.offline .dot {
  background: var(--pgpm-warn);
}

@media (max-width: 599px) {
  .label {
    display: none;
  }
}
</style>

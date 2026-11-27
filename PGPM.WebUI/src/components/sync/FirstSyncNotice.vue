<script setup lang="ts">
import { computed } from 'vue'
import { useSyncStore } from '@/stores/sync'

/**
 * *Preparing your data… (first sync)* (docs/03 §12.6): shown by lists while they are empty and by create dialogs while
 * the first sync after signing in brings the configuration (statuses, templates, sections, groups). `compact` is the
 * dialogs' one-line form.
 */
defineProps<{ compact?: boolean }>()
const sync = useSyncStore()
const offline = computed(() => sync.state === 'offline')
</script>

<template>
  <div class="first-sync" :class="{ compact }" role="status" aria-live="polite" data-testid="first-sync">
    <i class="pi pi-spin pi-spinner" aria-hidden="true" />
    <div>
      <div class="title">Preparing your data… (first sync)</div>
      <div class="muted">
        {{
          offline
            ? 'Waiting for a connection to finish it.'
            : 'Everything you may see is copied to this device once; then PGPM works offline too.'
        }}
      </div>
    </div>
  </div>
</template>

<style scoped>
.first-sync {
  display: flex;
  align-items: center;
  gap: var(--pgpm-space-3);
  padding: var(--pgpm-space-4);
}

.first-sync > i {
  font-size: 1.5rem;
  color: var(--p-primary-color);
}

.first-sync.compact {
  padding: var(--pgpm-space-2) var(--pgpm-space-3);
  border-radius: var(--p-border-radius-md);
  background: var(--p-highlight-background);
}

.first-sync.compact > i {
  font-size: 1rem;
}

.title {
  font-weight: 500;
}
</style>

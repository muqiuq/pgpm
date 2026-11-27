<script setup lang="ts">
import Button from 'primevue/button'
import { useNotify } from '@/composables/useNotify'

/** A value to copy (a temporary password, a 2FA secret), monospace, with a copy button (PNMTD). */
const props = defineProps<{ value: string; label?: string; display?: string }>()
const notify = useNotify()

async function copy() {
  try {
    await navigator.clipboard.writeText(props.value)
    notify.success('Copied', props.label)
  } catch {
    notify.error(new Error('The browser does not allow copying here; select the text instead.'), 'Not copied')
  }
}
</script>

<template>
  <div class="copy-field">
    <code class="value" :aria-label="label" data-testid="copy-value">{{ display ?? value }}</code>
    <Button
      v-tooltip.top="'Copy'"
      icon="pi pi-copy"
      size="small"
      text
      severity="secondary"
      :aria-label="`Copy ${label ?? 'value'}`"
      @click="copy"
    />
  </div>
</template>

<style scoped>
.copy-field {
  display: flex;
  align-items: center;
  gap: var(--pgpm-space-1);
  min-width: 0;
  background: var(--pgpm-bg);
  border: 1px solid var(--pgpm-border);
  border-radius: var(--p-border-radius-md);
  padding-left: var(--pgpm-space-2);
}

.value {
  flex: 1;
  min-width: 0;
  overflow-wrap: anywhere;
  user-select: all;
  font-family: var(--pgpm-mono);
}
</style>

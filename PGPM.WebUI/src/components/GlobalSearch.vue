<script setup lang="ts">
import IconField from 'primevue/iconfield'
import InputIcon from 'primevue/inputicon'
import InputText from 'primevue/inputtext'
import { computed, onBeforeUnmount, onMounted, ref } from 'vue'

/**
 * The global search's place in the app bar (docs/01 §3): Ctrl/⌘ K focuses it. Phase 3 searches every item type here,
 * offline, from IndexedDB; until then it only holds the place.
 */
const field = ref<{ $el?: HTMLElement } | null>(null)
const isMac = typeof navigator !== 'undefined' && /Mac|iPhone|iPad/.test(navigator.platform)
const shortcut = computed(() => (isMac ? '⌘K' : 'Ctrl K'))

function onKey(event: KeyboardEvent) {
  if ((event.metaKey || event.ctrlKey) && event.key.toLowerCase() === 'k') {
    event.preventDefault()
    field.value?.$el?.querySelector('input')?.focus()
  }
}

onMounted(() => window.addEventListener('keydown', onKey))
onBeforeUnmount(() => window.removeEventListener('keydown', onKey))
</script>

<template>
  <IconField ref="field" class="global-search">
    <InputIcon class="pi pi-search" />
    <InputText
      size="small"
      :placeholder="`Search (${shortcut}) – arrives in phase 3`"
      aria-label="Search"
      readonly
      fluid
    />
  </IconField>
</template>

<style scoped>
.global-search {
  width: min(28rem, 100%);
}
</style>

<script setup lang="ts">
import IconField from 'primevue/iconfield'
import InputIcon from 'primevue/inputicon'
import InputText from 'primevue/inputtext'
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { localDb } from '@/local/db'
import { searchLocal, type SearchResult } from '@/local/search'
import { useLive } from '@/local/useLive'

/**
 * The global search in the app bar (docs/01 §3): Ctrl/⌘ K focuses it; it searches every item type and the persons in
 * the local data (local/search.ts) – instant, offline, following changes live – and lists the results grouped *Items* /
 * *Persons* below the field. ↑/↓ move, Enter opens, Esc closes; a click opens too. A combobox with its listbox
 * (`aria-activedescendant`), closed on blur and after a navigation.
 */
const route = useRoute()
const router = useRouter()
const field = ref<{ $el?: HTMLElement } | null>(null)
const isMac = typeof navigator !== 'undefined' && /Mac|iPhone|iPad/.test(navigator.platform)
const shortcut = computed(() => (isMac ? '⌘K' : 'Ctrl K'))
const listId = 'global-search-results'

const query = ref('')
const debounced = ref('')
const open = ref(false)
const active = ref(0)
let timer: ReturnType<typeof setTimeout> | undefined

watch(query, (value) => {
  clearTimeout(timer)
  open.value = !!value.trim()
  timer = setTimeout(() => (debounced.value = value), 80)
})

const found = useLive(
  () => searchLocal(localDb(), debounced.value),
  [] as SearchResult[],
  () => debounced.value,
)
const results = computed(() => (debounced.value.trim() ? found.value.value : []))
const ready = computed(() => found.loaded.value && debounced.value === query.value)
watch(results, () => (active.value = 0))

const groups = computed(() => {
  const list = results.value.map((result, index) => ({ result, index }))
  return [
    { label: 'Items', entries: list.filter((e) => e.result.kind === 'item') },
    { label: 'Persons', entries: list.filter((e) => e.result.kind === 'person') },
  ].filter((g) => g.entries.length)
})

const optionId = (index: number) => `global-search-option-${index}`
const activeId = computed(() => (open.value && results.value.length ? optionId(active.value) : undefined))

function input(): HTMLInputElement | null {
  const el = field.value?.$el
  return el instanceof HTMLInputElement ? el : (el?.querySelector('input') ?? null)
}

function choose(result: SearchResult | undefined) {
  if (!result) return
  open.value = false
  query.value = ''
  debounced.value = ''
  input()?.blur()
  void router.push(result.to)
}

function onInputKey(event: KeyboardEvent) {
  const count = results.value.length
  if (event.key === 'ArrowDown' || event.key === 'ArrowUp') {
    event.preventDefault()
    if (!open.value) {
      open.value = !!query.value.trim()
      return
    }
    if (!count) return
    active.value = (active.value + (event.key === 'ArrowDown' ? 1 : count - 1)) % count
    document.getElementById(optionId(active.value))?.scrollIntoView?.({ block: 'nearest' })
  } else if (event.key === 'Enter') {
    if (!open.value || !count) return
    event.preventDefault()
    choose(results.value[active.value])
  } else if (event.key === 'Escape') {
    if (open.value) {
      event.preventDefault()
      open.value = false
    } else {
      query.value = ''
      input()?.blur()
    }
  }
}

function onFocus() {
  open.value = !!query.value.trim()
}

function onBlur() {
  open.value = false
}

// Another page closes the results – not a change of the page's query (a list writes its search and filters there, a
// moment after typing: that closed the results while one typed here).
watch(
  () => route.path,
  () => (open.value = false),
)

function onKey(event: KeyboardEvent) {
  if ((event.metaKey || event.ctrlKey) && event.key.toLowerCase() === 'k') {
    event.preventDefault()
    const el = input()
    el?.focus()
    el?.select()
  }
}

onMounted(() => window.addEventListener('keydown', onKey))
onBeforeUnmount(() => {
  window.removeEventListener('keydown', onKey)
  clearTimeout(timer)
})
</script>

<template>
  <div class="global-search">
    <IconField ref="field" class="field">
      <InputIcon class="pi pi-search" />
      <InputText
        v-model="query"
        size="small"
        :placeholder="`Search (${shortcut})`"
        aria-label="Search"
        role="combobox"
        aria-autocomplete="list"
        :aria-expanded="open"
        :aria-controls="listId"
        :aria-activedescendant="activeId"
        autocomplete="off"
        spellcheck="false"
        fluid
        data-testid="global-search"
        @keydown="onInputKey"
        @focus="onFocus"
        @blur="onBlur"
      />
    </IconField>
    <div v-show="open" :id="listId" class="results" role="listbox" aria-label="Search results">
      <div v-for="group in groups" :key="group.label" role="group" :aria-label="group.label">
        <div class="group-label" aria-hidden="true">{{ group.label }}</div>
        <div
          v-for="{ result, index } in group.entries"
          :id="optionId(index)"
          :key="`${result.kind}-${result.id}`"
          role="option"
          class="option"
          :class="{ active: index === active }"
          :aria-selected="index === active"
          @mousedown.prevent="choose(result)"
          @mousemove="active = index"
        >
          <i :class="result.icon" aria-hidden="true" />
          <span v-if="result.key" class="key mono muted">{{ result.key }}</span>
          <span class="title ellipsis">{{ result.title }}</span>
          <span v-if="result.subtitle" class="subtitle muted ellipsis">{{ result.subtitle }}</span>
        </div>
      </div>
      <div v-if="ready && !results.length" class="empty muted" role="presentation">Nothing found</div>
    </div>
  </div>
</template>

<style scoped>
.global-search {
  position: relative;
  width: min(28rem, 100%);
}

.results {
  position: absolute;
  top: calc(100% + var(--pgpm-space-1));
  left: 0;
  right: 0;
  z-index: 1100;
  max-height: min(28rem, 70vh);
  overflow-y: auto;
  padding: var(--pgpm-space-1) 0;
  background: var(--pgpm-panel);
  border: 1px solid var(--pgpm-border);
  border-radius: var(--p-border-radius-md);
  box-shadow: 0 8px 24px rgb(0 0 0 / 0.12);
}

.group-label {
  padding: var(--pgpm-space-1) var(--pgpm-space-3);
  font-size: 0.786rem;
  font-weight: 600;
  text-transform: uppercase;
  letter-spacing: 0.04em;
  color: var(--pgpm-muted);
}

.option {
  display: flex;
  align-items: center;
  gap: var(--pgpm-space-2);
  min-height: 32px;
  padding: 0 var(--pgpm-space-3);
  cursor: pointer;
  min-width: 0;
}

.option.active {
  background: var(--p-highlight-background);
}

.option .key {
  flex: none;
  font-size: 0.857rem;
}

.option .title {
  flex: 0 1 auto;
}

.option .subtitle {
  flex: 1 1 0;
  font-size: 0.857rem;
}

.empty {
  padding: var(--pgpm-space-2) var(--pgpm-space-3);
}
</style>

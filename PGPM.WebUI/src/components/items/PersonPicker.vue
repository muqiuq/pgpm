<script setup lang="ts">
import AutoComplete from 'primevue/autocomplete'
import { computed, ref, watch } from 'vue'
import { normalise } from '@/composables/useLocalTable'
import { usePersons } from '@/local/lookups'
import type { PersonRow } from '@/local/rows'

/**
 * A person from the directory, by type-ahead over name and e-mail (docs/01 §4.6): the synced persons, offline;
 * inactive ones are not offered (docs/01 §16). v-model is the person's id. `exclude`: ids not to offer (assigned
 * already).
 */
const props = withDefaults(
  defineProps<{ exclude?: readonly string[]; placeholder?: string; inputId?: string; disabled?: boolean }>(),
  { exclude: () => [], placeholder: 'Name or e-mail…', inputId: undefined, disabled: false },
)
const personId = defineModel<string | null>({ default: null })
const persons = usePersons()

const picked = ref<PersonRow | string | null>(null)
const suggestions = ref<PersonRow[]>([])

watch(
  [personId, () => persons.byId.value],
  ([id]) => {
    const current = typeof picked.value === 'object' && picked.value ? picked.value.id : null
    if (id !== current) picked.value = id ? (persons.byId.value.get(id) ?? null) : null
  },
  { immediate: true },
)

const candidates = computed(() =>
  persons.all.value.filter((p) => p.isActive !== false && !props.exclude.includes(p.id)),
)

function search(event: { query: string }) {
  const words = normalise(event.query.trim()).split(/\s+/).filter(Boolean)
  suggestions.value = candidates.value
    .filter((p) => {
      const text = normalise(`${p.displayName} ${p.email ?? ''}`)
      return words.every((w) => text.includes(w))
    })
    .slice(0, 20)
}

function select(value: unknown) {
  picked.value = value as PersonRow | string | null
  personId.value = value && typeof value === 'object' ? (value as PersonRow).id : null
}
</script>

<template>
  <AutoComplete
    :model-value="picked"
    :suggestions="suggestions"
    option-label="displayName"
    :input-id="inputId"
    :placeholder="placeholder"
    :disabled="disabled"
    force-selection
    complete-on-focus
    size="small"
    fluid
    @complete="search"
    @update:model-value="select"
  >
    <template #option="{ option }">
      <div class="person-option">
        <span>{{ option.displayName }}</span>
        <small v-if="option.email || option.jobTitle" class="muted">
          {{ [option.email, option.jobTitle].filter(Boolean).join(' · ') }}
        </small>
      </div>
    </template>
  </AutoComplete>
</template>

<style scoped>
.person-option {
  display: flex;
  flex-direction: column;
  line-height: 1.3;
}
</style>

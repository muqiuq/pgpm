<script setup lang="ts">
import AutoComplete from 'primevue/autocomplete'
import { ref } from 'vue'
import { normalise } from '@/composables/useLocalTable'
import { useRoleSuggestions } from '@/local/lookups'

/** An assignment's role (docs/01 §4.1): free text, with the configured roles (`assignment_roles`) as suggestions. */
withDefaults(defineProps<{ inputId?: string; disabled?: boolean; placeholder?: string }>(), {
  inputId: undefined,
  disabled: false,
  placeholder: 'Role (optional)',
})
const role = defineModel<string | null>({ default: null })
const roles = useRoleSuggestions()
const suggestions = ref<string[]>([])

function search(event: { query: string }) {
  const query = normalise(event.query.trim())
  suggestions.value = roles.value.filter((r) => normalise(r).includes(query))
}
</script>

<template>
  <AutoComplete
    :model-value="role ?? ''"
    :suggestions="suggestions"
    :input-id="inputId"
    :placeholder="placeholder"
    :disabled="disabled"
    complete-on-focus
    size="small"
    fluid
    @complete="search"
    @update:model-value="(v: unknown) => (role = typeof v === 'string' && v.trim() ? v : null)"
  />
</template>

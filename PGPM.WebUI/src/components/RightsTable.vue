<script setup lang="ts">
import SelectButton from 'primevue/selectbutton'
import { computed } from 'vue'
import type { RightsAreaItem } from '@/api/auth'
import type { Area, Level } from '@/api/rights'

/**
 * Rights as one table, a row per area (PNMTD): editable, a level per area from None up (a group's page); or read-only,
 * with the groups each level comes from (a user's effective rights, Your access). Under each row, what the chosen level
 * allows, from the server's catalogue.
 */
const props = withDefaults(
  defineProps<{
    catalog: RightsAreaItem[]
    readonly?: boolean
    /** For read-only rights: the groups that give each level. */
    sources?: Partial<Record<Area, string[]>>
  }>(),
  { readonly: false, sources: () => ({}) },
)
const rights = defineModel<Partial<Record<Area, Level>>>({ default: () => ({}) })

const rows = computed(() =>
  props.catalog.map((a) => {
    const level: Level = rights.value[a.area] ?? 'None'
    const index = a.levels.findIndex((l) => l.level === level)
    return {
      area: a.area,
      label: a.label,
      level,
      options: [
        { value: 'None' as Level, label: 'None' },
        ...a.levels.map((l) => ({ value: l.level, label: l.level })),
      ],
      allows: a.levels.find((l) => l.level === level)?.allows,
      // A level includes the ones below it: what they allow is part of it.
      below: index > 0 ? a.levels.slice(0, index).map((l) => l.level) : [],
    }
  }),
)
const shown = computed(() => (props.readonly ? rows.value.filter((r) => r.level !== 'None') : rows.value))

function set(area: Area, level: Level | null) {
  const next = { ...rights.value }
  if (!level || level === 'None') delete next[area]
  else next[area] = level
  rights.value = next
}
</script>

<template>
  <div class="rights" role="table" :aria-label="readonly ? 'Rights' : 'Rights of the group'">
    <p v-if="readonly && !shown.length" class="muted">No rights: they see what is shared with them.</p>
    <div v-for="row in shown" :key="row.area" class="row" role="row">
      <div class="area" role="rowheader">{{ row.label }}</div>
      <div class="level" role="cell">
        <SelectButton
          v-if="!readonly"
          :model-value="row.level"
          :options="row.options"
          option-label="label"
          option-value="value"
          :allow-empty="false"
          size="small"
          :aria-label="row.label"
          @update:model-value="(v: Level) => set(row.area, v)"
        />
        <strong v-else>{{ row.level }}</strong>
        <small v-if="row.allows" class="allows">
          {{ row.allows }}<template v-if="row.below.length"> Includes {{ row.below.join(' and ') }}.</template>
        </small>
        <small v-if="readonly && sources[row.area]?.length" class="muted">
          From {{ sources[row.area]!.join(', ') }}.</small
        >
      </div>
    </div>
  </div>
</template>

<style scoped>
.rights {
  display: grid;
  gap: var(--pgpm-space-2);
}

.row {
  display: grid;
  grid-template-columns: 9rem minmax(0, 1fr);
  gap: var(--pgpm-space-3);
  align-items: start;
  padding: var(--pgpm-space-2) 0;
  border-bottom: 1px solid var(--pgpm-border);
}

.row:last-child {
  border-bottom: none;
}

.area {
  font-weight: 600;
  padding-top: var(--pgpm-space-1);
}

.level {
  display: grid;
  gap: var(--pgpm-space-1);
  justify-items: start;
}

.allows {
  color: var(--pgpm-muted);
}

@media (max-width: 599px) {
  .row {
    grid-template-columns: 1fr;
    gap: var(--pgpm-space-1);
  }
}
</style>

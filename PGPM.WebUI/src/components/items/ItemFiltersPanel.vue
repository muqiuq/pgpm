<script setup lang="ts">
import Button from 'primevue/button'
import InputText from 'primevue/inputtext'
import MultiSelect from 'primevue/multiselect'
import ToggleSwitch from 'primevue/toggleswitch'
import { computed } from 'vue'
import { itemTypes, presetColors } from '@/domain/itemTypes'
import { relations } from '@/local/items'
import { useGroups, usePersons, useUserPersons } from '@/local/lookups'
import type { ItemType } from '@/local/rows'
import type { ItemFilters } from '@/views/items/itemList'

/**
 * The further filters of an item list (docs/01 §4.3), in the *Filters* panel: owner, assigned person, colour, created
 * and modified ranges, shared with a group, hide closed – and the link filter a saved view may bring (*Learnings*),
 * shown so that it can be removed. v-model is the list's filters; the quick ones (status, assigned to me, overdue) are
 * on the bar.
 */
const props = defineProps<{ type: ItemType }>()
const filters = defineModel<ItemFilters>({ required: true })
const info = computed(() => itemTypes[props.type])
const users = useUserPersons()
const persons = usePersons()
const groups = useGroups()

const owners = computed(() => users.persons.value.map((p) => ({ label: p.displayName, value: p.userId! })))
const assignees = computed(() =>
  persons.all.value.map((p) => ({
    label: p.isActive === false ? `${p.displayName} (inactive)` : p.displayName,
    value: p.id,
  })),
)
const groupOptions = computed(() => groups.value.map((g) => ({ label: g.name, value: g.id })))
const colorOptions = [...presetColors]
const linkText = computed(() => {
  const link = filters.value.hasLink
  if (!link) return null
  const relation = relations.find((r) => r.value === link.relation)
  const label = link.direction === 'incoming' ? relation?.inverse : relation?.label
  const targets = link.targetTypes?.length ? ` → ${link.targetTypes.map((t) => itemTypes[t].label).join(', ')}` : ''
  return `${link.direction === 'any' ? 'any link: ' : ''}${label ?? link.relation}${targets}`
})

function set<K extends keyof ItemFilters>(key: K, value: ItemFilters[K]) {
  filters.value = { ...filters.value, [key]: value }
}

const date = (value: unknown) => (typeof value === 'string' && value ? value : undefined)
</script>

<template>
  <div class="item-filters form" role="group" aria-label="Filters">
    <div class="field">
      <label for="f-owner">Owner</label>
      <MultiSelect
        input-id="f-owner"
        :model-value="filters.ownerIds ?? []"
        :options="owners"
        option-label="label"
        option-value="value"
        filter
        placeholder="Anyone"
        size="small"
        @update:model-value="(v: string[]) => set('ownerIds', v)"
      />
    </div>
    <div class="field">
      <label for="f-person">Assigned person</label>
      <MultiSelect
        input-id="f-person"
        :model-value="filters.personIds ?? []"
        :options="assignees"
        option-label="label"
        option-value="value"
        filter
        placeholder="Anyone"
        size="small"
        @update:model-value="(v: string[]) => set('personIds', v)"
      />
    </div>
    <div class="field">
      <label for="f-group">Shared with</label>
      <MultiSelect
        input-id="f-group"
        :model-value="filters.groupIds ?? []"
        :options="groupOptions"
        option-label="label"
        option-value="value"
        placeholder="Any group"
        size="small"
        @update:model-value="(v: string[]) => set('groupIds', v)"
      />
    </div>
    <div class="field">
      <label for="f-color">Colour</label>
      <MultiSelect
        input-id="f-color"
        :model-value="filters.colors ?? []"
        :options="colorOptions"
        option-label="name"
        option-value="value"
        placeholder="Any colour"
        size="small"
        @update:model-value="(v: string[]) => set('colors', v)"
      >
        <template #option="{ option }">
          <span class="swatch" :style="{ background: option.value }" aria-hidden="true" />{{ option.name }}
        </template>
      </MultiSelect>
    </div>
    <div class="field">
      <span class="label">Created</span>
      <div class="range">
        <InputText
          type="date"
          size="small"
          aria-label="Created from"
          :model-value="filters.createdFrom ?? ''"
          @update:model-value="(v) => set('createdFrom', date(v))"
        />
        <span aria-hidden="true">–</span>
        <InputText
          type="date"
          size="small"
          aria-label="Created until"
          :model-value="filters.createdTo ?? ''"
          @update:model-value="(v) => set('createdTo', date(v))"
        />
      </div>
    </div>
    <div class="field">
      <span class="label">Modified</span>
      <div class="range">
        <InputText
          type="date"
          size="small"
          aria-label="Modified from"
          :model-value="filters.modifiedFrom ?? ''"
          @update:model-value="(v) => set('modifiedFrom', date(v))"
        />
        <span aria-hidden="true">–</span>
        <InputText
          type="date"
          size="small"
          aria-label="Modified until"
          :model-value="filters.modifiedTo ?? ''"
          @update:model-value="(v) => set('modifiedTo', date(v))"
        />
      </div>
    </div>
    <div v-if="info.hasStatus" class="switch">
      <ToggleSwitch
        input-id="f-closed"
        :model-value="!!filters.hideClosed"
        @update:model-value="(v: boolean) => set('hideClosed', v || undefined)"
      />
      <label for="f-closed">Hide closed</label>
    </div>
    <div v-if="linkText" class="link">
      <span><i class="pi pi-link" aria-hidden="true" /> Linked: {{ linkText }}</span>
      <Button
        icon="pi pi-times"
        text
        rounded
        size="small"
        severity="secondary"
        aria-label="Remove the link filter"
        @click="set('hasLink', undefined)"
      />
    </div>
    <p v-if="filters.fields?.length" class="muted small">Field filters of this view apply with phase 4.</p>
  </div>
</template>

<style scoped>
.item-filters {
  width: min(22rem, 90vw);
}

.label {
  font-weight: 600;
}

.range {
  display: flex;
  align-items: center;
  gap: var(--pgpm-space-2);
}

.range :deep(input) {
  flex: 1;
  min-width: 0;
}

.switch,
.link {
  display: flex;
  align-items: center;
  gap: var(--pgpm-space-2);
}

.swatch {
  display: inline-block;
  width: 0.75rem;
  height: 0.75rem;
  border-radius: 50%;
  margin-right: var(--pgpm-space-2);
  vertical-align: middle;
}

.small {
  font-size: 0.857rem;
}
</style>

<script setup lang="ts">
import Button from 'primevue/button'
import Menu from 'primevue/menu'
import type { MenuItem } from 'primevue/menuitem'
import { computed, reactive, ref } from 'vue'
import { useActions } from '@/composables/useActions'
import { useNow } from '@/composables/useNow'
import type { MarkdownFile } from '@/components/markdown/types'
import { sectionsForType } from '@/local/catalogue'
import { addSection, removeSection, setFieldValue } from '@/local/fields'
import { today as todayOf } from '@/local/items'
import { usePersons, useStatuses } from '@/local/lookups'
import { repo } from '@/local/repo'
import type { FieldDefinitionRow, ItemRow, SectionRow } from '@/local/rows'
import { useSyncStore } from '@/stores/sync'
import { useItemFields } from '@/views/items/useItemFields'
import SectionPanel from './SectionPanel.vue'

/**
 * The sections of an idea or project on its Overview (docs/01 §6.2, docs/03 §12.7): in `item_sections` order, each
 * collapsed or open (its `collapsedByDefault` until toggled), read-only where the user may not edit; values saved to the
 * local data at once (offline too) and scores recomputed live. With *edit* access: *Add section* (the sections that
 * apply to the type and aren't shown) and *Remove section* (the values stay).
 */
const props = defineProps<{ item: ItemRow; canEdit: boolean; files?: readonly MarkdownFile[] | null }>()
const { ask, notify } = useActions()
const sync = useSyncStore()
const now = useNow()
const persons = usePersons()
const statuses = useStatuses()
const { catalogue, itemSections, byKey, scores, conflicted, loaded } = useItemFields(() => props.item.id)

const type = computed(() => (props.item.type === 'project' ? 'project' : 'idea'))
const shown = computed(() =>
  itemSections.value
    .map((row) => ({ row, section: catalogue.value.sectionById.get(row.sectionId) }))
    .filter((s): s is { row: (typeof itemSections.value)[number]; section: SectionRow } => !!s.section),
)
const addable = computed(() => {
  const present = new Set(itemSections.value.map((s) => s.sectionId))
  return sectionsForType(catalogue.value, type.value).filter((s) => !present.has(s.id))
})
const closed = computed(
  () => !!props.item.statusId && statuses.byId.value.get(props.item.statusId)?.category === 'closed',
)
const today = computed(() => todayOf(now.value))
const context = computed(() => ({ personName: (id: string) => persons.byId.value.get(id)?.displayName }))

// Collapsed state: the section's default until toggled on this page.
const toggled = reactive(new Map<string, boolean>())
const collapsed = (section: SectionRow) => toggled.get(section.id) ?? section.collapsedByDefault
const toggle = (section: SectionRow) => toggled.set(section.id, !collapsed(section))

async function save(section: SectionRow, field: FieldDefinitionRow, value: unknown) {
  await repo.transaction((tx) => setFieldValue(tx, props.item.id, field, value, { section }))
}

async function add(section: SectionRow) {
  try {
    await repo.transaction((tx) => addSection(tx, props.item.id, section, catalogue.value))
    toggled.set(section.id, false)
  } catch (e) {
    notify.error(e, 'Section not added')
  }
}

async function remove(row: { id: string }, section: SectionRow) {
  const yes = await ask(
    `Remove ${section.name}?`,
    'Its values are kept, hidden until the section is added again.',
    'Remove',
  )
  if (!yes) return
  try {
    await repo.transaction((tx) => removeSection(tx, row.id))
  } catch (e) {
    notify.error(e, 'Section not removed')
  }
}

const addMenu = ref<InstanceType<typeof Menu> | null>(null)
const addItems = computed<MenuItem[]>(() => addable.value.map((s) => ({ label: s.name, command: () => void add(s) })))
</script>

<template>
  <div v-if="loaded" class="item-sections">
    <SectionPanel
      v-for="{ row, section } in shown"
      :key="row.id"
      :item="item"
      :section="section"
      :catalogue="catalogue"
      :values="byKey"
      :scores="scores"
      :can-edit="canEdit"
      :collapsed="collapsed(section)"
      :closed="closed"
      :today="today"
      :context="context"
      :conflicted="conflicted"
      :files="files"
      :save="(field: FieldDefinitionRow, value: unknown) => save(section, field, value)"
      @toggle="toggle(section)"
      @remove="remove(row, section)"
      @conflict="sync.openDialog('conflicts')"
    />
    <div v-if="canEdit && addable.length" class="add">
      <Button
        label="Add section"
        icon="pi pi-plus"
        size="small"
        text
        aria-haspopup="true"
        aria-controls="add-section-menu"
        @click="(e: Event) => addMenu?.toggle(e)"
      />
      <Menu id="add-section-menu" ref="addMenu" :model="addItems" popup />
    </div>
  </div>
</template>

<style scoped>
.item-sections {
  display: flex;
  flex-direction: column;
  gap: var(--pgpm-space-4);
}

.add {
  margin-top: calc(-1 * var(--pgpm-space-2));
}
</style>

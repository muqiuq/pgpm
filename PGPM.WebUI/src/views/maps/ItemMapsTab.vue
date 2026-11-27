<script setup lang="ts">
import Button from 'primevue/button'
import Tag from 'primevue/tag'
import { computed, ref } from 'vue'
import EmptyState from '@/components/EmptyState.vue'
import { useNotify } from '@/composables/useNotify'
import { itemTypes } from '@/domain/itemTypes'
import { localDb } from '@/local/db'
import { addCardToMap, placementsOf, type MapRecord } from '@/local/maps'
import { repo } from '@/local/repo'
import type { ItemRow, ItemShareRow, Stored } from '@/local/rows'
import { useLive } from '@/local/useLive'
import type { FieldDefinition } from '@/maps/cards'
import { toMapRow } from '@/maps/mapRows'
import { matrixCandidates } from '@/maps/matrixItems'
import type { ItemRow as MapItemRow } from '@/maps/types'
import { KIND_ICONS, KIND_LABELS, quadrantsOf, type FieldDefinitionRecord } from './mapList'
import MapPickerDialog from './MapPickerDialog.vue'

/**
 * An item's *Maps* tab (doc 01 §4.4, §12.4): the canvas maps it is placed on – how often – and the matrix maps that
 * show it, with its quadrant (or *Not yet assessed*); each opens the map centred on it (`?focus=item:<id>`). *Add to
 * map…* puts a card of it in the middle of a canvas map the user may edit, on the device, offline too.
 */
const props = defineProps<{ item: Stored<ItemRow> }>()
const notify = useNotify()

interface FieldValueRecord {
  fieldId: string
  value: unknown
  deleted?: boolean
}

const maps = useLive(
  async () => ((await localDb().synced('maps').toArray()) as unknown as MapRecord[]).filter((m) => !m.deleted),
  [] as MapRecord[],
)
const placements = useLive(
  () => placementsOf(localDb(), props.item.id),
  new Map<string, number>(),
  () => props.item.id,
)
const lookups = useLive(
  async () => {
    const db = localDb()
    const defs = ((await db.synced('field_definitions').toArray()) as unknown as FieldDefinitionRecord[]).filter(
      (d) => !d.deleted,
    )
    const values = (
      (await db.synced('field_values').where('itemId').equals(props.item.id).toArray()) as unknown as FieldValueRecord[]
    ).filter((v) => !v.deleted)
    const groups = (
      (await db.synced('item_shares').where('itemId').equals(props.item.id).toArray()) as unknown as ItemShareRow[]
    )
      .filter((s) => !s.deleted)
      .map((s) => s.groupId)
    return { defs, values, groups }
  },
  { defs: [] as FieldDefinitionRecord[], values: [] as FieldValueRecord[], groups: [] as string[] },
  () => props.item.id,
)

const rows = computed(() => {
  const { defs, values, groups } = lookups.value.value
  const defsByKey: Record<string, FieldDefinition> = {}
  const keyOf = new Map<string, string>()
  for (const d of defs) {
    defsByKey[d.key] = {
      id: d.id,
      key: d.key,
      label: d.label,
      type: d.type,
      config: d.config,
      isArchived: d.isArchived,
    }
    keyOf.set(d.id, d.key)
  }
  const mine: Record<string, unknown> = {}
  for (const v of values) {
    const key = keyOf.get(v.fieldId)
    if (key) mine[key] = v.value
  }
  const item = toMapRow<MapItemRow>(props.item)
  const out: { map: MapRecord; text: string; cards: number }[] = []
  for (const map of maps.value.value) {
    if (map.isTemplate) continue
    if (map.kind === 'canvas') {
      const cards = placements.value.value.get(map.id) ?? 0
      if (cards) out.push({ map, cards, text: cards === 1 ? 'Placed once' : `Placed ${cards}×` })
    } else if (map.matrix?.x && map.matrix.y) {
      const groupIds = map.matrix.filter?.groupIds ?? []
      const shared = !groupIds.length || groups.some((g) => groupIds.includes(g))
      if (!shared || !matrixCandidates([item], map.matrix.filter).length) continue
      const q = quadrantsOf(map.matrix, [item], defsByKey, new Map([[item.id, mine]]), () => shared).get(item.id)
      out.push({ map, cards: 0, text: q ? q.label : 'Not yet assessed' })
    }
  }
  return out.sort((a, b) => a.map.name.localeCompare(b.map.name))
})

// --- Add to map… ---
const adding = ref(false)
async function addTo(map: MapRecord) {
  try {
    await repo.transaction((tx) => addCardToMap(tx, map.id, props.item.id))
    adding.value = false
    notify.success(`Added to ${map.name}`)
  } catch (e) {
    notify.error(e, 'Not added')
  }
}
const info = computed(() => itemTypes[props.item.type])
</script>

<template>
  <section class="item-maps">
    <div class="actions">
      <Button
        label="Add to map…"
        icon="pi pi-plus"
        size="small"
        severity="secondary"
        outlined
        :disabled="item.deleted"
        @click="adding = true"
      />
    </div>
    <EmptyState
      v-if="maps.loaded.value && !rows.length"
      icon="pi pi-map"
      :title="`This ${info.label} is on no map yet.`"
      text="Place it on a canvas with Add to map…, or from a map’s library; matrix maps show it once it is assessed."
    />
    <ul v-else class="rows">
      <li v-for="row in rows" :key="row.map.id" :data-map="row.map.id">
        <i :class="KIND_ICONS[row.map.kind]" class="muted" aria-hidden="true" />
        <RouterLink
          :to="{ name: 'map', params: { id: row.map.id }, query: { focus: `item:${item.id}` } }"
          class="strong name"
        >
          {{ row.map.name }}
        </RouterLink>
        <Tag :value="KIND_LABELS[row.map.kind]" severity="secondary" />
        <span class="muted">{{ row.text }}</span>
      </li>
    </ul>
    <MapPickerDialog
      v-model:visible="adding"
      :placements="placements.value.value"
      hint="The card goes in the middle of the map; matrix maps place their items themselves."
      @pick="addTo"
    />
  </section>
</template>

<style scoped>
.item-maps {
  display: flex;
  flex-direction: column;
  gap: var(--pgpm-space-3);
}

.actions {
  display: flex;
  justify-content: flex-end;
}

.rows {
  list-style: none;
  margin: 0;
  padding: 0;
}

.rows li {
  display: flex;
  align-items: center;
  gap: var(--pgpm-space-2);
  padding: var(--pgpm-space-2) 0;
  border-bottom: 1px solid var(--pgpm-border);
}

.name {
  min-width: 0;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}
</style>

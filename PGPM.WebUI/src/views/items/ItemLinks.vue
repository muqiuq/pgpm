<script setup lang="ts">
import Button from 'primevue/button'
import Select from 'primevue/select'
import { computed, ref } from 'vue'
import EmptyState from '@/components/EmptyState.vue'
import ItemPicker from '@/components/items/ItemPicker.vue'
import StatusBadge from '@/components/items/StatusBadge.vue'
import { useNotify } from '@/composables/useNotify'
import { itemRoute, itemTypes } from '@/domain/itemTypes'
import { localDb } from '@/local/db'
import { addLink, relations } from '@/local/items'
import { useStatuses } from '@/local/lookups'
import { repo } from '@/local/repo'
import { allowsAccess, itemKey, type ItemLinkRow, type ItemRow, type LinkRelation, type Stored } from '@/local/rows'
import { useLive } from '@/local/useLive'

/**
 * An item's *Links* tab (docs/01 §4.7): its links to other items and theirs to it, by relation, both directions. A
 * link belongs to its source: adding or removing one needs *edit* on the source (an incoming relation – *has learning*
 * – is a link of the picked item). An item that isn't on this device (not shared with the user) is shown *restricted*.
 * The items contributing to this one are listed by status – a simple funnel.
 */
const props = defineProps<{ item: Stored<ItemRow>; canEdit: boolean }>()
const notify = useNotify()
const statuses = useStatuses()

interface Line {
  link: Stored<ItemLinkRow>
  other: Stored<ItemRow> | null
  outgoing: boolean
}

const lines = useLive(
  async (): Promise<Line[]> => {
    const db = localDb()
    const id = props.item.id
    const out = (await db.synced('item_links').where('fromItemId').equals(id).toArray()) as unknown as Stored<
      ItemLinkRow[]
    >
    const into = (await db.synced('item_links').where('toItemId').equals(id).toArray()) as unknown as Stored<
      ItemLinkRow[]
    >
    const result: Line[] = []
    for (const [list, outgoing] of [
      [out, true],
      [into, false],
    ] as const) {
      for (const link of list) {
        if (link.deleted) continue
        const otherId = outgoing ? link.toItemId : link.fromItemId
        const other = (await db.synced('items').get(otherId)) as Stored<ItemRow> | undefined
        result.push({ link, other: other && !other.deleted ? other : null, outgoing })
      }
    }
    return result
  },
  [] as Line[],
  () => props.item.id,
)

const groups = computed(() => {
  const order = relations.flatMap((r) => [`${r.value}:out`, `${r.value}:in`])
  const map = new Map<string, Line[]>()
  for (const line of lines.value.value) {
    const key = `${line.link.relation}:${line.outgoing ? 'out' : 'in'}`
    if (!map.has(key)) map.set(key, [])
    map.get(key)!.push(line)
  }
  return [...map.entries()]
    .sort((a, b) => order.indexOf(a[0]) - order.indexOf(b[0]))
    .map(([key, list]) => {
      const [relation, dir] = key.split(':') as [LinkRelation, 'out' | 'in']
      const r = relations.find((x) => x.value === relation)!
      const sorted = [...list].sort((a, b) => {
        // The funnel: contributing items by their status's order.
        const sa = a.other?.statusId ? (statuses.byId.value.get(a.other.statusId)?.sortOrder ?? 0) : 0
        const sb = b.other?.statusId ? (statuses.byId.value.get(b.other.statusId)?.sortOrder ?? 0) : 0
        return sa - sb || (a.other?.title ?? '').localeCompare(b.other?.title ?? '')
      })
      return { key, label: dir === 'out' ? r.label : r.inverse, lines: sorted }
    })
})

/** What *Add link* offers: each relation from this item, and the inverse ones (a link of the picked item). */
const choices = computed(() => [
  ...relations.map((r) => ({
    label: `This ${itemTypes[props.item.type].label} ${r.label} …`,
    value: `${r.value}:out`,
  })),
  ...relations
    .filter((r) => r.value !== 'related')
    .map((r) => ({ label: `This ${itemTypes[props.item.type].label} ${r.inverse} …`, value: `${r.value}:in` })),
])
const choice = ref('related:out')
const picking = ref(false)
const pickHeader = computed(() => choices.value.find((c) => c.value === choice.value)?.label ?? 'Link to')

async function picked(other: ItemRow) {
  const [relation, dir] = choice.value.split(':') as [LinkRelation, 'out' | 'in']
  const from = dir === 'out' ? props.item : other
  const to = dir === 'out' ? other : props.item
  if (!allowsAccess(from.access, 'edit')) {
    notify.error(new Error(`You can’t change ${itemKey(from)} ${from.title}: the link would be its own.`), 'Not linked')
    return
  }
  try {
    const added = await repo.transaction((tx) => addLink(tx, from.id, to.id, relation))
    if (!added) notify.info('Linked already')
  } catch (e) {
    notify.error(e, 'Not linked')
  }
}

function canRemove(line: Line) {
  return line.outgoing ? props.canEdit : allowsAccess(line.other?.access, 'edit')
}

async function remove(line: Line) {
  try {
    await repo.remove('item_links', line.link.id)
  } catch (e) {
    notify.error(e, 'Not removed')
  }
}

const excluded = computed(() => [props.item.id])
</script>

<template>
  <section class="panel" aria-labelledby="links-title">
    <div class="panel-header with-action">
      <span id="links-title">Links</span>
      <span v-if="canEdit" class="add">
        <Select
          v-model="choice"
          :options="choices"
          option-label="label"
          option-value="value"
          size="small"
          aria-label="Relation"
        />
        <Button label="Add link…" icon="pi pi-link" size="small" @click="picking = true" />
      </span>
    </div>
    <div class="panel-body">
      <EmptyState v-if="!lines.value.value.length" icon="pi pi-link" title="No links yet." />
      <div v-for="group in groups" :key="group.key" class="group">
        <h3 class="relation">{{ group.label }}</h3>
        <ul class="list">
          <li v-for="line in group.lines" :key="line.link.id" :data-testid="`link-${line.link.id}`">
            <template v-if="line.other">
              <i :class="itemTypes[line.other.type].icon" aria-hidden="true" />
              <span class="mono muted key">{{ itemKey(line.other) }}</span>
              <RouterLink :to="itemRoute(line.other)" class="ellipsis">{{ line.other.title }}</RouterLink>
              <StatusBadge
                v-if="line.other.statusId"
                :status="statuses.byId.value.get(line.other.statusId)"
                class="status"
              />
            </template>
            <template v-else>
              <i class="pi pi-lock muted" aria-hidden="true" />
              <span class="muted restricted">Restricted</span>
              <small class="muted">not shared with you</small>
            </template>
            <i
              v-if="line.link._local?.pending"
              v-tooltip.top="'Not synced yet'"
              class="pi pi-cloud-upload muted"
              aria-label="Not synced yet"
            />
            <Button
              v-if="canRemove(line)"
              icon="pi pi-times"
              size="small"
              text
              severity="secondary"
              class="remove"
              :aria-label="`Remove the link to ${line.other?.title ?? 'a restricted item'}`"
              @click="remove(line)"
            />
          </li>
        </ul>
      </div>
    </div>
    <ItemPicker v-model:visible="picking" :header="pickHeader" :exclude="excluded" @pick="picked" />
  </section>
</template>

<style scoped>
.with-action {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: var(--pgpm-space-2);
  flex-wrap: wrap;
}

.add {
  display: flex;
  gap: var(--pgpm-space-2);
  align-items: center;
}

.group + .group {
  margin-top: var(--pgpm-space-4);
}

.relation {
  margin: 0 0 var(--pgpm-space-1);
  font-size: 0.857rem;
  font-weight: 600;
  text-transform: uppercase;
  letter-spacing: 0.04em;
  color: var(--pgpm-muted);
}

.list {
  list-style: none;
  margin: 0;
  padding: 0;
}

.list li {
  display: flex;
  align-items: center;
  gap: var(--pgpm-space-2);
  min-height: 32px;
  border-bottom: 1px solid var(--pgpm-border);
}

.list li:last-child {
  border-bottom: none;
}

.key {
  flex: none;
}

.status {
  margin-left: var(--pgpm-space-2);
}

.remove {
  margin-left: auto;
}

.restricted {
  font-style: italic;
}
</style>

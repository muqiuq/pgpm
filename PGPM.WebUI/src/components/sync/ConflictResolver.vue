<script setup lang="ts">
import Button from 'primevue/button'
import InputText from 'primevue/inputtext'
import Tag from 'primevue/tag'
import Textarea from 'primevue/textarea'
import ToggleSwitch from 'primevue/toggleswitch'
import { computed, ref } from 'vue'
import { useRouter } from 'vue-router'
import EmptyState from '@/components/EmptyState.vue'
import RelativeTime from '@/components/RelativeTime.vue'
import { useActions } from '@/composables/useActions'
import { itemTypes } from '@/domain/itemTypes'
import { conflicts as actions } from '@/local/boot'
import type { ConflictEntry } from '@/local/conflicts'
import { localDb, type StoredRow } from '@/local/db'
import { fieldLabel, useOnline } from '@/local/lookups'
import { itemKey, type ItemRow } from '@/local/rows'
import { useLive } from '@/local/useLive'

/**
 * The conflict dialog's list (docs/03 §11.8, docs/01 §14.4): the objects whose changes the server held back, grouped
 * (map elements per map, phase 5); per field *mine* and *theirs* with who and when. Per object **Keep mine**, **Keep
 * theirs**, **Edit** (Markdown texts in the editor's conflict view); deleted by someone else → **Restore with my
 * changes** / **Discard**; changed by someone else while deleted here → **Delete anyway** / **Keep it**. For all:
 * **Keep mine for all**, **Keep theirs for all**.
 */
const props = defineProps<{ conflicts: ConflictEntry[] }>()
const emit = defineEmits<{ navigate: [] }>()
const router = useRouter()
const { run, busy, notify } = useActions()
const online = useOnline()

/** Markdown fields: edited in the editor's conflict view. */
const markdownFields = new Set(['description'])

const rows = useLive(
  async () => {
    const db = localDb()
    const found = new Map<string, StoredRow>()
    for (const c of props.conflicts) {
      const row = await db.synced(c.table).get(c.rowId)
      if (row) found.set(`${c.table}:${c.rowId}`, row)
      // The map a group of conflicts belongs to, for its heading.
      if (c.mapId && !found.has(`maps:${c.mapId}`)) {
        const map = await db.synced('maps').get(c.mapId)
        if (map) found.set(`maps:${c.mapId}`, map)
      }
    }
    return found
  },
  new Map<string, StoredRow>(),
  () => props.conflicts.map((c) => c.id).join(','),
)

const groups = computed(() => {
  const byMap = new Map<string, ConflictEntry[]>()
  for (const c of props.conflicts) {
    const key = c.mapId ?? ''
    byMap.set(key, [...(byMap.get(key) ?? []), c])
  }
  return [...byMap.entries()].map(([mapId, list]) => ({ mapId, list }))
})

function rowOf(c: ConflictEntry) {
  return rows.value.value.get(`${c.table}:${c.rowId}`)
}

function mapName(mapId: string): string {
  const name = rows.value.value.get(`maps:${mapId}`)?.name
  return typeof name === 'string' && name ? `Map ${name}` : 'Map'
}

function describe(c: ConflictEntry) {
  const row = rowOf(c)
  if (c.table === 'items' && row) {
    const item = row as unknown as ItemRow
    return { icon: itemTypes[item.type]?.icon ?? 'pi pi-file', key: itemKey(item), type: item.type }
  }
  const icons: Record<string, string> = {
    persons: 'pi pi-address-book',
    user_settings: 'pi pi-cog',
    field_values: 'pi pi-list-check',
    milestones: 'pi pi-flag',
    dod_items: 'pi pi-check-square',
    maps: 'pi pi-map',
    map_elements: 'pi pi-clone',
    map_layers: 'pi pi-clone',
  }
  return { icon: icons[c.table] ?? 'pi pi-file', key: null, type: undefined }
}

/** Why *Restore with my changes* can't be done now: offline, or without *manage* (the Trash's rule). */
function restoreHint(c: ConflictEntry): string | undefined {
  if (!online.value) return 'Restoring needs a connection'
  const row = rowOf(c) as { access?: unknown } | undefined
  if (c.table === 'items' && row?.access && row.access !== 'manage') return 'Restoring needs manage access'
  return undefined
}

function show(value: unknown): string {
  if (value === null || value === undefined || value === '') return '(empty)'
  if (typeof value === 'boolean') return value ? 'Yes' : 'No'
  if (typeof value === 'string') return value.length > 160 ? `${value.slice(0, 160)}…` : value
  return JSON.stringify(value)
}

const kindText: Record<ConflictEntry['kind'], string> = {
  field: 'Someone else changed the same thing.',
  deleted_by_other: 'Someone else deleted it while you changed it.',
  changed_since_delete: 'You deleted it while someone else changed it.',
}

// --- Edit ---------------------------------------------------------------------------------------------------------

const editing = ref<string | null>(null)
const edited = ref<Record<string, unknown>>({})

function startEdit(c: ConflictEntry) {
  const markdown = Object.keys(c.fields).find((k) => markdownFields.has(k))
  const type = describe(c).type
  if (markdown && c.table === 'items' && type) {
    emit('navigate')
    void router.push({ name: itemTypes[type].edit, params: { id: c.rowId }, query: { conflict: c.id } })
    return
  }
  editing.value = c.id
  edited.value = Object.fromEntries(Object.entries(c.fields).map(([k, f]) => [k, f.mine]))
}

async function saveEdit(c: ConflictEntry) {
  if (await done(() => actions.edit(c, edited.value), 'Your edit is saved')) editing.value = null
}

async function done(action: () => Promise<unknown>, success: string): Promise<boolean> {
  let ok = false
  await run(
    async () => {
      await action()
      ok = true
    },
    { success, failure: 'Not resolved' },
  )
  return ok
}

async function forAll(side: 'mine' | 'theirs') {
  const list = [...props.conflicts]
  await run(async () => {
    const failed = await (side === 'mine' ? actions.keepMineForAll(list) : actions.keepTheirsForAll(list))
    if (failed.length) {
      notify.error(failed[0]!.error, `${failed.length} not resolved`)
    } else {
      notify.success(side === 'mine' ? 'Your versions are kept' : 'Their versions are kept')
    }
  })
}
</script>

<template>
  <div class="conflicts">
    <EmptyState v-if="!conflicts.length" icon="pi pi-check-circle" title="No conflicts." />
    <template v-else>
      <section v-for="group in groups" :key="group.mapId" class="group">
        <h3 v-if="group.mapId" class="group-title">{{ mapName(group.mapId) }}</h3>
        <article v-for="c in group.list" :key="c.id" class="conflict panel" :data-testid="`conflict-${c.rowId}`">
          <header class="conflict-header">
            <i :class="describe(c).icon" aria-hidden="true" />
            <span v-if="describe(c).key" class="mono muted">{{ describe(c).key }}</span>
            <strong class="ellipsis">{{ c.title || rowOf(c)?.title || 'Untitled' }}</strong>
            <Tag
              v-if="c.kind !== 'field'"
              severity="warn"
              :value="c.kind === 'deleted_by_other' ? 'Deleted' : 'Changed'"
            />
          </header>
          <p class="muted kind">{{ kindText[c.kind] }}</p>
          <table v-if="Object.keys(c.fields).length" class="fields">
            <thead>
              <tr>
                <th scope="col">Field</th>
                <th scope="col">Yours</th>
                <th v-if="c.kind === 'field'" scope="col">Theirs</th>
              </tr>
            </thead>
            <tbody>
              <tr v-for="(f, key) in c.fields" :key="key">
                <th scope="row">{{ fieldLabel(c.table, String(key), describe(c).type) }}</th>
                <td>
                  <template v-if="editing === c.id">
                    <ToggleSwitch
                      v-if="typeof f.mine === 'boolean'"
                      :model-value="Boolean(edited[key])"
                      :aria-label="`Your ${fieldLabel(c.table, String(key), describe(c).type)}`"
                      @update:model-value="(v: boolean) => (edited[key] = v)"
                    />
                    <Textarea
                      v-else-if="typeof f.mine === 'string' && f.mine.length > 60"
                      :model-value="String(edited[key] ?? '')"
                      auto-resize
                      rows="3"
                      fluid
                      :aria-label="`Your ${fieldLabel(c.table, String(key), describe(c).type)}`"
                      @update:model-value="(v: string | undefined) => (edited[key] = v ?? '')"
                    />
                    <InputText
                      v-else
                      :model-value="String(edited[key] ?? '')"
                      size="small"
                      fluid
                      :aria-label="`Your ${fieldLabel(c.table, String(key), describe(c).type)}`"
                      @update:model-value="(v: string | undefined) => (edited[key] = v ?? '')"
                    />
                  </template>
                  <span v-else class="value">{{ show(f.mine) }}</span>
                </td>
                <td v-if="c.kind === 'field'">
                  <span class="value">{{ show(f.theirs) }}</span>
                  <small v-if="f.by || f.at" class="muted who">
                    {{ f.by ?? 'Someone' }}<template v-if="f.at">, <RelativeTime :value="f.at" /></template>
                  </small>
                </td>
              </tr>
            </tbody>
          </table>
          <div class="actions">
            <template v-if="c.kind === 'field'">
              <template v-if="editing === c.id">
                <Button label="Cancel" size="small" severity="secondary" text @click="editing = null" />
                <Button label="Save edit" size="small" icon="pi pi-check" :loading="busy" @click="saveEdit(c)" />
              </template>
              <template v-else>
                <Button label="Edit" size="small" severity="secondary" text icon="pi pi-pencil" @click="startEdit(c)" />
                <Button
                  label="Keep theirs"
                  size="small"
                  severity="secondary"
                  outlined
                  :disabled="busy"
                  @click="done(() => actions.keepTheirs(c), 'Their version is kept')"
                />
                <Button
                  label="Keep mine"
                  size="small"
                  :disabled="busy"
                  @click="done(() => actions.keepMine(c), 'Your version is kept')"
                />
              </template>
            </template>
            <template v-else-if="c.kind === 'deleted_by_other'">
              <Button
                label="Discard my changes"
                size="small"
                severity="secondary"
                outlined
                :disabled="busy"
                @click="done(() => actions.discard(c), 'Your changes are discarded')"
              />
              <Button
                v-tooltip.top="restoreHint(c)"
                label="Restore with my changes"
                size="small"
                :disabled="busy || !!restoreHint(c)"
                @click="done(() => actions.restoreWithMine(c), 'Restored with your changes')"
              />
            </template>
            <template v-else>
              <Button
                label="Keep it"
                size="small"
                severity="secondary"
                outlined
                :disabled="busy"
                @click="done(() => actions.keepIt(c), 'It is kept')"
              />
              <Button
                label="Delete anyway"
                size="small"
                severity="danger"
                :disabled="busy"
                @click="done(() => actions.deleteAnyway(c), 'Deleted')"
              />
            </template>
          </div>
        </article>
      </section>
      <footer class="all">
        <Button label="Keep theirs for all" severity="secondary" outlined :disabled="busy" @click="forAll('theirs')" />
        <Button label="Keep mine for all" :disabled="busy" @click="forAll('mine')" />
      </footer>
    </template>
  </div>
</template>

<style scoped>
.conflicts {
  display: flex;
  flex-direction: column;
  gap: var(--pgpm-space-3);
}

.group {
  display: flex;
  flex-direction: column;
  gap: var(--pgpm-space-2);
}

.group-title {
  margin: 0;
  font-size: 0.857rem;
  text-transform: uppercase;
  color: var(--pgpm-muted);
}

.conflict {
  padding: var(--pgpm-space-3);
  display: flex;
  flex-direction: column;
  gap: var(--pgpm-space-2);
}

.conflict-header {
  display: flex;
  align-items: center;
  gap: var(--pgpm-space-2);
  min-width: 0;
}

.kind {
  margin: 0;
}

.fields {
  width: 100%;
  border-collapse: collapse;
  table-layout: fixed;
}

.fields th,
.fields td {
  text-align: left;
  vertical-align: top;
  padding: var(--pgpm-space-1) var(--pgpm-space-2);
  border-top: 1px solid var(--pgpm-border);
}

.fields thead th {
  border-top: none;
  color: var(--pgpm-muted);
  font-weight: 500;
}

.fields th[scope='row'] {
  width: 8rem;
  font-weight: 500;
}

.value {
  white-space: pre-wrap;
  overflow-wrap: anywhere;
}

.who {
  display: block;
}

.actions,
.all {
  display: flex;
  justify-content: flex-end;
  flex-wrap: wrap;
  gap: var(--pgpm-space-2);
}

.all {
  border-top: 1px solid var(--pgpm-border);
  padding-top: var(--pgpm-space-3);
}
</style>

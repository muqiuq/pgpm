<script setup lang="ts">
import Message from 'primevue/message'
import ProgressSpinner from 'primevue/progressspinner'
import { computed, onBeforeUnmount, ref, watch } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import ItemPicker from '@/components/items/ItemPicker.vue'
import MarkdownEditor from '@/components/markdown/MarkdownEditor.vue'
import { provideMarkdownServices } from '@/components/markdown/services'
import type { EditConflict, ItemLink, MarkdownSave, Revision } from '@/components/markdown/types'
import { useNotify } from '@/composables/useNotify'
import { itemPath, itemTypes } from '@/domain/itemTypes'
import { appFiles, conflicts as actions } from '@/local/boot'
import type { ConflictEntry } from '@/local/conflicts'
import { localDb } from '@/local/db'
import { fieldReadOnly, setFieldValue } from '@/local/fields'
import { useMarkdownFiles } from '@/local/files'
import { useUserPersons } from '@/local/lookups'
import { repo } from '@/local/repo'
import { itemKey, type ItemRow } from '@/local/rows'
import { useItem } from './useItem'
import { useItemFields } from './useItemFields'

/**
 * Editing a Markdown field of an idea or project on its own page (docs/03 §18.2: `/<type>/:id/fields/:key/edit`, the
 * editor's mode `field`). Drafts under `items:<id>:field.<key>`; saving writes the field's value through the local
 * repository (offline too) – a patch `{value: {old: <text when editing began>, new}}` when the item has a value, else
 * an insert – so that someone else's change meanwhile becomes a conflict of this one field. When the synced text
 * changes while the editor is open it shows *Their changes* / *My changes*; from the conflict dialog
 * (`?conflict=<id>`) it opens in that view. Pasted files belong to the item.
 */
const props = defineProps<{ type: 'idea' | 'project'; id: string; fieldKey: string }>()
const route = useRoute()
const router = useRouter()
const notify = useNotify()
const people = useUserPersons()
const {
  item,
  loaded: itemLoaded,
  canEdit,
} = useItem(
  () => props.type,
  () => props.id,
)
const { catalogue, valueRows, loaded: fieldsLoaded } = useItemFields(() => props.id)
const files = useMarkdownFiles(() => props.id)
const info = computed(() => itemTypes[props.type])
const field = computed(() => catalogue.value.fieldByKey.get(props.fieldKey))
const section = computed(() => (field.value ? catalogue.value.sectionById.get(field.value.sectionId) : undefined))
const valueRow = computed(() => (field.value ? valueRows.value.get(field.value.id) : undefined))
const loaded = computed(() => itemLoaded.value && fieldsLoaded.value)
const readOnly = computed(() =>
  item.value && field.value ? fieldReadOnly(item.value, section.value, field.value) : null,
)

provideMarkdownServices({
  fileSink: { store: (file, kind, progress) => appFiles.sink(props.id).store(file, kind, progress) },
})

const picking = ref(false)
let resolvePick: ((link: ItemLink | null) => void) | null = null
function pickItem(): Promise<ItemLink | null> {
  resolvePick?.(null)
  picking.value = true
  return new Promise((resolve) => (resolvePick = resolve))
}
function picked(other: ItemRow) {
  resolvePick?.({ label: `${itemKey(other)} ${other.title}`, href: itemPath(other) })
  resolvePick = null
}
watch(picking, (open) => {
  if (!open && resolvePick) {
    setTimeout(() => {
      resolvePick?.(null)
      resolvePick = null
    })
  }
})

const editor = ref<InstanceType<typeof MarkdownEditor> | null>(null)
const content = ref<string | null>(null)
const baseRevision = ref<Revision | null>(null)
const conflict = ref<EditConflict | null>(null)
const saving = ref(false)
/** The text when editing began (null: no value); undefined: there was no value row then. */
let opened: string | null | undefined
let seen: string | null = null
const resolving = ref<ConflictEntry | null>(null)

const text = (value: unknown) => (typeof value === 'string' ? value : null)

const back = () => {
  const target =
    typeof route.query.return === 'string' && route.query.return.startsWith('/') ? route.query.return : null
  return target ? router.push(target) : router.push({ name: info.value.page, params: { id: props.id } })
}

watch(
  [loaded, item, field],
  async () => {
    if (!loaded.value || !item.value || !field.value || content.value !== null) return
    const row = valueRow.value
    opened = row ? text(row.value) : undefined
    seen = opened ?? null
    content.value = opened ?? ''
    baseRevision.value = row ? (row._local?.serverRev ?? row.rev ?? null) : null
    const conflictId = typeof route.query.conflict === 'string' ? route.query.conflict : null
    const entry = conflictId ? await localDb().conflicts.get(conflictId) : undefined
    const value = entry?.table === 'field_values' ? entry.fields.value : undefined
    if (entry && value) {
      resolving.value = entry
      content.value = String(value.mine ?? '')
      conflict.value = {
        by: value.by ?? 'Someone',
        at: value.at ?? entry.createdAt,
        theirs: String(value.theirs ?? ''),
        base: String(value.old ?? value.theirs ?? ''),
        revision: entry.baseRev ?? undefined,
      }
    }
  },
  { immediate: true },
)

// Someone else saved the text meanwhile (visible live): the editor shows both before anything is saved.
watch(
  () => text(valueRow.value?.value),
  (theirs) => {
    if (content.value === null || saving.value || theirs === seen) return
    seen = theirs
    const row = valueRow.value
    conflict.value = {
      by: people.nameOf(row?.updatedBy, 'Someone'),
      at: row?.updatedAt ?? new Date().toISOString(),
      theirs: theirs ?? '',
      revision: row ? (row._local?.serverRev ?? row.rev) : undefined,
    }
  },
)

onBeforeUnmount(() => resolvePick?.(null))

async function save(change: MarkdownSave) {
  if (!field.value) return
  saving.value = true
  try {
    const markdown = change.markdown === '' ? null : change.markdown
    seen = markdown
    if (resolving.value) {
      await actions.edit(resolving.value, { value: markdown })
      resolving.value = null
    } else {
      const old = opened === undefined ? undefined : change.base === '' && opened === null ? null : change.base
      const target = field.value
      await repo.transaction((tx) => setFieldValue(tx, props.id, target, markdown, { section: section.value, old }))
    }
    editor.value?.saved()
    await back()
  } catch (e) {
    notify.error(e, 'Not saved')
  } finally {
    saving.value = false
  }
}

async function takeTheirs(theirs: string) {
  seen = theirs === '' ? null : theirs
  opened = seen
  const row = valueRow.value
  baseRevision.value = row ? (row._local?.serverRev ?? row.rev ?? baseRevision.value) : baseRevision.value
  if (resolving.value) {
    try {
      await actions.keepTheirs(resolving.value)
      resolving.value = null
    } catch (e) {
      notify.error(e, 'Not resolved')
    }
  }
}
</script>

<template>
  <div v-if="!loaded" class="center"><ProgressSpinner style="width: 32px; height: 32px" /></div>
  <div v-else-if="!item || item.deleted" class="page">
    <Message severity="warn" :closable="false">This {{ info.label }} is not on this device (any more).</Message>
  </div>
  <div v-else-if="!field || field.type !== 'markdown'" class="page">
    <Message severity="warn" :closable="false">This {{ info.label }} has no Markdown field {{ fieldKey }}.</Message>
  </div>
  <div v-else-if="!canEdit || readOnly" class="page">
    <Message severity="secondary" :closable="false">{{ readOnly ?? `You can read this ${info.label}.` }}</Message>
  </div>
  <template v-else-if="content !== null">
    <MarkdownEditor
      ref="editor"
      v-model="content"
      v-model:conflict="conflict"
      mode="field"
      layout="page"
      :base-revision="baseRevision"
      :draft-key="`items:${id}:field.${fieldKey}`"
      :back-label="itemKey(item)"
      :saving="saving"
      :files="files"
      :pick-item="pickItem"
      :placeholder="field.helpText ?? `${field.label}…`"
      @save="save"
      @cancel="back"
      @take-theirs="takeTheirs"
    >
      <template #title>
        <span class="mono muted">{{ itemKey(item) }}</span> {{ item.title }} · {{ field.label }}
      </template>
    </MarkdownEditor>
    <ItemPicker v-model:visible="picking" header="Link to an item" :exclude="[id]" @pick="picked" />
  </template>
</template>

<style scoped>
.center {
  display: flex;
  justify-content: center;
  padding: var(--pgpm-space-6);
}
</style>

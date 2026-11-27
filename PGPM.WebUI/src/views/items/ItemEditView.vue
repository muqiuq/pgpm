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
import { useMarkdownFiles } from '@/local/files'
import { useOnline, useUserPersons } from '@/local/lookups'
import { announceEditing, useEditors } from '@/local/presence'
import { repo } from '@/local/repo'
import { itemKey, type ItemRow, type ItemType } from '@/local/rows'
import { useSession } from '@/stores/session'
import { useItem } from './useItem'

/**
 * Editing an item's description (notes: content) on its own page (docs/03 §18.2, §18.5, §18.6), for every type. Drafts
 * go to IndexedDB (key `items:<id>:description`). Saving is one patch `{description: {old: <text when editing began>,
 * new}}` through the local repository – offline too. Pasted and dropped files go through the offline file pipeline
 * (images `embedded`, other files attachments of the item, docs/03 §18.4); *Link to an item…* searches the local
 * items. When the synced text changes while the editor is open, it shows *Their changes* / *My changes* at once; from
 * the conflict dialog (`?conflict=<id>`) it opens in that view. *… is editing* online.
 */
const props = defineProps<{ type: ItemType; id: string }>()
const route = useRoute()
const router = useRouter()
const session = useSession()
const notify = useNotify()
const online = useOnline()
const people = useUserPersons()
const { item, loaded, canEdit } = useItem(
  () => props.type,
  () => props.id,
)
const files = useMarkdownFiles(() => props.id)
const editors = useEditors(() => props.id, {
  target: 'description',
  me: () => (session.user ? { id: session.user.id, name: session.user.displayName } : null),
})
const info = computed(() => itemTypes[props.type])

// The files of this text are this item's (docs/03 §18.4).
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
  // The editor escapes the label (linkText) itself.
  resolvePick?.({ label: `${itemKey(other)} ${other.title}`, href: itemPath(other) })
  resolvePick = null
}
watch(picking, (open) => {
  if (!open && resolvePick) {
    // Closed without a pick: after the pick event, if any.
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
/** The text the server had when editing began (null and '' differ on the server). */
let opened: string | null = null
/** The synced text this view knows of: a change of it under the editor is someone else's. */
let seen: string | null = null
/** A conflict of the sync this page resolves (from the conflict dialog). */
const resolving = ref<ConflictEntry | null>(null)

const back = () => {
  const target =
    typeof route.query.return === 'string' && route.query.return.startsWith('/') ? route.query.return : null
  return target ? router.push(target) : router.push({ name: info.value.page, params: { id: props.id } })
}

watch(
  [loaded, item],
  async () => {
    const row = item.value
    if (!loaded.value || !row || content.value !== null) return
    opened = row.description ?? null
    seen = opened
    content.value = opened ?? ''
    baseRevision.value = row._local?.serverRev ?? row.rev ?? null
    const conflictId = typeof route.query.conflict === 'string' ? route.query.conflict : null
    const entry = conflictId ? await localDb().conflicts.get(conflictId) : undefined
    const field = entry?.fields.description
    if (entry && field) {
      resolving.value = entry
      content.value = String(field.mine ?? '')
      conflict.value = {
        by: field.by ?? 'Someone',
        at: field.at ?? entry.createdAt,
        theirs: String(field.theirs ?? ''),
        base: String(field.old ?? field.theirs ?? ''),
        revision: entry.baseRev ?? undefined,
      }
    }
  },
  { immediate: true },
)

// Someone else saved the text meanwhile (visible live): the editor shows both before anything is saved.
watch(
  () => item.value?.description ?? null,
  (theirs) => {
    if (content.value === null || saving.value || theirs === seen) return
    seen = theirs
    const row = item.value!
    conflict.value = {
      by: people.nameOf(row.updatedBy, 'Someone'),
      at: row.updatedAt,
      theirs: theirs ?? '',
      revision: row._local?.serverRev ?? row.rev,
    }
  },
)

let stopAnnouncing: (() => void) | null = null
watch(
  [online, canEdit],
  ([isOnline, may]) => {
    if (isOnline && may && !stopAnnouncing) stopAnnouncing = announceEditing(props.id, 'description')
    if ((!isOnline || !may) && stopAnnouncing) {
      stopAnnouncing()
      stopAnnouncing = null
    }
  },
  { immediate: true },
)
onBeforeUnmount(() => {
  stopAnnouncing?.()
  resolvePick?.(null)
})

async function save(change: MarkdownSave) {
  saving.value = true
  try {
    const markdown = change.markdown === '' ? null : change.markdown
    seen = markdown
    if (resolving.value) {
      await actions.edit(resolving.value, { description: markdown })
      resolving.value = null
    } else {
      const old = change.base === '' && opened === null ? null : change.base
      await repo.patch('items', props.id, { description: markdown }, { old: { description: old } })
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
  baseRevision.value = item.value?._local?.serverRev ?? item.value?.rev ?? baseRevision.value
  if (resolving.value) {
    try {
      await actions.keepTheirs(resolving.value)
      resolving.value = null
    } catch (e) {
      notify.error(e, 'Not resolved')
    }
  }
}

const othersText = computed(() =>
  editors.value
    .map(
      (e) =>
        `${e.userName} is editing this (since ${new Date(e.since).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' })})`,
    )
    .join(' · '),
)
</script>

<template>
  <div v-if="!loaded" class="center"><ProgressSpinner style="width: 32px; height: 32px" /></div>
  <div v-else-if="!item || item.deleted" class="page">
    <Message severity="warn" :closable="false">This {{ info.label }} is not on this device (any more).</Message>
  </div>
  <div v-else-if="!canEdit" class="page">
    <Message severity="secondary" :closable="false">You can read this {{ info.label }}, not change it.</Message>
  </div>
  <template v-else-if="content !== null">
    <MarkdownEditor
      ref="editor"
      v-model="content"
      v-model:conflict="conflict"
      mode="page"
      layout="page"
      :base-revision="baseRevision"
      :draft-key="`items:${id}:description`"
      :back-label="itemKey(item)"
      :saving="saving"
      :files="files"
      :pick-item="pickItem"
      :placeholder="type === 'note' ? 'Write the note…' : `Describe the ${info.label}…`"
      @save="save"
      @cancel="back"
      @take-theirs="takeTheirs"
    >
      <template #title>
        <span class="mono muted">{{ itemKey(item) }}</span> {{ item.title }}
      </template>
      <template v-if="othersText" #others>
        <span class="others"><i class="pi pi-pencil" aria-hidden="true" /> {{ othersText }}</span>
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

.others {
  font-size: 0.857rem;
  color: var(--pgpm-muted);
}
</style>

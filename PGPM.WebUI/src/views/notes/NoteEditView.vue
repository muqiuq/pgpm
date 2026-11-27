<script setup lang="ts">
import Message from 'primevue/message'
import ProgressSpinner from 'primevue/progressspinner'
import { computed, onBeforeUnmount, ref, watch } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import MarkdownEditor from '@/components/markdown/MarkdownEditor.vue'
import type { EditConflict, MarkdownSave, Revision } from '@/components/markdown/types'
import { useNotify } from '@/composables/useNotify'
import { conflicts as actions } from '@/local/boot'
import type { ConflictEntry } from '@/local/conflicts'
import { localDb } from '@/local/db'
import { useOnline, useUserPersons } from '@/local/lookups'
import { announceEditing, useEditors } from '@/local/presence'
import { repo } from '@/local/repo'
import { itemKey } from '@/local/rows'
import { useSession } from '@/stores/session'
import { useNote } from './useNote'

/**
 * Editing a note's content (docs/03 §18.2, §18.5, §18.6): the Markdown editor on its own page. Drafts go to IndexedDB
 * (App.vue's DraftStore, key `items:<id>:description`). Saving is one patch `{description: {old: <text when editing
 * began>, new}}` through the local repository – offline too. When the synced text changes while the editor is open, the
 * editor shows *Their changes* / *My changes* at once; from the conflict dialog (`?conflict=<id>`) it opens in that
 * view. *… is editing* through the editing endpoints and the live channel (online only). Files come with phase 3.
 */
const props = defineProps<{ id: string }>()
const route = useRoute()
const router = useRouter()
const session = useSession()
const notify = useNotify()
const online = useOnline()
const people = useUserPersons()
const { note, loaded, canEdit } = useNote(() => props.id)
const editors = useEditors(() => props.id, {
  target: 'description',
  me: () => (session.user ? { id: session.user.id, name: session.user.displayName } : null),
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

const back = () => router.push({ name: 'note', params: { id: props.id } })

watch(
  [loaded, note],
  async () => {
    const row = note.value
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
  () => note.value?.description ?? null,
  (theirs) => {
    if (content.value === null || saving.value || theirs === seen) return
    seen = theirs
    const row = note.value!
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
onBeforeUnmount(() => stopAnnouncing?.())

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
  baseRevision.value = note.value?._local?.serverRev ?? note.value?.rev ?? baseRevision.value
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
  <div v-else-if="!note || note.deleted" class="page">
    <Message severity="warn" :closable="false">This note is not on this device (any more).</Message>
  </div>
  <div v-else-if="!canEdit" class="page">
    <Message severity="secondary" :closable="false">You can read this note, not change it.</Message>
  </div>
  <MarkdownEditor
    v-else-if="content !== null"
    ref="editor"
    v-model="content"
    v-model:conflict="conflict"
    mode="page"
    layout="page"
    :base-revision="baseRevision"
    :draft-key="`items:${id}:description`"
    :back-label="itemKey(note)"
    :saving="saving"
    placeholder="Write the note…"
    @save="save"
    @cancel="back"
    @take-theirs="takeTheirs"
  >
    <template #title>
      <span class="mono muted">{{ itemKey(note) }}</span> {{ note.title }}
    </template>
    <template v-if="othersText" #others>
      <span class="others"><i class="pi pi-pencil" aria-hidden="true" /> {{ othersText }}</span>
    </template>
  </MarkdownEditor>
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

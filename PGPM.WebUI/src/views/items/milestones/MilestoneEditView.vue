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
import { kindLabels } from '@/domain/milestones'
import { itemPath } from '@/domain/itemTypes'
import { appFiles, conflicts as actions } from '@/local/boot'
import type { ConflictEntry } from '@/local/conflicts'
import { localDb } from '@/local/db'
import { useMarkdownFiles } from '@/local/files'
import { useUserPersons } from '@/local/lookups'
import { repo } from '@/local/repo'
import { itemKey, type ItemRow, type MilestoneRow, type Stored } from '@/local/rows'
import { useLive } from '@/local/useLive'
import { useItem } from '@/views/items/useItem'

/**
 * Editing a milestone's or gate's description on its own page (docs/03 §18.2: `/projects/:id/milestones/:mid/edit`,
 * editor mode `field`). Drafts under `milestones:<id>:description`; saving is one patch `{description: {old: <text
 * when editing began>, new}}` through the local repository – offline too. Pasted files belong to the project (docs/03
 * §18.4). When the synced text changes while the editor is open, it shows *Their changes* / *My changes*; from the
 * conflict dialog (`?conflict=<id>`) it opens in that view. Back to the project's Milestones tab.
 */
const props = defineProps<{ id: string; milestoneId: string }>()
const route = useRoute()
const router = useRouter()
const notify = useNotify()
const people = useUserPersons()
const { item, loaded: itemLoaded, canEdit } = useItem('project', () => props.id)
const milestone = useLive(
  async () => {
    const row = (await localDb().synced('milestones').get(props.milestoneId)) as Stored<MilestoneRow> | undefined
    return row && !row.deleted && row.itemId === props.id ? row : undefined
  },
  undefined as Stored<MilestoneRow> | undefined,
  () => `${props.id}:${props.milestoneId}`,
)
const files = useMarkdownFiles(() => props.id)
const loaded = computed(() => itemLoaded.value && milestone.loaded.value)
const row = computed(() => milestone.value.value)

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
let opened: string | null = null
let seen: string | null = null
const resolving = ref<ConflictEntry | null>(null)

const back = () => {
  const target =
    typeof route.query.return === 'string' && route.query.return.startsWith('/') ? route.query.return : null
  return target ? router.push(target) : router.push({ name: 'project', params: { id: props.id, tab: 'milestones' } })
}

watch(
  [loaded, row],
  async () => {
    const m = row.value
    if (!loaded.value || !m || content.value !== null) return
    opened = m.description ?? null
    seen = opened
    content.value = opened ?? ''
    baseRevision.value = m._local?.serverRev ?? m.rev ?? null
    const conflictId = typeof route.query.conflict === 'string' ? route.query.conflict : null
    const entry = conflictId ? await localDb().conflicts.get(conflictId) : undefined
    const field = entry?.table === 'milestones' ? entry.fields.description : undefined
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
  () => row.value?.description ?? null,
  (theirs) => {
    if (content.value === null || saving.value || theirs === seen) return
    seen = theirs
    const m = row.value!
    conflict.value = {
      by: people.nameOf(m.updatedBy, 'Someone'),
      at: m.updatedAt,
      theirs: theirs ?? '',
      revision: m._local?.serverRev ?? m.rev,
    }
  },
)

onBeforeUnmount(() => resolvePick?.(null))

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
      await repo.patch('milestones', props.milestoneId, { description: markdown }, { old: { description: old } })
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
  baseRevision.value = row.value?._local?.serverRev ?? row.value?.rev ?? baseRevision.value
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
  <div v-else-if="!item || item.deleted || !row" class="page">
    <Message severity="warn" :closable="false">This milestone is not on this device (any more).</Message>
  </div>
  <div v-else-if="!canEdit" class="page">
    <Message severity="secondary" :closable="false">You can read this project, not change it.</Message>
  </div>
  <template v-else-if="content !== null">
    <MarkdownEditor
      ref="editor"
      v-model="content"
      v-model:conflict="conflict"
      mode="field"
      layout="page"
      :base-revision="baseRevision"
      :draft-key="`milestones:${milestoneId}:description`"
      :back-label="itemKey(item)"
      :saving="saving"
      :files="files"
      :pick-item="pickItem"
      :placeholder="`Describe the ${kindLabels[row.kind].toLowerCase()}…`"
      @save="save"
      @cancel="back"
      @take-theirs="takeTheirs"
    >
      <template #title>
        <span class="mono muted">{{ itemKey(item) }}</span> {{ kindLabels[row.kind] }} · {{ row.title }}
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

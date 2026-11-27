<script setup lang="ts">
/**
 * The Markdown editor (docs/03 §18; PNMTD docs/locations-knowledge-plan.md §5): one component for item descriptions,
 * milestone descriptions and Markdown fields. Crepe (Visual) and Monaco (Markdown) behind a switch, a <textarea> where
 * they can't run, uploads by paste, drop and buttons, drafts, and the conflict view of §18.5.
 *
 * ## Use
 *
 * Mount it once the text is loaded (v-if), on its own route (`layout="page"`, the default):
 *
 *     <MarkdownEditor
 *       ref="editor"
 *       v-model="content"                       the Markdown, as the active editor writes it
 *       mode="page"                             'page' | 'field': which remembered mode (§18.2)
 *       :base-revision="item.rev"               the revision the edit started from
 *       :draft-key="`items:${item.id}:description`"   drafts (§18.5, the DraftStore); null: none
 *       back-label="P-0042"
 *       :saving="saving"
 *       :files="files"                          the files' metadata (synced `files` rows), for chips and images
 *       details-summary="…"                     the folded details row; leave out for none
 *       v-model:details-open="detailsOpen"
 *       :details-dirty="metaChanged"            the details' own unsaved changes, for leaving
 *       :pick-item="pickItem"                   *Link to an item…*: resolves to { label, href } or null
 *       v-model:conflict="conflict"             set when the synced text changed under the editor (§18.5)
 *       @save="save" @cancel="router.back()" @dirty="dirty = $event" @take-theirs="…">
 *       <template #title>…</template>           the title (key and title, or the field's label)
 *       <template #status>…</template>          the status
 *       <template #others>…</template>          *Anna Muster is editing this description (since 10:42)* (§18.6)
 *       <template #details>…</template>
 *     </MarkdownEditor>
 *
 * Files, file URLs and drafts go through the services of services.ts (provideMarkdownServices): without a FileSink,
 * files are refused with a notice; without a resolver, file URLs are used as they are; without a DraftStore, drafts
 * are kept in localStorage.
 *
 * ## Props
 *
 * - `modelValue` (v-model): the Markdown. It changes only when the person edits: opening the visual
 *   editor, or switching back and forth, never rewrites it by itself.
 * - `mode`: 'page' or 'field'. The last mode is remembered per browser, apart for the two.
 * - `layout`: 'page' (EditingPage: the whole window, the header, the drawer as a rail, Ctrl/⌘ S, and
 *   leaving with changes asks) or 'inline' (a framed box of `height`, for a create dialog).
 * - `baseRevision`, `draftKey`: a draft is kept per key while editing; one from a crash is offered on
 *   opening (*Restore*, *Discard*). Restoring a draft made on another revision makes the save carry
 *   that revision and its base text, so that the sync sees the conflict rather than overwriting.
 * - `uploads` (default true): false refuses pasted and dropped files (read-only access).
 * - `saving`, `canSave`, `saveLabel`, `backLabel`, `placeholder`, `files`, `pickItem`, `detailsSummary`,
 *   `detailsDirty`, `height` (inline only).
 *
 * ## Events
 *
 * - `save` (MarkdownSave): from Save, Ctrl/⌘ S or *Save mine anyway*. Save it as one patch
 *   `{ field: { old: base, new: markdown } }`; `overwrite` says it came from *Save mine anyway*.
 * - `cancel`: Cancel or back, after the person agreed to drop their changes. Navigate away.
 * - `dirty` (boolean): unsaved changes, in the text or (`detailsDirty`) the details.
 * - `take-theirs` (string): the conflict's *Take theirs*. The editor already shows theirs, with nothing unsaved.
 * - `restored` (Draft): a draft was restored; the view may compare its base with the synced text.
 *
 * ## Methods (template ref)
 *
 * - `saved()`: call once the save is accepted (in the local repo), before navigating: the draft is
 *   cleared and the text counts as saved, so leaving doesn't ask.
 * - `focus()`, `insertMarkdown(text)`.
 *
 * ## The conflict: the view drives it
 *
 * When the synced field changes while the editor is open, or a push returned a conflict for it, the
 * view sets `conflict` ({ by, at, theirs, base?, revision? }). The editor shows *Their changes* and
 * *My changes* (diffs from the base) and offers *Save mine anyway*, which emits `save` with
 * `base: theirs` and `overwrite: true`, and *Take theirs*; *Keep editing* or closing sets `conflict`
 * to null.
 */
import Button from 'primevue/button'
import Dialog from 'primevue/dialog'
import Message from 'primevue/message'
import SelectButton from 'primevue/selectbutton'
import { computed, defineAsyncComponent, nextTick, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { useMedia } from '@/composables/useMedia'
import { useNotify } from '@/composables/useNotify'
import { usePreferences, type EditorMode } from '@/stores/preferences'
import EditingPage from './EditingPage.vue'
import { fileMarkdown, linkText } from './files'
import { rewriteSummary } from './lineDiff'
import MarkdownView from './MarkdownView.vue'
import { useDraftStore, useFileSink, type Draft } from './services'
import type { EditConflict, EditorModeKey, ItemLink, MarkdownFile, MarkdownSave, Revision } from './types'
import {
  chooseKind,
  UploadError,
  useUpload,
  type UploadHandler,
  type UploadIntent,
  type UploadResult,
} from './useUpload'

const props = withDefaults(
  defineProps<{
    mode: EditorModeKey
    layout?: 'page' | 'inline'
    baseRevision?: Revision | null
    draftKey?: string | null
    backLabel?: string
    saving?: boolean
    canSave?: boolean
    saveLabel?: string
    detailsSummary?: string
    detailsDirty?: boolean
    uploads?: boolean
    pickItem?: (() => Promise<ItemLink | null>) | null
    files?: readonly MarkdownFile[] | null
    placeholder?: string
    /** The inline layout's height. */
    height?: string
  }>(),
  {
    layout: 'page',
    baseRevision: null,
    draftKey: null,
    backLabel: 'Back',
    saving: false,
    canSave: true,
    saveLabel: 'Save',
    detailsSummary: undefined,
    detailsDirty: false,
    uploads: true,
    pickItem: null,
    files: null,
    placeholder: undefined,
    height: '28rem',
  },
)
const markdown = defineModel<string>({ required: true })
const conflict = defineModel<EditConflict | null>('conflict', { default: null })
const detailsOpen = defineModel<boolean>('detailsOpen', { default: false })
const emit = defineEmits<{
  save: [save: MarkdownSave]
  cancel: []
  dirty: [dirty: boolean]
  takeTheirs: [theirs: string]
  restored: [draft: Draft]
}>()

const preferences = usePreferences()
const notify = useNotify()
// Monaco isn't for phones (§5.3): narrow and touch, or very narrow.
const phone = useMedia('(max-width: 599px), (pointer: coarse) and (max-width: 959px)')
const wide = useMedia('(min-width: 960px)')

// --- the editors and the switch (§5.4) -----------------------------------------------------------

/** Why the textarea took over: an editor's chunk didn't load, or it didn't start. */
const failed = ref<string | null>(null)
function fail(what: 'visual' | 'code', error: unknown) {
  console.error(`The ${what} editor didn't start`, error)
  checking.value = false
  failed.value = 'The editor couldn’t start; editing as plain text.'
}

const VisualEditor = defineAsyncComponent({
  loader: () => import('./VisualEditor.vue'),
  onError: (error, _retry, giveUp) => {
    giveUp()
    fail('visual', error)
  },
})
const CodeEditor = defineAsyncComponent({
  loader: () => import('./CodeEditor.vue'),
  onError: (error, _retry, giveUp) => {
    giveUp()
    fail('code', error)
  },
})

const wanted = computed<EditorMode>({
  get: () => preferences.editorModes[props.mode],
  set: (mode) => (preferences.editorModes[props.mode] = mode),
})
type Surface = 'visual' | 'code' | 'textarea'
const surface = computed<Surface>(() =>
  failed.value ? 'textarea' : wanted.value === 'visual' ? 'visual' : phone.value ? 'textarea' : 'code',
)
const markdownMode = computed(() => surface.value !== 'visual')
const modeOptions = [
  { label: 'Visual', value: 'visual' },
  { label: 'Markdown', value: 'markdown' },
]

const visual = ref<{
  markdown(): string
  flush(): string
  focus(): void
  insertMarkdown(text: string): void
} | null>(null)
/** The visual editor has edits it hasn't written as Markdown yet (VisualEditor's `pending`). */
const visualPending = ref(false)

/**
 * The text as the active editor holds it now, for a save, a draft or a switch: v-model reaches the
 * view a moment later, and the visual editor writes its Markdown only after a pause in typing.
 */
function current(): string {
  if (surface.value === 'visual' && visual.value) {
    return visual.value.flush()
  }
  if (surface.value === 'code' && code.value) {
    return code.value.value()
  }
  if (surface.value === 'textarea' && textarea.value) {
    return textarea.value.value
  }
  return markdown.value
}
const code = ref<{
  value(): string
  focus(): void
  insertMarkdown(text: string): void
  revealLine(line: number): void
} | null>(null)
const textarea = ref<HTMLTextAreaElement | null>(null)

/** Markdown → Visual: the visual editor opens hidden first, and what it would write is compared (§5.4). */
const checking = ref(false)
const rewrite = ref<{ lines: number; example: { from: string; to: string } | null; text: string } | null>(null)
const rewriteDiff = ref(false)
const MonacoDiff = defineAsyncComponent(() => import('./MonacoDiff.vue'))

/**
 * Crepe has started. Until then the text is shown read-only in its place: what was typed into an
 * editor that isn't there yet would be lost.
 */
const visualReady = ref(false)
watch(
  () => surface.value === 'visual' || checking.value,
  (mounted) => {
    if (!mounted) {
      visualReady.value = false
    }
  },
)

function setMode(mode: EditorMode | null) {
  if (!mode || mode === wanted.value) {
    return
  }
  if (mode === 'markdown') {
    current()
    checking.value = false
    wanted.value = 'markdown'
    return
  }
  checking.value = true
}

function onVisualReady(written: string) {
  visualReady.value = true
  if (!checking.value) {
    return
  }
  const summary = rewriteSummary(markdown.value, written)
  if (summary.lines === 0) {
    checking.value = false
    wanted.value = 'visual'
    return
  }
  rewrite.value = { ...summary, text: written }
  rewriteDiff.value = false
}

function switchAnyway() {
  rewrite.value = null
  checking.value = false
  wanted.value = 'visual'
}

function stay() {
  rewrite.value = null
  checking.value = false
}

const previewOpen = ref(false)

// --- the text as it started, drafts (§5.7, docs/03 §18.5) ------------------------------------------

const pristine = ref(markdown.value)
const dirty = computed(() => markdown.value !== pristine.value || visualPending.value || props.detailsDirty)
watch(dirty, (value) => emit('dirty', value))

const drafts = useDraftStore()
const draftOffer = ref<Draft | null>(null)
/** The revision of a restored draft that began on another one; undefined: the base revision. */
const draftRevision = ref<Revision | null | undefined>(undefined)
/** The text such a draft began from; undefined: the text the editor opened with. */
const draftBase = ref<string | undefined>(undefined)
/** The stored draft has been read: until then nothing is written over it (the store may answer later). */
let draftsRead = false
let draftTimer: ReturnType<typeof setTimeout> | undefined

/** A draft store's call, fired and forgotten: a draft is a convenience, the editor goes on. */
function quietly(run: () => void | Promise<void>) {
  try {
    void Promise.resolve(run()).catch(() => undefined)
  } catch {
    // As above.
  }
}

function removeDraft() {
  clearTimeout(draftTimer)
  const key = props.draftKey
  if (key) {
    quietly(() => drafts.remove(key))
  }
}

function writeDraft() {
  const key = props.draftKey
  if (!key || draftOffer.value || !draftsRead) {
    return
  }
  const text = current()
  if (text === pristine.value) {
    quietly(() => drafts.remove(key))
    return
  }
  const draft: Draft = { revision: revision(), base: base(), text, savedAt: new Date().toISOString() }
  quietly(() => drafts.write(key, draft))
}

watch(markdown, () => {
  clearTimeout(draftTimer)
  draftTimer = setTimeout(writeDraft, 800)
})

function offerDraft(draft: Draft | null) {
  draftsRead = true
  if (draft && typeof draft.text === 'string' && draft.text !== markdown.value) {
    draftOffer.value = draft
  }
}

function readDraft() {
  const key = props.draftKey
  if (!key) {
    draftsRead = true
    return
  }
  try {
    const read = drafts.read(key)
    if (read instanceof Promise) {
      read.then(offerDraft, () => offerDraft(null))
    } else {
      offerDraft(read)
    }
  } catch {
    offerDraft(null)
  }
}

function restoreDraft() {
  const draft = draftOffer.value
  draftOffer.value = null
  if (!draft) {
    return
  }
  if (draft.revision !== props.baseRevision) {
    draftRevision.value = draft.revision
    draftBase.value = typeof draft.base === 'string' ? draft.base : undefined
  }
  markdown.value = draft.text
  emit('restored', draft)
}

function discardDraft() {
  draftOffer.value = null
  removeDraft()
}

const draftTime = computed(() => {
  const at = draftOffer.value ? new Date(draftOffer.value.savedAt) : null
  if (!at || Number.isNaN(at.getTime())) {
    return ''
  }
  const today = new Date().toDateString() === at.toDateString()
  return today
    ? at.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' })
    : at.toLocaleString([], { dateStyle: 'medium', timeStyle: 'short' })
})

// A new base revision (the view saved, or took theirs): the text counts as saved.
watch(
  () => props.baseRevision,
  () => saved(),
)

function revision(): Revision | null {
  return draftRevision.value !== undefined ? draftRevision.value : props.baseRevision
}

/** The text the edit started from: the patch's `old` (docs/03 §18.5). */
function base(): string {
  return draftBase.value ?? pristine.value
}

/** The save is accepted: no draft, nothing unsaved (leaving doesn't ask). */
function saved() {
  // What was saved; anything typed while the save was on its way stays unsaved.
  pristine.value = lastSave?.markdown ?? current()
  lastSave = null
  draftRevision.value = undefined
  draftBase.value = undefined
  draftOffer.value = null
  removeDraft()
}

/** Cancel or leaving, confirmed: the changes and their draft go. */
function discard() {
  removeDraft()
  visualPending.value = false
  pristine.value = current()
}

/** The tab is hidden or closed: the draft is written now, not after the pause (PLM's flushDraft, docs/03 §11.11). */
function flushDraft() {
  if (dirty.value || current() !== pristine.value) {
    clearTimeout(draftTimer)
    writeDraft()
  }
}

function onVisibilityChange() {
  if (document.visibilityState === 'hidden') {
    flushDraft()
  }
}

// --- saving and the conflict (§18.5) ------------------------------------------------------------

let lastSave: MarkdownSave | null = null

function save(flags: Partial<MarkdownSave> = {}) {
  if (!props.canSave || props.saving) {
    return
  }
  lastSave = {
    markdown: current(),
    base: base(),
    revision: revision(),
    overwrite: false,
    ...flags,
  }
  emit('save', lastSave)
}

const conflictView = ref<'theirs' | 'mine'>('theirs')
const conflictBase = computed(() => conflict.value?.base ?? base())
const conflictTime = computed(() =>
  conflict.value ? new Date(conflict.value.at).toLocaleString([], { dateStyle: 'medium', timeStyle: 'short' }) : '',
)

/** *Save mine anyway*: mine on top of theirs, the patch's `old` being their text. */
function saveMineAnyway() {
  const theirs = conflict.value
  conflict.value = null
  if (theirs) {
    save({ base: theirs.theirs, revision: theirs.revision ?? revision(), overwrite: true })
  }
}

/** *Take theirs*: their text in the editor, nothing unsaved, no draft. */
function takeTheirs() {
  const theirs = conflict.value
  conflict.value = null
  if (!theirs) {
    return
  }
  removeDraft()
  draftOffer.value = null
  draftRevision.value = undefined
  draftBase.value = undefined
  lastSave = null
  markdown.value = theirs.theirs
  pristine.value = theirs.theirs
  visualPending.value = false
  emit('takeTheirs', theirs.theirs)
}

// --- uploads (§5.5, docs/03 §18.4) -----------------------------------------------------------------

interface Notice {
  id: number
  severity: 'info' | 'warn'
  text: string
  action?: { label: string; run: () => void }
}
const notices = ref<Notice[]>([])
let noticeCount = 0
const uploader = useUpload(useFileSink())
const uploadedHere = ref<MarkdownFile[]>([])
// Without the view's list, chips show their link's text; with it, this session's uploads join it (the view's
// metadata, a pending upload's status among it, wins).
const previewFiles = computed<MarkdownFile[] | null>(() =>
  props.files ? [...uploadedHere.value, ...props.files] : null,
)
const inFlight = uploader.active

function addNotice(notice: Omit<Notice, 'id'>) {
  notices.value = [...notices.value.slice(-3), { ...notice, id: ++noticeCount }]
}

function dismiss(id: number) {
  notices.value = notices.value.filter((n) => n.id !== id)
}

const handleUpload: UploadHandler = async (file, intent, onProgress) => {
  if (!props.uploads) {
    addNotice({ severity: 'info', text: 'Files can’t be added here.' })
    return null
  }
  const kind = chooseKind(file, intent)
  let result: UploadResult | null
  try {
    result = await uploader.upload(file, kind, onProgress)
  } catch (error) {
    if (error instanceof UploadError && error.reason === 'no_storage') {
      if (!notices.value.some((n) => n.text === error.message)) {
        addNotice({ severity: 'warn', text: error.message })
      }
    } else if (error instanceof UploadError && error.attachInstead && kind === 'image') {
      addNotice({
        severity: 'warn',
        text: error.message,
        action: { label: 'Attach it as a file', run: () => void attachInstead(file) },
      })
    } else {
      notify.error(error, 'Not uploaded')
    }
    return null
  }
  if (!result) {
    return null
  }
  const uploaded = result.file
  uploadedHere.value = [
    ...uploadedHere.value,
    {
      id: uploaded.id,
      name: uploaded.name,
      size: uploaded.size,
      contentType: uploaded.contentType,
      kind: uploaded.kind,
    },
  ]
  if (result.notice && result.original) {
    const original = result.original
    addNotice({
      severity: 'info',
      text: result.notice,
      action: { label: 'Attach the original instead', run: () => void attachOriginal(uploaded.url, original) },
    })
  }
  return uploaded
}
const upload = computed<UploadHandler | null>(() => (props.uploads ? handleUpload : null))

async function attachInstead(file: File) {
  const uploaded = await handleUpload(file, 'attach')
  if (uploaded) {
    insertMarkdown(fileMarkdown(uploaded))
  }
}

/** *Attach the original instead* (§5.5): the recompressed image becomes a link to the original. */
async function attachOriginal(imageUrl: string, original: File) {
  const uploaded = await handleUpload(original, 'attach')
  if (!uploaded) {
    return
  }
  const escaped = imageUrl.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')
  const image = new RegExp(`!\\[[^\\]]*\\]\\(${escaped}(?:\\s+"[^"]*")?\\)`)
  if (image.test(markdown.value)) {
    markdown.value = markdown.value.replace(image, fileMarkdown(uploaded))
  } else {
    insertMarkdown(fileMarkdown(uploaded))
  }
}

function insertMarkdown(text: string) {
  if (surface.value === 'visual') {
    visual.value?.insertMarkdown(text)
  } else if (surface.value === 'code') {
    code.value?.insertMarkdown(text)
  } else {
    insertInTextarea(text)
  }
}

/** *Image* and *Attach file* in the Markdown modes (Crepe has its own in its top bar). */
function pickFiles(intent: UploadIntent) {
  const input = document.createElement('input')
  input.type = 'file'
  input.multiple = true
  input.accept = intent === 'image' ? 'image/png,image/jpeg,image/gif,image/webp' : ''
  input.onchange = () => {
    for (const file of Array.from(input.files ?? [])) {
      void handleUpload(file, intent).then((uploaded) => uploaded && insertMarkdown(fileMarkdown(uploaded)))
    }
  }
  input.click()
}

/** *Link to an item…* in the Markdown modes (Crepe has its own in its top bar): `[label](href)` at the cursor. */
async function linkItem() {
  const link = props.pickItem ? await props.pickItem() : null
  if (link) {
    insertMarkdown(`[${linkText(link.label)}](${link.href})`)
  }
}

// --- the textarea (§5.4) ------------------------------------------------------------------------

let textareaUploads = 0

function insertInTextarea(text: string) {
  const el = textarea.value
  const start = el?.selectionStart ?? markdown.value.length
  const end = el?.selectionEnd ?? start
  markdown.value = markdown.value.slice(0, start) + text + markdown.value.slice(end)
  void nextTick(() => {
    el?.focus()
    el?.setSelectionRange(start + text.length, start + text.length)
  })
}

function uploadIntoTextarea(files: File[]) {
  for (const file of files) {
    const placeholder = `[Uploading ${file.name.replace(/[[\]]/g, '')}… #${++textareaUploads}]()`
    insertInTextarea(placeholder)
    void handleUpload(file, 'paste').then((uploaded) => {
      markdown.value = markdown.value.replace(placeholder, uploaded ? fileMarkdown(uploaded) : '')
    })
  }
}

function onTextareaPaste(event: ClipboardEvent) {
  const files = Array.from(event.clipboardData?.files ?? [])
  if (files.length && !event.clipboardData?.getData('text/plain')) {
    event.preventDefault()
    uploadIntoTextarea(files)
  }
}

function onTextareaDrop(event: DragEvent) {
  const files = Array.from(event.dataTransfer?.files ?? [])
  if (files.length) {
    event.preventDefault()
    uploadIntoTextarea(files)
  }
}

function onTextareaDragOver(event: DragEvent) {
  if (event.dataTransfer?.types.includes('Files')) {
    event.preventDefault()
  }
}

// --- the inline layout ----------------------------------------------------------------------------

function onInlineKey(event: KeyboardEvent) {
  if ((event.metaKey || event.ctrlKey) && !event.altKey && event.key.toLowerCase() === 's') {
    event.preventDefault()
    save()
  }
}

onMounted(() => {
  readDraft()
  window.addEventListener('pagehide', flushDraft)
  document.addEventListener('visibilitychange', onVisibilityChange)
})

onBeforeUnmount(() => {
  clearTimeout(draftTimer)
  window.removeEventListener('pagehide', flushDraft)
  document.removeEventListener('visibilitychange', onVisibilityChange)
  // A draft still waiting to be written (a closed tab writes nothing more).
  if (dirty.value || current() !== pristine.value) {
    writeDraft()
  }
})

defineExpose({
  saved,
  focus: () =>
    surface.value === 'visual'
      ? visual.value?.focus()
      : surface.value === 'code'
        ? code.value?.focus()
        : textarea.value?.focus(),
  insertMarkdown,
})
</script>

<template>
  <component
    :is="layout === 'page' ? EditingPage : 'section'"
    v-bind="
      layout === 'page'
        ? { backLabel, saving, canSave, dirty, detailsSummary, saveLabel, detailsOpen }
        : { class: 'md-inline', style: { '--md-inline-height': height }, 'aria-label': 'Markdown editor' }
    "
    @update:details-open="(open: boolean) => (detailsOpen = open)"
    @save="save()"
    @cancel="emit('cancel')"
    @discard="discard"
    @keydown="layout === 'inline' ? onInlineKey($event) : undefined"
  >
    <template v-if="layout === 'page'" #title><slot name="title" /></template>
    <template v-if="layout === 'page' && $slots.status" #status><slot name="status" /></template>
    <template v-if="layout === 'page' && $slots.others" #others><slot name="others" /></template>
    <template v-if="layout === 'page'" #details><slot name="details" /></template>
    <template v-if="layout === 'page'" #mode>
      <div class="md-mode">
        <SelectButton
          v-if="!failed"
          :model-value="checking ? 'visual' : surface === 'visual' ? 'visual' : 'markdown'"
          :options="modeOptions"
          option-label="label"
          option-value="value"
          :allow-empty="false"
          size="small"
          aria-label="Editor"
          @update:model-value="setMode"
        />
        <template v-if="markdownMode">
          <Button
            v-tooltip.bottom="'Preview'"
            :label="wide ? 'Preview' : undefined"
            icon="pi pi-eye"
            size="small"
            :severity="previewOpen ? undefined : 'secondary'"
            :outlined="!previewOpen"
            :aria-pressed="previewOpen"
            aria-label="Preview"
            @click="previewOpen = !previewOpen"
          />
          <template v-if="uploads">
            <Button
              v-tooltip.bottom="'Image'"
              icon="pi pi-image"
              size="small"
              text
              severity="secondary"
              aria-label="Add an image"
              @click="pickFiles('image')"
            />
            <Button
              v-tooltip.bottom="'Attach file'"
              icon="pi pi-paperclip"
              size="small"
              text
              severity="secondary"
              aria-label="Attach a file"
              @click="pickFiles('attach')"
            />
          </template>
          <Button
            v-if="pickItem"
            v-tooltip.bottom="'Link to an item…'"
            icon="pi pi-link"
            size="small"
            text
            severity="secondary"
            aria-label="Link to an item…"
            @click="linkItem"
          />
        </template>
      </div>
    </template>
    <template v-if="layout === 'page'" #notices>
      <template v-if="draftOffer || failed || notices.length || inFlight.length">
        <Message v-if="draftOffer" severity="warn" class="md-notice">
          <span>You have unsaved changes from {{ draftTime }}.</span>
          <Button label="Restore" size="small" text @click="restoreDraft" />
          <Button label="Discard" size="small" text severity="secondary" @click="discardDraft" />
        </Message>
        <Message v-if="failed" severity="info" class="md-notice">{{ failed }}</Message>
        <Message v-for="n in notices" :key="n.id" :severity="n.severity" class="md-notice">
          <span>{{ n.text }}</span>
          <Button v-if="n.action" :label="n.action.label" size="small" text @click="(n.action.run(), dismiss(n.id))" />
          <Button
            icon="pi pi-times"
            size="small"
            text
            severity="secondary"
            aria-label="Dismiss"
            @click="dismiss(n.id)"
          />
        </Message>
        <div v-for="u in inFlight" :key="u.id" class="md-progress muted" role="status">
          <i class="pi pi-spin pi-spinner" aria-hidden="true" /> Uploading {{ u.name }}…
          {{ Math.round(u.progress * 100) }} %
        </div>
      </template>
    </template>

    <!-- The inline layout's own bar: the same controls. -->
    <div v-if="layout === 'inline'" class="md-inline-bar">
      <SelectButton
        v-if="!failed"
        :model-value="checking ? 'visual' : surface === 'visual' ? 'visual' : 'markdown'"
        :options="modeOptions"
        option-label="label"
        option-value="value"
        :allow-empty="false"
        size="small"
        aria-label="Editor"
        @update:model-value="setMode"
      />
      <template v-if="markdownMode">
        <Button
          label="Preview"
          icon="pi pi-eye"
          size="small"
          :severity="previewOpen ? undefined : 'secondary'"
          :outlined="!previewOpen"
          :aria-pressed="previewOpen"
          @click="previewOpen = !previewOpen"
        />
        <template v-if="uploads">
          <Button
            icon="pi pi-image"
            size="small"
            text
            severity="secondary"
            aria-label="Add an image"
            @click="pickFiles('image')"
          />
          <Button
            icon="pi pi-paperclip"
            size="small"
            text
            severity="secondary"
            aria-label="Attach a file"
            @click="pickFiles('attach')"
          />
        </template>
        <Button
          v-if="pickItem"
          icon="pi pi-link"
          size="small"
          text
          severity="secondary"
          aria-label="Link to an item…"
          @click="linkItem"
        />
      </template>
      <span class="spacer" />
      <span v-if="$slots.others" class="md-others"><slot name="others" /></span>
      <slot name="bar" />
    </div>
    <div
      v-if="layout === 'inline' && (draftOffer || failed || notices.length || inFlight.length)"
      class="md-inline-notices"
    >
      <Message v-if="draftOffer" severity="warn" class="md-notice">
        <span>You have unsaved changes from {{ draftTime }}.</span>
        <Button label="Restore" size="small" text @click="restoreDraft" />
        <Button label="Discard" size="small" text severity="secondary" @click="discardDraft" />
      </Message>
      <Message v-if="failed" severity="info" class="md-notice">{{ failed }}</Message>
      <Message v-for="n in notices" :key="n.id" :severity="n.severity" class="md-notice">
        <span>{{ n.text }}</span>
        <Button v-if="n.action" :label="n.action.label" size="small" text @click="(n.action.run(), dismiss(n.id))" />
        <Button icon="pi pi-times" size="small" text severity="secondary" aria-label="Dismiss" @click="dismiss(n.id)" />
      </Message>
      <div v-for="u in inFlight" :key="u.id" class="md-progress muted" role="status">
        <i class="pi pi-spin pi-spinner" aria-hidden="true" /> Uploading {{ u.name }}…
        {{ Math.round(u.progress * 100) }} %
      </div>
    </div>

    <div class="md-surface" :class="`surface-${surface}`">
      <div v-if="surface === 'visual' && !visualReady" class="md-starting">
        <p class="md-starting-note muted" role="status">
          <i class="pi pi-spin pi-spinner" aria-hidden="true" /> Starting the visual editor…
        </p>
        <textarea
          class="md-textarea"
          readonly
          :value="markdown"
          aria-label="Markdown, read-only until the editor has started"
        />
      </div>
      <VisualEditor
        v-if="surface === 'visual' || checking"
        v-show="surface === 'visual' && visualReady"
        ref="visual"
        v-model="markdown"
        :upload="upload"
        :pick-item="pickItem"
        :placeholder="placeholder"
        @ready="onVisualReady"
        @pending="(p: boolean) => (visualPending = p)"
        @failed="(e: unknown) => fail('visual', e)"
        @notice="(text: string) => addNotice({ severity: 'info', text })"
      />
      <CodeEditor
        v-if="surface === 'code'"
        ref="code"
        v-model="markdown"
        :upload="upload"
        :preview="previewOpen"
        :files="previewFiles"
        :placeholder="placeholder"
        :readonly="checking"
        @failed="(e: unknown) => fail('code', e)"
        @notice="(text: string) => addNotice({ severity: 'info', text })"
      />
      <div
        v-if="surface === 'textarea'"
        class="md-plain"
        :class="{ split: previewOpen && wide, 'preview-only': previewOpen && !wide }"
      >
        <textarea
          v-show="!(previewOpen && !wide)"
          ref="textarea"
          v-model="markdown"
          class="md-textarea"
          spellcheck="true"
          aria-label="Markdown"
          :readonly="checking"
          :placeholder="placeholder ?? 'Markdown'"
          @paste="onTextareaPaste"
          @drop="onTextareaDrop"
          @dragover="onTextareaDragOver"
        />
        <section v-if="previewOpen" class="md-plain-preview" aria-label="Preview">
          <MarkdownView :source="markdown" :files="previewFiles" />
        </section>
      </div>
    </div>
  </component>

  <Dialog
    :visible="!!rewrite"
    modal
    header="Switch to the visual editor?"
    :style="{ width: rewriteDiff ? 'min(64rem, 95vw)' : '34rem' }"
    @update:visible="(v: boolean) => !v && stay()"
  >
    <p v-if="rewrite">
      The visual editor will rewrite {{ rewrite.lines }} {{ rewrite.lines === 1 ? 'line' : 'lines'
      }}<template v-if="rewrite.example"
        >, for example <code>{{ rewrite.example.from || '(nothing)' }}</code> becomes
        <code>{{ rewrite.example.to || '(nothing)' }}</code></template
      >. Nothing changes until you edit there.
    </p>
    <MonacoDiff
      v-if="rewrite && rewriteDiff"
      :original="markdown"
      :modified="rewrite.text"
      original-label="Now"
      modified-label="As the visual editor writes it"
      height="24rem"
    />
    <template #footer>
      <Button :label="rewriteDiff ? 'Hide' : 'Show'" severity="secondary" text @click="rewriteDiff = !rewriteDiff" />
      <Button label="Stay" severity="secondary" outlined @click="stay" />
      <Button label="Switch anyway" @click="switchAnyway" />
    </template>
  </Dialog>

  <Dialog
    :visible="!!conflict"
    modal
    header="Changed in between"
    :style="{ width: 'min(64rem, 95vw)' }"
    @update:visible="(v: boolean) => !v && (conflict = null)"
  >
    <template v-if="conflict">
      <p>
        <strong>{{ conflict.by }}</strong> changed this text at {{ conflictTime }}, while you were editing. Your changes
        aren’t saved yet.
      </p>
      <SelectButton
        v-model="conflictView"
        :options="[
          { label: 'Their changes', value: 'theirs' },
          { label: 'My changes', value: 'mine' },
        ]"
        option-label="label"
        option-value="value"
        :allow-empty="false"
        size="small"
        aria-label="Changes to show"
      />
      <MonacoDiff
        class="md-dialog-diff"
        :original="conflictBase"
        :modified="conflictView === 'theirs' ? conflict.theirs : markdown"
        original-label="When you started"
        :modified-label="conflictView === 'theirs' ? `${conflict.by}'s version` : 'Yours'"
        height="24rem"
      />
      <p class="muted">
        Saving yours anyway replaces theirs; theirs stays in the history. Taking theirs drops your changes.
      </p>
    </template>
    <template #footer>
      <Button label="Keep editing" severity="secondary" outlined @click="conflict = null" />
      <Button label="Take theirs" severity="secondary" @click="takeTheirs" />
      <Button label="Save mine anyway" severity="warn" @click="saveMineAnyway" />
    </template>
  </Dialog>
</template>

<style scoped>
.md-mode {
  display: flex;
  align-items: center;
  gap: var(--pgpm-space-1);
}

.md-notice :deep(.p-message-text) {
  display: flex;
  align-items: center;
  flex-wrap: wrap;
  gap: var(--pgpm-space-1) var(--pgpm-space-2);
}

.md-progress {
  font-size: 0.9286rem;
  padding: 0 var(--pgpm-space-1);
}

.md-surface {
  height: 100%;
  min-height: 0;
}

.md-plain {
  display: flex;
  height: 100%;
}

.md-textarea {
  flex: 1 1 0;
  min-width: 0;
  height: 100%;
  padding: var(--pgpm-space-3) var(--pgpm-space-4);
  border: none;
  outline: none;
  resize: none;
  background: var(--pgpm-panel);
  color: var(--pgpm-text);
  font-family: var(--pgpm-mono);
  font-size: 0.9286rem;
  line-height: 1.55;
  tab-size: 4;
}

.md-textarea:focus-visible {
  box-shadow: inset 0 0 0 2px var(--p-primary-color);
}

.md-starting {
  display: flex;
  flex-direction: column;
  height: 100%;
}

.md-starting-note {
  margin: 0;
  padding: var(--pgpm-space-1) var(--pgpm-space-4);
  font-size: 0.9286rem;
}

.md-starting .md-textarea {
  flex: 1 1 auto;
  height: auto;
  color: var(--pgpm-muted);
}

.md-plain-preview {
  flex: 1 1 0;
  min-width: 0;
  height: 100%;
  overflow: auto;
  padding: var(--pgpm-space-4) var(--pgpm-space-6);
  border-left: 1px solid var(--pgpm-border);
}

.preview-only .md-plain-preview {
  border-left: none;
}

/* The inline layout: a framed box in a page or a dialog. */
.md-inline {
  display: flex;
  flex-direction: column;
  border: 1px solid var(--pgpm-border);
  border-radius: var(--p-border-radius-md);
  background: var(--pgpm-panel);
  overflow: hidden;
}

.md-inline .md-surface {
  height: var(--md-inline-height);
}

.md-inline-bar {
  display: flex;
  align-items: center;
  flex-wrap: wrap;
  gap: var(--pgpm-space-1);
  padding: var(--pgpm-space-1) var(--pgpm-space-2);
  border-bottom: 1px solid var(--pgpm-border);
}

.md-inline-bar .spacer {
  flex: 1;
}

.md-others {
  color: var(--pgpm-muted);
  font-size: 0.9286rem;
}

.md-inline-notices {
  display: flex;
  flex-direction: column;
  gap: var(--pgpm-space-1);
  padding: var(--pgpm-space-2) var(--pgpm-space-2) 0;
}

.md-dialog-diff {
  margin-top: var(--pgpm-space-2);
}
</style>

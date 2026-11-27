<script setup lang="ts">
import Button from 'primevue/button'
import Tag from 'primevue/tag'
import { useConfirm } from 'primevue/useconfirm'
import { computed, onBeforeUnmount, onMounted, ref } from 'vue'
import { ApiError } from '@/api/client'
import InlineField from '@/components/InlineField.vue'
import { fileIcon, fileSize } from '@/components/markdown/files'
import { imageTypes, UploadError } from '@/components/markdown/useUpload'
import RelativeTime from '@/components/RelativeTime.vue'
import { useFilePipeline } from '@/composables/useFileUrl'
import { useNotify } from '@/composables/useNotify'
import { NotDownloaded, useFileRows, useFileStates, type FileOwner, type FileState } from '@/local/files'
import { useUserPersons } from '@/local/lookups'
import type { FileRow, Stored } from '@/local/rows'
import FilePreviewDialog from './FilePreviewDialog.vue'

/**
 * An item's (or map's) attachments (docs/01 §4.5): name, size, who added it and when; download, preview of images and
 * PDFs, rename, delete. Files come by the picker, by drag and drop onto the panel or by pasting (while no text field
 * has the focus), several at once. Everything works offline: a new file is stored on this device and shown as
 * *Pending upload* until the sync has sent it; a file never downloaded is *Not available offline*. Read-only without
 * edit access.
 */
const props = defineProps<{ itemId?: string; mapId?: string; canEdit: boolean }>()

const files = useFilePipeline()
const notify = useNotify()
const confirm = useConfirm()
const people = useUserPersons()

const owner = computed<FileOwner>(() => (props.itemId ? { itemId: props.itemId } : { mapId: props.mapId ?? '' }))
const rows = useFileRows(owner, 'attachment', () => files.db())
const states = useFileStates(files, rows)

const online = ref(files.online())
const updateOnline = () => (online.value = files.online())
const picker = ref<HTMLInputElement | null>(null)
const dragging = ref(false)
const adding = ref(false)

const stateOf = (row: Stored<FileRow>): FileState => states.value.get(row.id) ?? { kind: 'remote' }

/** The bytes can be had now: on this device, or downloadable. */
function canOpen(row: Stored<FileRow>): boolean {
  const state = stateOf(row)
  if (state.kind === 'remote') return online.value
  return state.kind !== 'elsewhere' && state.kind !== 'downloading'
}

const isImage = (row: FileRow) => imageTypes.includes(row.contentType)
const isPdf = (row: FileRow) => row.contentType === 'application/pdf'

async function add(list: File[]) {
  if (!props.canEdit || !list.length) return
  adding.value = true
  let added = 0
  try {
    for (const file of list) {
      try {
        await files.attachFile(owner.value, file, 'attachment')
        added++
      } catch (e) {
        notify.error(e, `${file.name || 'File'} not attached`)
      }
    }
  } finally {
    adding.value = false
  }
  if (added && !files.online()) {
    notify.info(added === 1 ? 'File attached' : `${added} files attached`, 'They upload when the connection is back.')
  }
}

function onPick(event: Event) {
  const input = event.target as HTMLInputElement
  const list = Array.from(input.files ?? [])
  input.value = ''
  void add(list)
}

function onDrop(event: DragEvent) {
  dragging.value = false
  void add(Array.from(event.dataTransfer?.files ?? []))
}

function onDragOver(event: DragEvent) {
  if (!props.canEdit || !event.dataTransfer?.types.includes('Files')) return
  event.dataTransfer.dropEffect = 'copy'
  dragging.value = true
}

/** Files on the clipboard, while no text field (or editor) has the focus. */
function onPaste(event: ClipboardEvent) {
  if (!props.canEdit) return
  const active = document.activeElement as HTMLElement | null
  if (active?.closest('input, textarea, select, [contenteditable="true"], .cm-editor, .monaco-editor')) return
  const list = Array.from(event.clipboardData?.files ?? [])
  if (!list.length) return
  event.preventDefault()
  void add(list)
}

onMounted(() => {
  window.addEventListener('paste', onPaste)
  window.addEventListener('online', updateOnline)
  window.addEventListener('offline', updateOnline)
})
onBeforeUnmount(() => {
  window.removeEventListener('paste', onPaste)
  window.removeEventListener('online', updateOnline)
  window.removeEventListener('offline', updateOnline)
})

async function bytes(row: Stored<FileRow>): Promise<Blob | null> {
  try {
    return await files.fileBlob(row.id)
  } catch (e) {
    updateOnline()
    notify.error(
      e instanceof NotDownloaded ? new Error(`${row.fileName}: ${e.message}`) : e,
      e instanceof NotDownloaded ? 'Not available offline' : 'Not downloaded',
    )
    return null
  }
}

async function download(row: Stored<FileRow>) {
  const blob = await bytes(row)
  if (!blob) return
  const url = URL.createObjectURL(blob)
  const link = document.createElement('a')
  link.href = url
  link.download = row.fileName
  document.body.appendChild(link)
  link.click()
  link.remove()
  setTimeout(() => URL.revokeObjectURL(url), 60_000)
}

const previewing = ref(false)
const previewed = ref<Stored<FileRow> | null>(null)
const previewUrl = ref<string | null>(null)

async function preview(row: Stored<FileRow>) {
  if (isPdf(row)) {
    const blob = await bytes(row)
    if (!blob) return
    const url = URL.createObjectURL(blob)
    window.open(url, '_blank', 'noopener')
    setTimeout(() => URL.revokeObjectURL(url), 5 * 60_000)
    return
  }
  previewed.value = row
  previewUrl.value = await files.fileUrl(row.id)
  previewing.value = true
}

async function rename(row: Stored<FileRow>, value: string | number | boolean | null) {
  try {
    await files.rename(row.id, String(value ?? ''))
  } catch (e) {
    if (e instanceof UploadError) {
      throw new ApiError(400, { code: 'validation_failed', errors: { fileName: [e.message] } })
    }
    throw e
  }
}

function remove(row: Stored<FileRow>) {
  confirm.require({
    header: `Delete ${row.fileName}?`,
    message: 'It is removed from the item for everyone who can see it.',
    icon: 'pi pi-exclamation-triangle',
    acceptProps: { label: 'Delete', severity: 'danger' },
    rejectProps: { label: 'Cancel', severity: 'secondary', text: true },
    accept: async () => {
      try {
        await files.remove(row.id)
      } catch (e) {
        notify.error(e, 'Not deleted')
      }
    },
  })
}

async function retry(row: Stored<FileRow>) {
  try {
    await files.retry(row.id)
  } catch (e) {
    notify.error(e, 'Not uploaded')
  }
}
</script>

<template>
  <section
    class="panel attachments"
    :class="{ dragging }"
    aria-labelledby="attachments-title"
    data-testid="attachments"
    @dragover.prevent="onDragOver"
    @dragleave="dragging = false"
    @drop.prevent="onDrop"
  >
    <div class="panel-header with-action">
      <span id="attachments-title">
        Attachments<span v-if="rows.length" class="muted count"> ({{ rows.length }})</span>
      </span>
      <template v-if="canEdit">
        <Button label="Add files" icon="pi pi-paperclip" size="small" text :loading="adding" @click="picker?.click()" />
        <input
          ref="picker"
          type="file"
          multiple
          class="picker"
          aria-label="Add files"
          data-testid="attachments-input"
          @change="onPick"
        />
      </template>
    </div>
    <div class="panel-body">
      <p v-if="!rows.length" class="muted empty">
        {{ canEdit ? 'No attachments yet. Add files, drop them here or paste them.' : 'No attachments.' }}
      </p>
      <ul v-else class="files">
        <li v-for="row in rows" :key="row.id" :data-testid="`attachment-${row.id}`">
          <i :class="fileIcon(row.fileName, row.contentType)" class="icon" aria-hidden="true" />
          <div class="main">
            <InlineField
              v-if="canEdit"
              :value="row.fileName"
              :save="(value) => rename(row, value)"
              label="File name"
              field="fileName"
            />
            <span v-else class="name ellipsis">{{ row.fileName }}</span>
            <span class="meta muted">
              {{ fileSize(row.sizeBytes) }} · {{ people.nameOf(row.createdBy, '–') }} ·
              <RelativeTime :value="row.createdAt" />
            </span>
          </div>
          <span class="state">
            <Tag
              v-if="stateOf(row).kind === 'pending'"
              v-tooltip.top="'Stored on this device; it uploads when online'"
              icon="pi pi-cloud-upload"
              value="Pending upload"
              severity="secondary"
            />
            <Tag
              v-else-if="stateOf(row).kind === 'uploading'"
              icon="pi pi-spin pi-spinner"
              :value="`Uploading ${Math.round(((stateOf(row) as { progress: number }).progress ?? 0) * 100)} %`"
              severity="info"
            />
            <template v-else-if="stateOf(row).kind === 'failed'">
              <Tag
                v-tooltip.top="(stateOf(row) as { error: string }).error"
                icon="pi pi-exclamation-circle"
                value="Failed"
                severity="danger"
              />
              <Button
                v-if="canEdit"
                label="Retry"
                size="small"
                text
                :aria-label="`Retry the upload of ${row.fileName}`"
                @click="retry(row)"
              />
            </template>
            <Tag
              v-else-if="stateOf(row).kind === 'elsewhere'"
              v-tooltip.top="'It is still on the device it was added on'"
              value="Not uploaded yet"
              severity="secondary"
            />
            <Tag v-else-if="stateOf(row).kind === 'downloading'" icon="pi pi-spin pi-spinner" value="Downloading…" />
            <Tag v-else-if="stateOf(row).kind === 'remote' && !online" value="Not available offline" severity="warn" />
          </span>
          <span class="actions">
            <Button
              v-if="isImage(row) || isPdf(row)"
              v-tooltip.top="'Preview'"
              icon="pi pi-eye"
              text
              rounded
              size="small"
              severity="secondary"
              :disabled="!canOpen(row)"
              :aria-label="`Preview ${row.fileName}`"
              @click="preview(row)"
            />
            <Button
              v-tooltip.top="'Download'"
              icon="pi pi-download"
              text
              rounded
              size="small"
              severity="secondary"
              :disabled="!canOpen(row)"
              :aria-label="`Download ${row.fileName}`"
              @click="download(row)"
            />
            <Button
              v-if="canEdit"
              v-tooltip.top="'Delete'"
              icon="pi pi-trash"
              text
              rounded
              size="small"
              severity="danger"
              :aria-label="`Delete ${row.fileName}`"
              @click="remove(row)"
            />
          </span>
        </li>
      </ul>
    </div>
    <FilePreviewDialog
      v-model:visible="previewing"
      :name="previewed?.fileName ?? ''"
      :url="previewUrl"
      :size="previewed?.sizeBytes"
      @download="previewed && download(previewed)"
    />
  </section>
</template>

<style scoped>
.with-action {
  display: flex;
  align-items: center;
  justify-content: space-between;
}

.attachments.dragging {
  outline: 2px dashed var(--p-primary-color);
  outline-offset: -2px;
}

.picker {
  display: none;
}

.empty {
  margin: 0;
}

.files {
  list-style: none;
  margin: 0;
  padding: 0;
}

.files li {
  display: flex;
  align-items: center;
  gap: var(--pgpm-space-2);
  padding: var(--pgpm-space-1) 0;
  border-bottom: 1px solid var(--pgpm-border);
  flex-wrap: wrap;
}

.files li:last-child {
  border-bottom: none;
}

.icon {
  color: var(--pgpm-muted);
}

.main {
  display: flex;
  flex-direction: column;
  flex: 1 1 12rem;
  min-width: 0;
}

.meta {
  font-size: 0.857rem;
}

.state {
  display: inline-flex;
  align-items: center;
  gap: var(--pgpm-space-1);
}

.actions {
  display: inline-flex;
  margin-left: auto;
}

@media (max-width: 599px) {
  .actions {
    margin-left: 0;
  }
}
</style>

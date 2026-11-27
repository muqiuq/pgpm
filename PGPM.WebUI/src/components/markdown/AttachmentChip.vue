<script setup lang="ts">
import { computed, onBeforeUnmount, ref, watch } from 'vue'
import { fileIcon, fileSize } from './files'
import { useFileUrlResolver } from './services'
import type { MarkdownFile } from './types'

/**
 * A link to an attachment in a text (PNMTD docs/locations-knowledge-plan.md §5.6, docs/03 §18.4): an icon by type,
 * the name and the size; a click downloads, from where the provided resolver has it (a blob: URL of this device's
 * store, else the URL itself). Without the file's metadata it shows the link's text only; a file that is gone shows
 * as removed, one on this device only as *Pending upload*, one that can't be had as *Not available offline*.
 */
const props = defineProps<{
  href: string
  /** The link's text in the Markdown. */
  name: string
  /** The file's metadata (the synced `files` row); undefined while unknown. */
  file?: MarkdownFile | null
  /** Known to be gone: not among the text's files. */
  removed?: boolean
}>()

const resolve = useFileUrlResolver()
/** Where it downloads from; undefined while the resolver looks, null where it can't be had. */
const target = ref<string | null | undefined>(resolve ? undefined : props.href)
let generation = 0

watch(
  () => props.href,
  (href) => {
    if (!resolve) {
      target.value = href
      return
    }
    const mine = ++generation
    target.value = undefined
    resolve(href).then(
      (url) => mine === generation && (target.value = url),
      () => mine === generation && (target.value = null),
    )
  },
  { immediate: !!resolve },
)

onBeforeUnmount(() => generation++)

const shownName = computed(() => props.name.trim() || props.file?.name || 'Attachment')
const icon = computed(() => fileIcon(props.file?.name ?? props.name, props.file?.contentType))
const gone = computed(() => props.removed || props.file?.deleted === true)
const pending = computed(() => props.file?.status === 'pending')
const label = computed(
  () =>
    `Download ${shownName.value}${props.file ? `, ${fileSize(props.file.size)}` : ''}${pending.value ? ', not uploaded yet' : ''}`,
)
</script>

<template>
  <span v-if="gone" class="attachment-chip removed" :title="`${shownName}: removed`">
    <i class="pi pi-ban" aria-hidden="true" />
    <span class="name">{{ shownName }}</span>
    <span class="meta">File removed</span>
  </span>
  <span v-else-if="target === null" class="attachment-chip unavailable" :title="`${shownName}: not available offline`">
    <i :class="icon" aria-hidden="true" />
    <span class="name">{{ shownName }}</span>
    <span class="meta">Not available offline</span>
  </span>
  <a
    v-else
    class="attachment-chip"
    :class="{ pending, resolving: target === undefined }"
    :href="target ?? href"
    :download="file?.name ?? shownName"
    :title="label"
    :aria-label="label"
    :aria-busy="target === undefined || undefined"
  >
    <i :class="icon" aria-hidden="true" />
    <span class="name">{{ shownName }}</span>
    <span v-if="file" class="meta">{{ fileSize(file.size) }}</span>
    <span v-if="pending" class="meta pending-note"
      ><i class="pi pi-cloud-upload" aria-hidden="true" /> Pending upload</span
    >
    <i class="pi pi-download download" aria-hidden="true" />
  </a>
</template>

<style scoped>
.attachment-chip {
  display: inline-flex;
  align-items: center;
  gap: var(--pgpm-space-1);
  max-width: 100%;
  padding: 1px var(--pgpm-space-2);
  border: 1px solid var(--pgpm-border);
  border-radius: 999px;
  background: var(--pgpm-bg);
  color: var(--pgpm-text);
  vertical-align: baseline;
  line-height: 1.6;
}

a.attachment-chip:hover {
  border-color: var(--p-primary-color);
  text-decoration: none;
}

.attachment-chip i {
  color: var(--pgpm-muted);
  font-size: 0.9rem;
}

.name {
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.meta {
  color: var(--pgpm-muted);
  font-size: 0.857rem;
  white-space: nowrap;
}

.pending-note {
  display: inline-flex;
  align-items: center;
  gap: 2px;
}

.removed,
.unavailable {
  color: var(--pgpm-muted);
  border-style: dashed;
}

.pending {
  border-style: dashed;
}

.resolving {
  pointer-events: none;
  opacity: 0.7;
}

@media print {
  .download {
    display: none;
  }
}
</style>

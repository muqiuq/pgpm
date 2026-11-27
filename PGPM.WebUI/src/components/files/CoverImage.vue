<script setup lang="ts">
import Button from 'primevue/button'
import { computed, ref } from 'vue'
import { useFilePipeline, useFileUrl } from '@/composables/useFileUrl'
import { useNotify } from '@/composables/useNotify'

/**
 * An item's cover image (docs/01 §4.1): shown from the thumbnail first, then the image itself (both from this device;
 * covers are prefetched, so they show offline). *Add cover image* / *Replace* by picking or dropping an image – prepared
 * as in the editor (metadata gone, > 2 MB made smaller) – and *Remove*; the file and the item's `imageFileId` change in
 * one batch, offline too. Without edit access it only shows the image (nothing without one).
 */
const props = defineProps<{ itemId: string; imageFileId: string | null; canEdit: boolean }>()

const files = useFilePipeline()
const notify = useNotify()
const thumbnail = useFileUrl(() => props.imageFileId, { thumbnail: true })
const full = useFileUrl(() => props.imageFileId)
/** The image as soon as it is here, the thumbnail meanwhile (or when the image can't be had). */
const shown = computed(() => full.value ?? thumbnail.value)
const picker = ref<HTMLInputElement | null>(null)
const busy = ref(false)
const dragging = ref(false)

async function set(file: File | undefined) {
  if (!file || !props.canEdit) return
  busy.value = true
  try {
    const { notice } = await files.setCover(props.itemId, file)
    if (notice) notify.info('Image made smaller', notice)
  } catch (e) {
    notify.error(e, 'Cover image not set')
  } finally {
    busy.value = false
  }
}

function onPick(event: Event) {
  const input = event.target as HTMLInputElement
  const file = input.files?.[0]
  input.value = ''
  void set(file)
}

function onDrop(event: DragEvent) {
  dragging.value = false
  void set(event.dataTransfer?.files?.[0])
}

async function remove() {
  busy.value = true
  try {
    await files.removeCover(props.itemId)
  } catch (e) {
    notify.error(e, 'Cover image not removed')
  } finally {
    busy.value = false
  }
}
</script>

<template>
  <div
    v-if="imageFileId || canEdit"
    class="cover"
    :class="{ empty: !imageFileId, dragging }"
    data-testid="cover-image"
    @dragover.prevent="canEdit && (dragging = true)"
    @dragleave="dragging = false"
    @drop.prevent="onDrop"
  >
    <img v-if="imageFileId && shown" :src="shown" alt="Cover image" class="image" />
    <div v-else-if="imageFileId && shown === null" class="placeholder muted">
      <i class="pi pi-image" aria-hidden="true" /> Not available offline
    </div>
    <div v-else-if="imageFileId" class="placeholder muted"><i class="pi pi-image" aria-hidden="true" /></div>
    <div v-if="canEdit" class="cover-actions">
      <Button
        :label="imageFileId ? 'Replace' : 'Add cover image'"
        icon="pi pi-image"
        size="small"
        text
        :loading="busy"
        @click="picker?.click()"
      />
      <Button
        v-if="imageFileId"
        label="Remove"
        icon="pi pi-times"
        size="small"
        text
        severity="secondary"
        :disabled="busy"
        @click="remove"
      />
      <input
        ref="picker"
        type="file"
        accept="image/png,image/jpeg,image/gif,image/webp"
        class="picker"
        aria-label="Cover image"
        data-testid="cover-input"
        @change="onPick"
      />
    </div>
  </div>
</template>

<style scoped>
.cover {
  display: flex;
  flex-direction: column;
  gap: var(--pgpm-space-1);
  border-radius: var(--p-border-radius-md);
}

.cover.dragging {
  outline: 2px dashed var(--p-primary-color);
}

.image {
  display: block;
  width: 100%;
  max-height: 16rem;
  object-fit: cover;
  border-radius: var(--p-border-radius-md);
  border: 1px solid var(--pgpm-border);
}

.placeholder {
  display: flex;
  align-items: center;
  justify-content: center;
  gap: var(--pgpm-space-2);
  height: 6rem;
  border: 1px dashed var(--pgpm-border);
  border-radius: var(--p-border-radius-md);
}

.cover-actions {
  display: flex;
  gap: var(--pgpm-space-1);
}

.picker {
  display: none;
}
</style>

<script setup lang="ts">
import Button from 'primevue/button'
import Dialog from 'primevue/dialog'
import { fileSize } from '@/components/markdown/files'

/**
 * An image attachment at full size (docs/01 §4.5), from its blob: URL (the CSP allows `img-src blob:`). PDFs open in a
 * tab of their own instead (no frames: the CSP's default-src 'self' would refuse a blob: frame).
 */
defineProps<{ name: string; url: string | null; size?: number | null }>()
const visible = defineModel<boolean>('visible', { default: false })
const emit = defineEmits<{ download: [] }>()
</script>

<template>
  <Dialog v-model:visible="visible" :header="name" modal dismissable-mask :style="{ width: 'min(64rem, 100vw)' }">
    <div class="preview">
      <img v-if="url" :src="url" :alt="name" />
      <p v-else class="muted">Not available offline.</p>
    </div>
    <template #footer>
      <span v-if="size" class="muted size">{{ fileSize(size) }}</span>
      <Button
        label="Download"
        icon="pi pi-download"
        size="small"
        severity="secondary"
        outlined
        @click="emit('download')"
      />
      <Button label="Close" size="small" text @click="visible = false" />
    </template>
  </Dialog>
</template>

<style scoped>
.preview {
  display: flex;
  justify-content: center;
}

.preview img {
  max-width: 100%;
  max-height: 75vh;
  object-fit: contain;
}

.size {
  margin-right: auto;
}
</style>

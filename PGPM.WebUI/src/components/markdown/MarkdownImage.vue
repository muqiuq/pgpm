<script setup lang="ts">
import { computed, onBeforeUnmount, ref, watch } from 'vue'
import { useFileUrlResolver } from './services'
import type { MarkdownFile } from './types'

/**
 * An image of PGPM's store in a text (PNMTD §5.6, docs/03 §18.4): lazily loaded, from where the provided resolver
 * has it (a blob: URL of this device's store, else the URL itself); *Pending upload* while it is on this device only,
 * *Not available offline* where it can't be shown, *Image removed* when the file is gone. `zoom`: a click opens the
 * full size. `localOnly` (compact texts): never fetched, only shown when this device has it.
 */
const props = withDefaults(
  defineProps<{
    src: string
    alt?: string
    title?: string
    file?: MarkdownFile | null
    zoom?: boolean
    localOnly?: boolean
  }>(),
  { alt: undefined, title: undefined, file: null, zoom: false, localOnly: false },
)

const resolve = useFileUrlResolver()
/** Where it is shown from; undefined while the resolver looks, null where it can't be shown. */
const shown = ref<string | null | undefined>(resolve ? undefined : props.src)
const failed = ref(false)
let generation = 0

watch(
  () => [props.src, props.localOnly] as const,
  ([src, localOnly]) => {
    failed.value = false
    if (!resolve) {
      shown.value = src
      return
    }
    const mine = ++generation
    shown.value = undefined
    resolve(src, { localOnly }).then(
      (url) => mine === generation && (shown.value = url),
      () => mine === generation && (shown.value = null),
    )
  },
  { immediate: !!resolve },
)

onBeforeUnmount(() => generation++)

const pending = computed(() => props.file?.status === 'pending')
const gone = computed(() => failed.value || props.file?.deleted === true)
</script>

<template>
  <span v-if="gone" class="md-image-removed" :title="src">
    <i class="pi pi-image" aria-hidden="true" /> Image removed<template v-if="alt">: {{ alt }}</template>
  </span>
  <span v-else-if="shown === null" class="md-image-removed" :title="src">
    <i class="pi pi-image" aria-hidden="true" />
    <template v-if="localOnly">{{ alt || 'Image' }}</template>
    <template v-else
      >Not available offline<template v-if="alt">: {{ alt }}</template></template
    >
  </span>
  <span v-else-if="shown === undefined" class="md-image-loading" role="img" :aria-label="alt || 'Image'">
    <i class="pi pi-image" aria-hidden="true" />
  </span>
  <span v-else class="md-image-frame" :class="{ pending }">
    <a v-if="zoom" class="md-image" :href="shown" target="_blank" rel="noopener">
      <img :src="shown" :alt="alt ?? ''" :title="title" loading="lazy" decoding="async" @error="failed = true" />
    </a>
    <img v-else :src="shown" :alt="alt ?? ''" :title="title" loading="lazy" decoding="async" @error="failed = true" />
    <span v-if="pending" class="md-pending" title="Stored on this device; it uploads when online">
      <i class="pi pi-cloud-upload" aria-hidden="true" /> Pending upload
    </span>
  </span>
</template>

<style scoped>
.md-image-removed,
.md-image-loading {
  display: inline-flex;
  align-items: center;
  gap: var(--pgpm-space-1);
  padding: var(--pgpm-space-1) var(--pgpm-space-2);
  border: 1px dashed var(--pgpm-border);
  border-radius: var(--p-border-radius-sm);
  color: var(--pgpm-muted);
}

.md-image-loading {
  min-width: 4rem;
  min-height: 3rem;
  justify-content: center;
}

.md-image-frame {
  position: relative;
  display: inline-block;
  max-width: 100%;
}

.md-pending {
  position: absolute;
  left: var(--pgpm-space-1);
  bottom: var(--pgpm-space-1);
  display: inline-flex;
  align-items: center;
  gap: var(--pgpm-space-1);
  padding: 0 var(--pgpm-space-2);
  border-radius: var(--p-border-radius-sm);
  background: var(--pgpm-panel);
  color: var(--pgpm-muted);
  font-size: 0.857rem;
  opacity: 0.92;
}
</style>

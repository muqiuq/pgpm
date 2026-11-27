import { watch } from 'vue'
import { conflicts } from '@/local/boot'
import { useSyncStore } from '@/stores/sync'

/**
 * A map's preview is the client's picture of it (preview.ts), written by whoever leaves a map they changed: when two
 * devices wrote one at the same time, the patch collides like any field – but there is nothing to decide. Such a
 * conflict (on `maps`, the preview only) is settled with the server's picture at once (*Keep theirs*); the next visit
 * draws a new one.
 */
export function usePreviewConflicts() {
  const sync = useSyncStore()
  watch(
    () => sync.conflicts,
    (list) => {
      for (const c of list) {
        const keys = Object.keys(c.fields ?? {})
        if (c.table === 'maps' && c.kind === 'field' && keys.length > 0 && keys.every((k) => k === 'preview')) {
          void conflicts.keepTheirs(c).catch(() => undefined)
        }
      }
    },
    { immediate: true },
  )
}

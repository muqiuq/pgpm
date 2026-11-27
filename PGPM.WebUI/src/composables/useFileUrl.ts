import { inject, shallowRef, toValue, watch, type MaybeRefOrGetter, type Ref } from 'vue'
import { appFiles } from '@/local/boot'
import { filePipelineKey, type FilePipeline } from '@/local/files'
import { useLive } from '@/local/useLive'

/** The provided pipeline (tests), else the app's. */
export function useFilePipeline(): FilePipeline {
  return inject(filePipelineKey, null) ?? appFiles
}

/**
 * A blob: URL to show a file (a cover, an image), live: undefined while it is looked up, null where it can't be shown
 * (offline and not on this device, not uploaded yet, gone). `thumbnail`: the row's thumbnail where it has one. It is
 * looked up again when the row or the bytes on this device change (the upload done, the download arrived, evicted).
 */
export function useFileUrl(
  fileId: MaybeRefOrGetter<string | null | undefined>,
  options: { thumbnail?: boolean } = {},
): Readonly<Ref<string | null | undefined>> {
  const files = useFilePipeline()
  const url = shallowRef<string | null | undefined>(undefined)
  const state = useLive(
    async () => {
      const id = toValue(fileId)
      if (!id) return 'none'
      const db = files.db()
      const row = await db.synced('files').get(id)
      const here = await db.blobs.where('id').equals(id).primaryKeys()
      return `${id}:${String(row?.status)}:${String(row?.deleted)}:${here.length}:${row?.thumbnail ? 1 : 0}`
    },
    '',
    () => toValue(fileId),
  )
  let generation = 0
  watch(
    state.value,
    async (signature) => {
      if (!signature) return
      const id = toValue(fileId)
      const mine = ++generation
      if (!id) {
        url.value = null
        return
      }
      const next = await files.fileUrl(id, options).catch(() => null)
      if (mine === generation) url.value = next
    },
    { immediate: true },
  )
  return url
}

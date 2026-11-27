import { shallowRef, watch, type Ref } from 'vue'
import type { MapDataSource } from './dataSource'

/**
 * The URLs of the files a map shows (cover images, image elements), each asked of the data source once (the local blob
 * store: thumbnail first, docs/03 §13.1); answers that arrive together are published together.
 */
export function useFileUrls(source: MapDataSource, fileIds: Readonly<Ref<ReadonlySet<string>>>) {
  const urls = shallowRef<ReadonlyMap<string, string | null>>(new Map())
  const asked = new Set<string>()
  let pending: [string, string | null][] = []

  function publish() {
    if (!pending.length) return
    const next = new Map(urls.value)
    for (const [id, url] of pending) next.set(id, url)
    pending = []
    urls.value = next
  }

  watch(
    fileIds,
    (ids) => {
      for (const id of ids) {
        if (asked.has(id)) continue
        asked.add(id)
        source
          .fileUrl(id, { thumbnail: true })
          .catch(() => null)
          .then((url) => {
            if (!pending.length) queueMicrotask(publish)
            pending.push([id, url])
          })
      }
    },
    { immediate: true },
  )
  return urls
}

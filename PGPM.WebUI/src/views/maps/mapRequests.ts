import { shallowRef } from 'vue'
import type { ShareSpec } from '@/api/sync'
import type { FilePipeline } from '@/local/files'
import { createItem } from '@/local/items'
import { repo as appRepo, type Repo } from '@/local/repo'
import type { ItemType } from '@/local/rows'
import { SIZES, type ImageRequest, type ItemRequest, type PlaceRequest } from '@/maps/tools'

/**
 * The map page's answers to what the canvas' placing tools ask (docs/03 §13.4, doc 01 §12.4): an item tool's *Create
 * new* (title, status, *Share with* preselected – the item and its shares in one batch, offline) or *Add existing*
 * (the dialog marks the items already on the map, they can be added again); a milestone or gate of a project; a
 * project for a timeline; an image – pasted, or picked – stored through the file pipeline (`map_image`, uploaded
 * after the sync). The canvas adds the element when `place` is called.
 */
export type PendingRequest =
  | { kind: 'item'; request: ItemRequest }
  | { kind: 'milestone'; request: PlaceRequest }
  | { kind: 'project'; request: PlaceRequest }

export interface MapRequestOptions {
  mapId: () => string
  files: Pick<FilePipeline, 'attachFile'>
  repo?: Repo
  /** The browser's file chooser (an image); null: cancelled. */
  pickImage: () => Promise<File | null>
  onError: (error: unknown, summary: string) => void
}

/** An image's size on the map: its own, at most 480 × 360, the aspect kept. */
export function imageSize(width: number | null | undefined, height: number | null | undefined) {
  if (!width || !height || width <= 0 || height <= 0) return { ...SIZES.image }
  const k = Math.min(1, 480 / width, 360 / height)
  return { width: Math.max(16, Math.round(width * k)), height: Math.max(16, Math.round(height * k)) }
}

export function useMapRequests(options: MapRequestOptions) {
  const repo = options.repo ?? appRepo
  const pending = shallowRef<PendingRequest | null>(null)
  const uploading = shallowRef(false)

  function settle() {
    pending.value = null
  }

  /** The dialog closed without a choice. */
  function cancel() {
    pending.value?.request.cancel()
    settle()
  }

  /** *Add existing*, a milestone, a project: the chosen id. */
  function place(id: string) {
    const p = pending.value
    if (!p) return
    settle()
    p.request.place(id)
  }

  /** *Create new*: the item (with its shares) on the device, then its card where the user clicked. */
  async function createAndPlace(input: { title: string; statusId: string | null; shares: ShareSpec[] }) {
    const p = pending.value
    if (p?.kind !== 'item') return
    const type: ItemType = p.request.extra.type
    const item = await repo.transaction((tx) => createItem(tx, { type, ...input }))
    settle()
    p.request.place(item.id)
    return item
  }

  async function onRequestImage(request: ImageRequest) {
    let file: File | null = request.extra.file
    try {
      file ??= await options.pickImage()
    } catch {
      file = null
    }
    if (!file) {
      request.cancel()
      return
    }
    uploading.value = true
    try {
      const row = await options.files.attachFile({ mapId: options.mapId() }, file, 'map_image')
      request.place(row.id, imageSize(row.width, row.height))
    } catch (e) {
      request.cancel()
      options.onError(e, 'Image not added')
    } finally {
      uploading.value = false
    }
  }

  return {
    pending,
    uploading,
    cancel,
    place,
    createAndPlace,
    onRequestItem: (request: ItemRequest) => (pending.value = { kind: 'item', request }),
    onRequestMilestone: (request: PlaceRequest) => (pending.value = { kind: 'milestone', request }),
    onRequestProject: (request: PlaceRequest) => (pending.value = { kind: 'project', request }),
    onRequestImage,
  }
}

export type MapRequests = ReturnType<typeof useMapRequests>

import { computed, shallowRef, toValue, type MaybeRefOrGetter } from 'vue'
import { editingApi } from '@/api/sync'
import type { EditingMessage } from './live'

/**
 * *… is editing* (docs/03 §18.6): while an editor is open, `POST /api/v1/editing {itemId, target}` every 30 s and
 * `DELETE` on close; the server tells the live connections of everyone who can read the item (`editing` /
 * `editing_end`). Kept in memory, blocks nothing, online only. A notice not refreshed for 2 minutes lapses.
 */
export interface EditingNotice {
  itemId: string
  /** `description`, `field:<key>`, `milestone:<id>`. */
  target: string
  userId: string | null
  userName: string
  since: string
  heardAt: number
}

const notices = shallowRef<EditingNotice[]>([])
const lapse = 120_000

/** A message of the live channel (sync.ts hands them over, in every tab): `userId` and `user`, the display name. */
export function receiveEditing(message: EditingMessage, now = Date.now()) {
  const id = typeof message.userId === 'string' && message.userId ? message.userId : null
  const name = typeof message.user === 'string' && message.user ? message.user : 'Someone'
  const same = (n: EditingNotice) =>
    n.itemId === message.itemId && n.target === message.target && (id ? n.userId === id : n.userName === name)
  const others = notices.value.filter((n) => !same(n) && now - n.heardAt < lapse)
  notices.value =
    message.type === 'editing'
      ? [
          ...others,
          {
            itemId: message.itemId,
            target: message.target,
            userId: id,
            userName: name,
            since: message.since ?? new Date(now).toISOString(),
            heardAt: now,
          },
        ]
      : others
}

/** Everyone else editing the item (or one of its texts), newest last. */
export function useEditors(
  itemId: MaybeRefOrGetter<string>,
  options: {
    target?: MaybeRefOrGetter<string | undefined>
    me?: MaybeRefOrGetter<{ id: string; name: string } | null>
  } = {},
) {
  return computed(() => {
    const id = toValue(itemId)
    const target = toValue(options.target)
    const me = toValue(options.me)
    const now = Date.now()
    return notices.value.filter(
      (n) =>
        n.itemId === id &&
        (!target || n.target === target) &&
        now - n.heardAt < lapse &&
        !(me && (n.userId ? n.userId === me.id : n.userName === me.name)),
    )
  })
}

/** Announces this editor until the returned stop is called; failures (offline) are ignored. */
export function announceEditing(itemId: string, target: string, api = editingApi, every = 30_000): () => void {
  let stopped = false
  const ping = () => void api.start(itemId, target).catch(() => undefined)
  ping()
  const timer = setInterval(ping, every)
  return () => {
    if (stopped) return
    stopped = true
    clearInterval(timer)
    void api.stop(itemId, target).catch(() => undefined)
  }
}

/** Forgets every notice (sign-out). */
export function clearEditing() {
  notices.value = []
}

import { mount } from '@vue/test-utils'
import { createPinia } from 'pinia'
import PrimeVue from 'primevue/config'
import ToastService from 'primevue/toastservice'
import Tooltip from 'primevue/tooltip'
import { afterEach, describe, expect, it } from 'vitest'
import { engine } from '@/local/boot'
import { useDatabase } from '@/local/db'
import { memoryDb, until } from '@/test/memoryDb'
import { useSyncStore } from '@/stores/sync'
import SyncIndicator from './SyncIndicator.vue'

describe('the sync indicator (docs/03 §12.6)', () => {
  const db = memoryDb()
  afterEach(async () => {
    useDatabase(null)
    await db.delete()
  })

  it('shows live / offline, the pending changes and the conflicts, and opens the dialog', async () => {
    useDatabase(db)
    const pinia = createPinia()
    const wrapper = mount(SyncIndicator, {
      global: {
        plugins: [[PrimeVue, { unstyled: true }], ToastService, pinia],
        directives: { tooltip: Tooltip },
        stubs: { SyncDialog: true },
      },
    })
    const button = () => wrapper.get('[data-testid="sync-indicator"]')
    engine.status.connection = 'live'
    await until(() => button().text() === 'Live', 'live')
    engine.status.connection = 'offline'
    await until(() => button().text() === 'Offline', 'offline')

    const entry = {
      mutationId: 'm',
      batch: 'b',
      table: 'items',
      op: 'delete' as const,
      baseRev: 1,
      changes: null,
      row: null,
      clientChangedAt: '2026-11-27T00:00:00Z',
    }
    await db.outbox.bulkAdd([
      { ...entry, id: 'a' },
      { ...entry, id: 'b' },
    ])
    await until(() => wrapper.find('[data-testid="sync-pending"]').exists(), 'the pending count')
    expect(wrapper.get('[data-testid="sync-pending"]').text()).toBe('2')
    await db.conflicts.add({
      id: 'c1',
      table: 'items',
      rowId: 'a',
      mapId: null,
      kind: 'field',
      fields: {},
      baseRev: 1,
      createdAt: '2026-11-27T00:00:00Z',
    })
    await until(() => wrapper.find('[data-testid="sync-conflicts"]').exists(), 'the conflict count')
    expect(button().attributes('aria-label')).toBe('Sync: Offline, 2 pending, 1 conflicts')

    await button().trigger('click')
    const sync = useSyncStore(pinia)
    expect(sync.dialogOpen).toBe(true)
    expect(sync.dialogTab).toBe('conflicts')
    engine.status.connection = 'stopped'
    wrapper.unmount()
  })
})

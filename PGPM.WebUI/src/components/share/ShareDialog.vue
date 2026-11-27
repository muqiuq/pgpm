<script setup lang="ts">
import Button from 'primevue/button'
import Dialog from 'primevue/dialog'
import Message from 'primevue/message'
import Select from 'primevue/select'
import { computed, ref, watch } from 'vue'
import { sharingApi, type ShareSpec } from '@/api/sync'
import { useActions } from '@/composables/useActions'
import { engine } from '@/local/boot'
import { localDb } from '@/local/db'
import { useGroups, useOnline, useUserPersons } from '@/local/lookups'
import type { Access, ItemShareRow } from '@/local/rows'
import { useLive } from '@/local/useLive'
import ShareEditor from './ShareEditor.vue'

/**
 * *Share* on an item – or, with `mapId`, a map (docs/01 §4.2, §12.1, docs/03 §6.3, §12.6): the groups with *read* /
 * *edit* / *manage*, and the owner.
 * Changing them needs *manage* and a connection (`PUT …/shares`, `POST …/owner`); the server re-stamps the item, so
 * everyone's devices get the change (and lose the item, where access is gone). With less than *manage*, the sharing is
 * shown read-only.
 */
const props = defineProps<{
  itemId?: string
  mapId?: string
  title: string
  ownerUserId: string
  access: Access | null | undefined
}>()
const visible = defineModel<boolean>('visible', { default: false })

const { done, notify } = useActions()
const online = useOnline()
const groups = useGroups()
const users = useUserPersons()
const target = computed(() =>
  props.mapId
    ? { table: 'map_shares', key: 'mapId', id: props.mapId }
    : { table: 'item_shares', key: 'itemId', id: props.itemId ?? '' },
)
const current = useLive(
  async () =>
    (
      (await localDb()
        .synced(target.value.table)
        .where(target.value.key)
        .equals(target.value.id)
        .toArray()) as unknown as ItemShareRow[]
    )
      .filter((s) => !s.deleted)
      .map((s) => ({ groupId: s.groupId, access: s.access })),
  [] as ShareSpec[],
  () => target.value.id,
)

const manage = computed(() => props.access === 'manage')
const draft = ref<ShareSpec[]>([])
const owner = ref<string | null>(null)
const saving = ref(false)

watch(visible, (open) => {
  if (open) {
    draft.value = current.value.value.map((s) => ({ ...s }))
    owner.value = props.ownerUserId
  }
})

const sorted = (list: ShareSpec[]) =>
  JSON.stringify([...list].sort((a, b) => (a.groupId < b.groupId ? -1 : 1)).map((s) => [s.groupId, s.access]))
const changed = computed(() => sorted(draft.value) !== sorted(current.value.value))
const ownerChanged = computed(() => !!owner.value && owner.value !== props.ownerUserId)
const ownerOptions = computed(() =>
  users.persons.value
    .filter((p) => p.isActive !== false)
    .map((p) => ({ label: p.displayName, value: p.userId! }))
    .sort((a, b) => a.label.localeCompare(b.label)),
)
const summary = computed(() =>
  draft.value.length === 0
    ? 'Private: only the owner and admins see it.'
    : `Shared with ${draft.value.length} group${draft.value.length === 1 ? '' : 's'}.`,
)

async function save() {
  saving.value = true
  try {
    if (changed.value) {
      const id = target.value.id
      const shares = draft.value
      const ok = await done(
        () => (props.mapId ? sharingApi.setMapShares(id, shares) : sharingApi.setShares(id, shares)),
        {
          success: 'Sharing saved',
          failure: 'Sharing not saved',
        },
      )
      if (!ok) return
    }
    if (ownerChanged.value) {
      const name = users.nameOf(owner.value, 'the new owner')
      const id = target.value.id
      const user = owner.value!
      const ok = await done(
        () => (props.mapId ? sharingApi.transferMapOwner(id, user) : sharingApi.transferOwner(id, user)),
        {
          success: `${name} is the owner now`,
          failure: 'Owner not changed',
        },
      )
      if (!ok) return
    }
    // The re-stamped rows come over the live channel; without it, with this sync.
    if (!engine.isLive) void engine.syncNow()
    visible.value = false
  } finally {
    saving.value = false
  }
}

function close() {
  if ((changed.value || ownerChanged.value) && manage.value) notify.info('Sharing not changed')
  visible.value = false
}
</script>

<template>
  <Dialog
    v-model:visible="visible"
    :header="`Share ${title}`"
    modal
    :style="{ width: 'min(32rem, 100vw)' }"
    data-testid="share-dialog"
  >
    <div class="form">
      <Message v-if="manage && !online" severity="warn" size="small" :closable="false">
        Changing the sharing needs a connection.
      </Message>
      <Message v-else-if="!manage" severity="secondary" size="small" :closable="false">
        Only someone with <em>manage</em> access can change the sharing.
      </Message>
      <div class="field">
        <span class="label">Groups</span>
        <ShareEditor v-model="draft" :groups="groups" :disabled="!manage || !online" id-prefix="share-dialog" />
        <small class="field-hint">{{ summary }} A member of several groups gets the highest access.</small>
      </div>
      <div class="field">
        <label id="share-owner-label" for="share-owner">Owner</label>
        <Select
          v-model="owner"
          input-id="share-owner"
          aria-labelledby="share-owner-label"
          :options="ownerOptions"
          option-label="label"
          option-value="value"
          filter
          :disabled="!manage || !online"
          :placeholder="users.nameOf(ownerUserId, 'Unknown')"
        />
        <small class="field-hint">The owner has <em>manage</em> access, whatever the sharing.</small>
      </div>
    </div>
    <template #footer>
      <Button label="Cancel" severity="secondary" text @click="close" />
      <Button
        label="Save"
        icon="pi pi-check"
        :loading="saving"
        :disabled="!manage || !online || (!changed && !ownerChanged)"
        @click="save"
      />
    </template>
  </Dialog>
</template>

<style scoped>
.label {
  font-weight: 600;
}
</style>

<script setup lang="ts">
import Button from 'primevue/button'
import Checkbox from 'primevue/checkbox'
import Dialog from 'primevue/dialog'
import InputText from 'primevue/inputtext'
import Menu from 'primevue/menu'
import type { MenuItem } from 'primevue/menuitem'
import { useConfirm } from 'primevue/useconfirm'
import { computed, ref, watch } from 'vue'
import type { ViewState } from '@/composables/useLocalTable'
import { useNotify } from '@/composables/useNotify'
import { localDb } from '@/local/db'
import { repo } from '@/local/repo'
import type { SavedViewRow } from '@/local/rows'
import {
  canChange,
  createView,
  deleteView,
  renameView,
  sameDefinition,
  updateView,
  viewNameError,
  viewsFor,
  type ViewType,
} from '@/local/savedViews'
import { useLive } from '@/local/useLive'
import { useSession } from '@/stores/session'

/**
 * The saved views of a list (docs/01 §4.3, data model §7.8), next to the search: own views and the shared ones (marked),
 * *All <items>*; selecting one applies its definition (`apply`). *Save as new view…* (shared with everybody needs
 * *Configuration: Manage*), *Update*, *Rename…* and *Delete…* for the user's own views. A view whose state was changed
 * since shows as *modified*. v-model is the view's id.
 */
const props = defineProps<{ type: ViewType; plural: string; state: ViewState }>()
const viewId = defineModel<string | null>({ default: null })
const emit = defineEmits<{ apply: [state: ViewState | null] }>()

const session = useSession()
const notify = useNotify()
const confirm = useConfirm()
const menu = ref<InstanceType<typeof Menu> | null>(null)
const menuId = `views-${props.type}`

const rows = useLive(
  async () =>
    (await localDb().synced('saved_views').where('itemType').equals(props.type).toArray()) as unknown as SavedViewRow[],
  [] as SavedViewRow[],
  () => props.type,
)
const user = computed(() => ({
  userId: session.user?.id ?? null,
  configManage: session.allows(['Configuration', 'Manage']),
}))
const views = computed(() => viewsFor(rows.value.value, props.type, user.value.userId))
const current = computed(() => views.value.find((v) => v.id === viewId.value) ?? null)
const modified = computed(() => !!current.value && !sameDefinition(current.value.definition, props.state))
const label = computed(() => (current.value ? current.value.name : `All ${props.plural.toLowerCase()}`))

function select(view: SavedViewRow | null) {
  viewId.value = view?.id ?? null
  emit('apply', view ? (view.definition as ViewState) : null)
}

// --- the name dialog (save as, rename) ---
const dialog = ref<'save' | 'rename' | null>(null)
const name = ref('')
const shared = ref(false)
const error = ref<string | null>(null)
const saving = ref(false)

watch(dialog, (open) => {
  if (!open) return
  error.value = null
  name.value = open === 'rename' ? (current.value?.name ?? '') : ''
  shared.value = false
})

async function submit() {
  error.value = viewNameError(name.value)
  if (error.value) return
  saving.value = true
  try {
    if (dialog.value === 'rename' && current.value) {
      await renameView(repo, current.value, name.value, user.value)
    } else {
      const view = await createView(
        repo,
        { type: props.type, name: name.value, state: props.state, shared: shared.value },
        user.value,
        rows.value.value,
      )
      viewId.value = view.id
    }
    dialog.value = null
  } catch (e) {
    notify.error(e, 'View not saved')
  } finally {
    saving.value = false
  }
}

async function update() {
  if (!current.value) return
  try {
    await updateView(repo, current.value, props.state, user.value)
    notify.success(`“${current.value.name}” updated`)
  } catch (e) {
    notify.error(e, 'View not updated')
  }
}

function remove() {
  const view = current.value
  if (!view) return
  confirm.require({
    header: `Delete the view ${view.name}?`,
    message: view.isShared ? 'It goes for everybody.' : 'Only the view goes; the items stay.',
    icon: 'pi pi-exclamation-triangle',
    acceptProps: { label: 'Delete', severity: 'danger' },
    rejectProps: { label: 'Cancel', severity: 'secondary', text: true },
    accept: async () => {
      try {
        await deleteView(repo, view, user.value)
        select(null)
      } catch (e) {
        notify.error(e, 'View not deleted')
      }
    },
  })
}

const items = computed<MenuItem[]>(() => {
  const mine = current.value && canChange(current.value, user.value)
  return [
    {
      label: 'Views',
      items: [
        {
          label: `All ${props.plural.toLowerCase()}`,
          icon: viewId.value ? 'pi pi-list' : 'pi pi-check',
          command: () => select(null),
        },
        ...views.value.map((v) => ({
          label: v.isShared ? `${v.name} (shared)` : v.name,
          icon: v.id === viewId.value ? 'pi pi-check' : v.isShared ? 'pi pi-users' : 'pi pi-bookmark',
          command: () => select(v),
        })),
      ],
    },
    {
      label: 'Actions',
      items: [
        { label: 'Save as new view…', icon: 'pi pi-plus', command: () => (dialog.value = 'save') },
        ...(mine
          ? [
              {
                label: `Update “${current.value!.name}”`,
                icon: 'pi pi-save',
                disabled: !modified.value,
                command: update,
              },
              { label: 'Rename…', icon: 'pi pi-pencil', command: () => (dialog.value = 'rename') },
              { label: 'Delete…', icon: 'pi pi-trash', command: remove },
            ]
          : []),
      ],
    },
  ]
})
</script>

<template>
  <Button
    size="small"
    severity="secondary"
    outlined
    icon="pi pi-bookmark"
    icon-pos="left"
    :label="modified ? `${label} (modified)` : label"
    aria-haspopup="true"
    :aria-controls="menuId"
    :aria-label="`View: ${label}${modified ? ', modified' : ''}`"
    data-testid="saved-views"
    @click="(e: Event) => menu?.toggle(e)"
  />
  <Menu :id="menuId" ref="menu" :model="items" popup />
  <Dialog
    :visible="dialog !== null"
    :header="dialog === 'rename' ? 'Rename view' : 'Save as new view'"
    modal
    :style="{ width: 'min(26rem, 100vw)' }"
    @update:visible="(open: boolean) => !open && (dialog = null)"
  >
    <form :id="`${menuId}-form`" class="form" novalidate @submit.prevent="submit">
      <div class="field">
        <label :for="`${menuId}-name`">Name</label>
        <InputText :id="`${menuId}-name`" v-model="name" autofocus maxlength="100" :invalid="!!error" />
        <small v-if="error" class="field-error">{{ error }}</small>
      </div>
      <div v-if="dialog === 'save'" class="check">
        <Checkbox v-model="shared" :input-id="`${menuId}-shared`" binary :disabled="!user.configManage" />
        <label :for="`${menuId}-shared`">Shared with everybody</label>
        <small v-if="!user.configManage" class="muted">Needs Configuration: Manage.</small>
      </div>
    </form>
    <template #footer>
      <Button label="Cancel" severity="secondary" text @click="dialog = null" />
      <Button type="submit" :form="`${menuId}-form`" label="Save" :loading="saving" />
    </template>
  </Dialog>
</template>

<style scoped>
.check {
  display: flex;
  align-items: center;
  gap: var(--pgpm-space-2);
  flex-wrap: wrap;
}
</style>

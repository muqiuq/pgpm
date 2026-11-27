<script setup lang="ts">
import Button from 'primevue/button'
import Dialog from 'primevue/dialog'
import InputNumber from 'primevue/inputnumber'
import InputText from 'primevue/inputtext'
import Select from 'primevue/select'
import ToggleSwitch from 'primevue/toggleswitch'
import { computed, ref, watch } from 'vue'
import { ApiError } from '@/api/client'
import { statusesApi, type StatusInput } from '@/api/config'
import { useNotify } from '@/composables/useNotify'
import { presetColors } from '@/domain/itemTypes'
import type { StatusCategory, StatusRow } from '@/local/rows'

/**
 * A status's settings (docs/01 §5): name, colour (the 12 presets or a hex value), PrimeIcons icon, category, default,
 * max. days. Saved online through the statuses endpoints (`POST`/`PUT /api/v1/config/statuses`); the row itself comes
 * back through the sync. The server's validation messages show under their field.
 */
const props = defineProps<{ itemType: StatusInput['itemType']; status: StatusRow | null; disabled?: boolean }>()
const visible = defineModel<boolean>('visible', { default: false })
const emit = defineEmits<{ saved: [] }>()
const notify = useNotify()

const categories: { label: string; value: StatusCategory }[] = [
  { label: 'Open', value: 'open' },
  { label: 'Active', value: 'active' },
  { label: 'Closed', value: 'closed' },
]
const typeNames: Record<StatusInput['itemType'], string> = { idea: 'idea', lead: 'lead', project: 'project' }

const name = ref('')
const color = ref('#64748b')
const icon = ref('')
const category = ref<StatusCategory>('open')
const isDefault = ref(false)
const maxDays = ref<number | null>(null)
const errors = ref<Record<string, string | undefined>>({})
const saving = ref(false)

watch(
  visible,
  (open) => {
    if (!open) return
    const s = props.status
    name.value = s?.name ?? ''
    color.value = s?.color ?? '#64748b'
    icon.value = s?.icon ?? ''
    category.value = s?.category ?? 'open'
    isDefault.value = s?.isDefault ?? false
    maxDays.value = s?.maxDays ?? null
    errors.value = {}
  },
  { immediate: true },
)

/** `eye`, `pi-eye` and `pi pi-eye` are the same icon: stored as `pi-eye` (docs/03 §17). */
function iconName(text: string): string | null {
  const bare = text
    .trim()
    .replace(/^pi\s+/, '')
    .replace(/^pi-/, '')
  return bare ? `pi-${bare}` : null
}

const iconClass = computed(() => {
  const value = iconName(icon.value)
  return value && /^pi-[a-z0-9-]+$/.test(value) ? `pi ${value}` : null
})
const title = computed(() =>
  props.status ? `Edit status ${props.status.name}` : `New ${typeNames[props.itemType]} status`,
)

function validate(): boolean {
  const next: Record<string, string | undefined> = {}
  const text = name.value.trim()
  if (!text) next.name = 'A name is needed.'
  else if (text.length > 100) next.name = 'At most 100 characters.'
  if (!/^#[0-9a-fA-F]{6}$/.test(color.value.trim())) next.color = 'A colour #rrggbb.'
  const value = iconName(icon.value)
  if (value && !/^pi-[a-z0-9-]+$/.test(value)) next.icon = 'A PrimeIcons name such as pi-eye.'
  if (maxDays.value !== null && (!Number.isInteger(maxDays.value) || maxDays.value < 1))
    next.maxDays = 'Whole days, at least 1 – or empty.'
  errors.value = next
  return !Object.values(next).some(Boolean)
}

async function save() {
  if (props.disabled || !validate()) return
  saving.value = true
  const body = {
    name: name.value.trim(),
    color: color.value.trim().toLowerCase(),
    icon: iconName(icon.value),
    category: category.value,
    isDefault: isDefault.value,
    maxDays: maxDays.value,
  }
  try {
    if (props.status) await statusesApi.update(props.status.id, { ...body, itemType: props.itemType })
    else await statusesApi.create({ ...body, itemType: props.itemType })
    notify.success(props.status ? 'Status saved' : 'Status created')
    visible.value = false
    emit('saved')
  } catch (e) {
    if (e instanceof ApiError && Object.keys(e.errors).length) {
      errors.value = Object.fromEntries(
        ['name', 'color', 'icon', 'category', 'isDefault', 'maxDays'].map((f) => [f, e.field(f)]),
      )
      if (!Object.values(errors.value).some(Boolean)) notify.error(e, 'Not saved')
    } else {
      notify.error(e, 'Not saved')
    }
  } finally {
    saving.value = false
  }
}
</script>

<template>
  <Dialog v-model:visible="visible" :header="title" modal :style="{ width: 'min(32rem, 100vw)' }">
    <form id="status-form" class="form" novalidate @submit.prevent="save">
      <div class="field">
        <label for="st-name">Name</label>
        <InputText id="st-name" v-model="name" maxlength="100" autofocus :invalid="!!errors.name" />
        <small v-if="errors.name" class="field-error">{{ errors.name }}</small>
      </div>
      <div class="field">
        <label for="st-color">Colour</label>
        <div class="swatches" role="group" aria-label="Preset colours">
          <button
            v-for="c in presetColors"
            :key="c.value"
            type="button"
            class="swatch"
            :class="{ picked: color.toLowerCase() === c.value }"
            :style="{ background: c.value }"
            :aria-label="c.name"
            :aria-pressed="color.toLowerCase() === c.value"
            @click="color = c.value"
          />
        </div>
        <div class="with-preview">
          <span class="dot" :style="{ background: color }" aria-hidden="true" />
          <InputText id="st-color" v-model="color" class="mono" maxlength="7" :invalid="!!errors.color" />
        </div>
        <small v-if="errors.color" class="field-error">{{ errors.color }}</small>
      </div>
      <div class="field">
        <label for="st-icon">Icon</label>
        <div class="with-preview">
          <i :class="iconClass ?? 'pi pi-circle'" class="icon-preview" aria-hidden="true" />
          <InputText id="st-icon" v-model="icon" class="mono" placeholder="pi-eye" :invalid="!!errors.icon" />
        </div>
        <small v-if="errors.icon" class="field-error">{{ errors.icon }}</small>
        <small v-else class="field-hint">Optional: a PrimeIcons name.</small>
      </div>
      <div class="form-grid">
        <div class="field">
          <label for="st-category">Category</label>
          <Select
            v-model="category"
            input-id="st-category"
            :options="categories"
            option-label="label"
            option-value="value"
          />
          <small v-if="errors.category" class="field-error">{{ errors.category }}</small>
        </div>
        <div class="field">
          <label for="st-max">Max. days</label>
          <InputNumber
            v-model="maxDays"
            input-id="st-max"
            :min="1"
            :use-grouping="false"
            placeholder="No limit"
            :invalid="!!errors.maxDays"
          />
          <small v-if="errors.maxDays" class="field-error">{{ errors.maxDays }}</small>
        </div>
      </div>
      <div class="switch">
        <ToggleSwitch v-model="isDefault" input-id="st-default" />
        <label for="st-default">Default for new {{ typeNames[itemType] }}s</label>
      </div>
    </form>
    <template #footer>
      <Button label="Cancel" severity="secondary" text @click="visible = false" />
      <Button
        type="submit"
        form="status-form"
        :label="status ? 'Save' : 'Create status'"
        :loading="saving"
        :disabled="disabled"
      />
    </template>
  </Dialog>
</template>

<style scoped>
.swatches {
  display: flex;
  flex-wrap: wrap;
  gap: var(--pgpm-space-1);
}

.swatch {
  width: 22px;
  height: 22px;
  border-radius: 50%;
  border: 2px solid transparent;
  cursor: pointer;
  padding: 0;
}

.swatch.picked {
  outline: 2px solid var(--p-primary-color);
  outline-offset: 1px;
}

.with-preview {
  display: flex;
  align-items: center;
  gap: var(--pgpm-space-2);
}

.dot {
  width: 16px;
  height: 16px;
  border-radius: 50%;
  flex: none;
  border: 1px solid var(--pgpm-border);
}

.icon-preview {
  width: 16px;
  text-align: center;
}

.switch {
  display: flex;
  align-items: center;
  gap: var(--pgpm-space-2);
}
</style>

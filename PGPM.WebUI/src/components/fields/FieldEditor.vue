<script setup lang="ts">
import Button from 'primevue/button'
import InputText from 'primevue/inputtext'
import MultiSelect from 'primevue/multiselect'
import Select from 'primevue/select'
import { computed, ref } from 'vue'
import { useRouter } from 'vue-router'
import { ApiError } from '@/api/client'
import InlineField from '@/components/InlineField.vue'
import PersonPicker from '@/components/items/PersonPicker.vue'
import StatusBadge from '@/components/items/StatusBadge.vue'
import MarkdownView from '@/components/markdown/MarkdownView.vue'
import type { MarkdownFile } from '@/components/markdown/types'
import { useNotify } from '@/composables/useNotify'
import {
  dateTimeFromLocal,
  dateTimeToLocal,
  fieldWidget,
  fromDraft,
  pickableOptions,
  toDraft,
  valueError,
} from '@/domain/fields/fieldWidget'
import { optionPart, optionText, renderValue, type RenderContext } from '@/domain/fields/renderValue'
import type { FieldDefinitionRow } from '@/local/rows'
import FieldValue from './FieldValue.vue'
import RatingInput from './RatingInput.vue'

/**
 * A field of an idea or project, edited where it is shown (docs/01 §4.4, §6.1; docs/03 §12.4, §12.7) – the one switch
 * over the field types (domain/fields/fieldWidget.ts): texts, numbers, dates and switches with `InlineField` (Save /
 * Discard, Enter, Esc), ratings with one click, single and multi choices with their colours and points saved as picked,
 * persons by type-ahead, Markdown rendered with *Edit* to its editing page, scores read-only and live. A value the
 * field refuses stays in the editor with the reason underneath. `readonly`: why it can't be changed (shown as a
 * tooltip of the lock), or null.
 */
const props = withDefaults(
  defineProps<{
    field: FieldDefinitionRow
    /** The stored value (a score's computed result). */
    value: unknown
    save: (value: unknown) => Promise<unknown>
    readonly?: string | null
    context?: RenderContext
    overdue?: string | null
    /** For a Markdown field's *Edit*: the item's page route base and id. */
    itemType?: 'idea' | 'project'
    itemId?: string
    files?: readonly MarkdownFile[] | null
  }>(),
  { readonly: null, context: () => ({}), overdue: null, itemType: undefined, itemId: undefined, files: null },
)
const router = useRouter()
const notify = useNotify()
const widget = computed(() => fieldWidget(props.field))
const locked = computed(() => !!props.readonly)
const busy = ref(false)
const error = ref<string | null>(null)

/** Saves through the caller; a refused value shows its reason (and throws for InlineField, which keeps the editor). */
async function store(value: unknown, rethrow = false) {
  const problem = valueError(props.field, value)
  if (problem) {
    if (rethrow) throw new ApiError(400, { code: 'validation_failed', errors: { value: [problem] } })
    error.value = problem
    return
  }
  busy.value = true
  error.value = null
  try {
    await props.save(value)
  } catch (e) {
    if (rethrow) {
      if (e instanceof ApiError) throw e
      throw new ApiError(400, { code: 'validation_failed', errors: { value: [(e as Error).message] } })
    }
    notify.error(e, `${props.field.label} not saved`)
  } finally {
    busy.value = false
  }
}

// --- inline types -------------------------------------------------------------------------------------------------
type Draft = string | number | boolean | null
const inlineType = computed(() => {
  const t = widget.value.inline
  return t === 'text' || t === 'textarea' || t === 'number' || t === 'date' || t === 'bool' ? t : null
})
const draftValue = computed(() => toDraft(props.field, props.value))
const saveInline = (draft: Draft) => store(fromDraft(props.field, draft), true)
const display = (draft: Draft) => renderValue(props.field, fromDraft(props.field, draft), props.context).text

// --- choices ----------------------------------------------------------------------------------------------------
const chosen = computed(() =>
  Array.isArray(props.value) ? props.value.map(String) : typeof props.value === 'string' ? [props.value] : [],
)
const choiceOptions = computed(() => {
  const list = pickableOptions(props.field.config, chosen.value).map((o) => optionPart(props.field.config, o.id))
  // A chosen id the configuration lost stays visible (and can be removed).
  for (const id of chosen.value) if (!list.some((o) => o.id === id)) list.push(optionPart(props.field.config, id))
  return list
})
function pickOne(id: string | null) {
  if ((id ?? null) === (typeof props.value === 'string' ? props.value : null)) return
  void store(id)
}
function pickMany(ids: string[]) {
  void store(ids.length ? ids : null)
}

// --- persons ----------------------------------------------------------------------------------------------------
const multiple = computed(() => props.field.config?.multiple === true)
const adding = ref<string | null>(null)
function addPerson(id: string | null) {
  if (!id) return
  adding.value = null
  void store(multiple.value ? [...chosen.value.filter((p) => p !== id), id] : [id])
}
function removePerson(id: string) {
  const rest = chosen.value.filter((p) => p !== id)
  void store(rest.length ? rest : null)
}

// --- date-time --------------------------------------------------------------------------------------------------
const localTime = ref('')
const editingTime = ref(false)
function beginTime() {
  localTime.value = dateTimeToLocal(props.value)
  editingTime.value = true
}
async function saveTime() {
  await store(dateTimeFromLocal(localTime.value))
  if (!error.value) editingTime.value = false
}

// --- rating -----------------------------------------------------------------------------------------------------
const rating = computed(() => (props.value === 'na' || typeof props.value === 'number' ? props.value : null))

function editMarkdown() {
  if (!props.itemType || !props.itemId) return
  void router.push({ name: `${props.itemType}-field-edit`, params: { id: props.itemId, key: props.field.key } })
}
</script>

<template>
  <div class="field-editor" :class="`type-${field.type}`">
    <!-- computed, or read-only -->
    <FieldValue
      v-if="widget.computed || (locked && widget.type !== 'rating' && widget.type !== 'markdown')"
      :field="field"
      :value="value"
      :context="context"
      :overdue="overdue"
    />

    <!-- Markdown: rendered, edited on its own page -->
    <div v-else-if="widget.page" class="markdown">
      <MarkdownView v-if="typeof value === 'string' && value" :source="value" :files="files" />
      <span v-else class="muted">–</span>
      <Button
        v-if="!locked && itemType && itemId"
        label="Edit"
        icon="pi pi-pencil"
        size="small"
        text
        :aria-label="`Edit ${field.label}`"
        @click="editMarkdown"
      />
    </div>

    <!-- rating: one click -->
    <RatingInput
      v-else-if="widget.oneClick"
      :model-value="rating"
      :config="field.config"
      :label="field.label"
      :disabled="locked"
      :busy="busy"
      @update:model-value="(v) => store(v)"
    />

    <!-- texts, numbers, dates, switches -->
    <div v-else-if="inlineType" class="inline">
      <InlineField
        :value="draftValue"
        :save="saveInline"
        :type="inlineType"
        field="value"
        :label="field.label"
        :placeholder="(field.config?.placeholder as string | undefined) ?? '–'"
        :format="inlineType === 'bool' ? null : display"
      />
      <StatusBadge v-if="overdue" :overdue="overdue" />
    </div>

    <!-- single choice -->
    <Select
      v-else-if="widget.type === 'select'"
      :model-value="chosen[0] ?? null"
      :options="choiceOptions"
      option-label="label"
      option-value="id"
      :option-disabled="(o: { removed: boolean }) => o.removed"
      show-clear
      size="small"
      placeholder="–"
      :aria-label="field.label"
      :disabled="busy"
      class="choice"
      @update:model-value="pickOne"
    >
      <template #value>
        <FieldValue v-if="chosen.length" :field="field" :value="value" compact />
        <span v-else class="muted">–</span>
      </template>
      <template #option="{ option }">
        <span class="option" :class="{ removed: option.removed }">
          <span v-if="option.color" class="dot" :style="{ background: option.color }" aria-hidden="true" />
          {{ option.label }}
          <small v-if="option.points !== null" class="muted">+{{ option.points }}</small>
        </span>
      </template>
    </Select>

    <!-- multi choice -->
    <MultiSelect
      v-else-if="widget.type === 'multiselect'"
      :model-value="chosen"
      :options="choiceOptions"
      option-label="label"
      option-value="id"
      :option-disabled="(o: { removed: boolean; id: string }) => o.removed && !chosen.includes(o.id)"
      size="small"
      placeholder="–"
      :aria-label="field.label"
      :disabled="busy"
      :show-toggle-all="false"
      class="choice"
      @update:model-value="pickMany"
    >
      <template #value>
        <FieldValue v-if="chosen.length" :field="field" :value="value" />
        <span v-else class="muted">–</span>
      </template>
      <template #option="{ option }">
        <span class="option" :class="{ removed: option.removed }" :title="optionText(option)">
          <span v-if="option.color" class="dot" :style="{ background: option.color }" aria-hidden="true" />
          {{ option.label }}
          <small v-if="option.points !== null" class="muted">+{{ option.points }}</small>
        </span>
      </template>
    </MultiSelect>

    <!-- persons -->
    <div v-else-if="widget.type === 'person'" class="persons">
      <span v-for="id in chosen" :key="id" class="person">
        <i class="pi pi-user" aria-hidden="true" />{{ context.personName?.(id) ?? 'Unknown person' }}
        <Button
          icon="pi pi-times"
          size="small"
          text
          rounded
          severity="secondary"
          :aria-label="`Remove ${context.personName?.(id) ?? 'person'} from ${field.label}`"
          @click="removePerson(id)"
        />
      </span>
      <PersonPicker
        v-model="adding"
        :exclude="chosen"
        :placeholder="!chosen.length ? 'Name or e-mail…' : multiple ? 'Add a person…' : 'Replace by…'"
        :disabled="busy"
        @update:model-value="addPerson"
      />
    </div>

    <!-- date-time -->
    <div v-else-if="widget.type === 'datetime'" class="inline">
      <template v-if="editingTime">
        <InputText v-model="localTime" type="datetime-local" size="small" :aria-label="field.label" />
        <Button icon="pi pi-check" size="small" :loading="busy" :aria-label="`Save ${field.label}`" @click="saveTime" />
        <Button
          icon="pi pi-times"
          size="small"
          text
          severity="secondary"
          :aria-label="`Discard ${field.label}`"
          @click="editingTime = false"
        />
      </template>
      <button v-else type="button" class="shown" :aria-label="`Edit ${field.label}`" @click="beginTime">
        <FieldValue :field="field" :value="value" :overdue="overdue" />
        <i class="pi pi-pencil" aria-hidden="true" />
      </button>
    </div>

    <FieldValue v-else :field="field" :value="value" :context="context" />
    <div v-if="error" class="field-error" role="alert">{{ error }}</div>
  </div>
</template>

<style scoped>
.field-editor {
  min-width: 0;
}

.inline,
.markdown,
.persons {
  display: flex;
  align-items: center;
  flex-wrap: wrap;
  gap: var(--pgpm-space-1);
  min-width: 0;
}

.markdown {
  align-items: flex-start;
  flex-direction: column;
}

.choice {
  min-width: 10rem;
  max-width: 100%;
}

.option {
  display: inline-flex;
  align-items: center;
  gap: var(--pgpm-space-2);
}

.option.removed {
  font-style: italic;
  color: var(--pgpm-muted);
}

.dot {
  width: 0.5rem;
  height: 0.5rem;
  border-radius: 50%;
}

.person {
  display: inline-flex;
  align-items: center;
  gap: var(--pgpm-space-1);
}

.person i {
  font-size: 0.8rem;
  color: var(--pgpm-muted);
}

.shown {
  all: unset;
  display: inline-flex;
  align-items: center;
  gap: var(--pgpm-space-2);
  cursor: text;
  min-height: 28px;
  border-radius: var(--p-border-radius-sm);
}

.shown:focus-visible {
  outline: 1px solid var(--p-primary-color);
}

.shown i {
  font-size: 0.8rem;
  color: var(--pgpm-muted);
}
</style>

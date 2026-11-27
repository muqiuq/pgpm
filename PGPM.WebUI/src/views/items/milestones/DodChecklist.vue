<script setup lang="ts">
import Button from 'primevue/button'
import Checkbox from 'primevue/checkbox'
import InputText from 'primevue/inputtext'
import ProgressBar from 'primevue/progressbar'
import { computed, nextTick, ref } from 'vue'
import InlineField from '@/components/InlineField.vue'
import { useNotify } from '@/composables/useNotify'
import { dodProgress, progressText } from '@/domain/milestones'
import { useUserPersons } from '@/local/lookups'
import { addDodItem, moveDodItem, removeDodItem, renameDodItem, setDodState } from '@/local/milestones'
import { repo } from '@/local/repo'
import type { DodItemRow, DodState, Stored } from '@/local/rows'

/**
 * A milestone's Definition of Done (docs/01 §11, docs/04 F7): ordered criteria, each *open*, *done* (the checkbox) or
 * *n/a*, with an optional note – the evidence, a link or short text – that *n/a* requires (asked for inline). Progress
 * = done ÷ (all − n/a). Who changed a state and when is set by the server. Read-only without *edit*.
 */
const props = defineProps<{ milestoneId: string; items: readonly Stored<DodItemRow>[]; canEdit: boolean }>()
const notify = useNotify()
const people = useUserPersons()

const progress = computed(() => dodProgress(props.items))
const percent = computed(() => Math.round(progress.value.ratio * 100))

/** The criterion whose note is being written, and for which state. */
const noting = ref<{ id: string; state: DodState } | null>(null)
const noteText = ref('')
const noteError = ref<string | null>(null)
const newText = ref('')
const adding = ref(false)

async function run(action: () => Promise<unknown>, failure: string) {
  try {
    await action()
    return true
  } catch (e) {
    notify.error(e, failure)
    return false
  }
}

function toggle(item: Stored<DodItemRow>, checked: boolean) {
  void run(() => setDodState(repo, item.id, checked ? 'done' : 'open'), 'Not changed')
}

async function askNote(item: Stored<DodItemRow>, state: DodState) {
  noting.value = { id: item.id, state }
  noteText.value = item.note ?? ''
  noteError.value = null
  await nextTick()
  document.getElementById(`dod-note-${item.id}`)?.focus()
}

function notApplicable(item: Stored<DodItemRow>) {
  if (item.state === 'na') {
    void run(() => setDodState(repo, item.id, 'open'), 'Not changed')
    return
  }
  if (item.note?.trim()) {
    void run(() => setDodState(repo, item.id, 'na'), 'Not changed')
    return
  }
  void askNote(item, 'na')
}

async function saveNote() {
  const target = noting.value
  if (!target) return
  if (target.state === 'na' && !noteText.value.trim()) {
    noteError.value = 'n/a needs a note: why it doesn’t apply.'
    return
  }
  if (await run(() => setDodState(repo, target.id, target.state, noteText.value), 'Note not saved')) noting.value = null
}

async function add() {
  if (!newText.value.trim()) return
  adding.value = true
  if (await run(() => addDodItem(repo, props.milestoneId, newText.value), 'Criterion not added')) newText.value = ''
  adding.value = false
}

const stateTitle = (item: Stored<DodItemRow>) => {
  if (!item.stateChangedAt || item.state === 'open') return undefined
  const who = people.nameOf(item.stateChangedBy, 'Someone')
  return `${item.state === 'na' ? 'Set n/a' : 'Done'} by ${who}, ${new Date(item.stateChangedAt).toLocaleString()}`
}
</script>

<template>
  <div class="dod" :data-testid="`dod-${milestoneId}`">
    <div class="head">
      <span class="label">Definition of Done</span>
      <template v-if="progress.total">
        <ProgressBar :value="percent" :show-value="false" class="bar" :aria-label="`Progress ${percent} %`" />
        <span class="muted count" data-testid="dod-progress">{{ progressText(progress) }}</span>
        <span v-if="progress.na" class="muted count">({{ progress.na }} n/a)</span>
      </template>
    </div>
    <p v-if="!items.length && !canEdit" class="muted empty">No criteria.</p>
    <ul class="list">
      <li v-for="(item, i) in items" :key="item.id" :class="['row', item.state]">
        <Checkbox
          :model-value="item.state === 'done'"
          binary
          :input-id="`dod-${item.id}`"
          :disabled="!canEdit || item.state === 'na'"
          :aria-label="`Done: ${item.text}`"
          @update:model-value="(v: boolean) => toggle(item, v)"
        />
        <div class="body" :title="stateTitle(item)">
          <InlineField
            :value="item.text"
            :save="(v) => renameDodItem(repo, item.id, String(v ?? ''))"
            label="Criterion"
            field="text"
            :readonly="!canEdit"
          />
          <form v-if="noting?.id === item.id" class="note-edit" @submit.prevent="saveNote">
            <InputText
              :id="`dod-note-${item.id}`"
              v-model="noteText"
              size="small"
              maxlength="2000"
              :placeholder="noting.state === 'na' ? 'Why doesn’t it apply?' : 'Evidence: a link or short text'"
              :invalid="!!noteError"
              aria-label="Note"
            />
            <Button type="submit" :label="noting.state === 'na' ? 'Mark n/a' : 'Save note'" size="small" />
            <Button label="Cancel" size="small" severity="secondary" text @click="noting = null" />
            <small v-if="noteError" class="field-error" role="alert">{{ noteError }}</small>
          </form>
          <small v-else-if="item.note" class="muted note">
            <i class="pi pi-paperclip" aria-hidden="true" /> {{ item.note }}
          </small>
        </div>
        <span class="tools">
          <span v-if="item.state === 'na'" class="na-tag">n/a</span>
          <template v-if="canEdit">
            <Button
              :label="item.state === 'na' ? 'Applies' : 'n/a'"
              size="small"
              text
              :aria-label="item.state === 'na' ? `Applies again: ${item.text}` : `Not applicable: ${item.text}`"
              @click="notApplicable(item)"
            />
            <Button
              v-tooltip.top="'Note (evidence)'"
              icon="pi pi-paperclip"
              size="small"
              text
              severity="secondary"
              :aria-label="`Note for ${item.text}`"
              @click="askNote(item, item.state)"
            />
            <Button
              icon="pi pi-arrow-up"
              size="small"
              text
              severity="secondary"
              :disabled="i === 0"
              :aria-label="`Move ${item.text} up`"
              @click="run(() => moveDodItem(repo, item.id, -1), 'Not moved')"
            />
            <Button
              icon="pi pi-arrow-down"
              size="small"
              text
              severity="secondary"
              :disabled="i === items.length - 1"
              :aria-label="`Move ${item.text} down`"
              @click="run(() => moveDodItem(repo, item.id, 1), 'Not moved')"
            />
            <Button
              icon="pi pi-times"
              size="small"
              text
              severity="secondary"
              :aria-label="`Remove ${item.text}`"
              @click="run(() => removeDodItem(repo, item.id), 'Not removed')"
            />
          </template>
        </span>
      </li>
    </ul>
    <form v-if="canEdit" class="add" @submit.prevent="add">
      <InputText
        v-model="newText"
        size="small"
        maxlength="500"
        placeholder="New criterion…"
        aria-label="New criterion"
      />
      <Button type="submit" label="Add" icon="pi pi-plus" size="small" :disabled="!newText.trim()" :loading="adding" />
    </form>
  </div>
</template>

<style scoped>
.dod {
  display: flex;
  flex-direction: column;
  gap: var(--pgpm-space-1);
}

.head {
  display: flex;
  align-items: center;
  gap: var(--pgpm-space-2);
}

.label {
  font-weight: 600;
}

.bar {
  width: 8rem;
  height: 0.375rem;
}

.count {
  font-size: 0.857rem;
}

.list {
  list-style: none;
  margin: 0;
  padding: 0;
}

.row {
  display: flex;
  align-items: flex-start;
  gap: var(--pgpm-space-2);
  padding: 0.125rem 0;
}

.row :deep(.p-checkbox) {
  margin-top: 0.375rem;
}

.row.na .body {
  color: var(--pgpm-muted);
}

.row.done .body :deep(.shown .text) {
  text-decoration: line-through;
  text-decoration-color: var(--pgpm-muted);
}

.body {
  flex: 1;
  min-width: 0;
  display: flex;
  flex-direction: column;
}

.note {
  overflow-wrap: anywhere;
}

.tools {
  display: inline-flex;
  align-items: center;
  flex: none;
}

.na-tag {
  font-size: 0.786rem;
  padding: 0 0.375rem;
  border-radius: 999px;
  border: 1px solid var(--pgpm-border);
  color: var(--pgpm-muted);
}

.note-edit,
.add {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: var(--pgpm-space-1);
}

.note-edit :deep(input),
.add :deep(input) {
  flex: 1;
  min-width: 12rem;
}

.empty {
  margin: 0;
}
</style>

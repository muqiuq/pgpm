<script setup lang="ts">
import Button from 'primevue/button'
import InputNumber from 'primevue/inputnumber'
import InputText from 'primevue/inputtext'
import Select from 'primevue/select'
import SelectButton from 'primevue/selectbutton'
import Textarea from 'primevue/textarea'
import type { MilestoneKind, StatusRow } from '@/local/rows'
import { newMilestone, offsetText, type MilestoneForm } from './templateForm'

/**
 * A template's milestones and gates (docs/01 §10.2, §11): in order, each with kind, title, due date relative to the
 * project start (in days, shown as "+8 weeks"), description, the Definition of Done criteria and – for gates – the
 * statuses to suggest on *go* and *no-go*. v-model is the list.
 */
const props = withDefaults(
  defineProps<{ statuses: readonly StatusRow[]; errors?: Record<string, string>; disabled?: boolean }>(),
  { errors: () => ({}), disabled: false },
)
const milestones = defineModel<MilestoneForm[]>({ required: true })

const kinds: { value: MilestoneKind; label: string }[] = [
  { value: 'milestone', label: 'Milestone' },
  { value: 'gate', label: 'Gate' },
]

function update(i: number, change: Partial<MilestoneForm>) {
  milestones.value = milestones.value.map((m, j) => (j === i ? { ...m, ...change } : m))
}

function move(i: number, step: -1 | 1) {
  const list = [...milestones.value]
  const j = i + step
  if (j < 0 || j >= list.length) return
  ;[list[i], list[j]] = [list[j]!, list[i]!]
  milestones.value = list
}

function add(kind: MilestoneKind) {
  const last = milestones.value.reduce((max, m) => Math.max(max, m.offsetDays ?? 0), 0)
  milestones.value = [...milestones.value, newMilestone(kind, milestones.value.length ? last + 28 : 0)]
}

const remove = (i: number) => (milestones.value = milestones.value.filter((_, j) => j !== i))

function setDod(i: number, list: string[]) {
  update(i, { dod: list })
}

function moveDod(i: number, d: number, step: -1 | 1) {
  const list = [...milestones.value[i]!.dod]
  const j = d + step
  if (j < 0 || j >= list.length) return
  ;[list[d], list[j]] = [list[j]!, list[d]!]
  setDod(i, list)
}

const statusOptions = () => [
  { value: null, label: 'No suggestion' },
  ...props.statuses.map((s) => ({ value: s.id, label: s.name })),
]
</script>

<template>
  <div class="milestones-editor">
    <ol v-if="milestones.length" class="list">
      <li v-for="(m, i) in milestones" :key="m.uid" class="milestone" :data-testid="`milestone-${i}`">
        <div class="head">
          <span class="order">
            <Button
              icon="pi pi-angle-up"
              text
              size="small"
              severity="secondary"
              :disabled="disabled || i === 0"
              :aria-label="`Move ${m.title || `milestone ${i + 1}`} up`"
              @click="move(i, -1)"
            />
            <Button
              icon="pi pi-angle-down"
              text
              size="small"
              severity="secondary"
              :disabled="disabled || i === milestones.length - 1"
              :aria-label="`Move ${m.title || `milestone ${i + 1}`} down`"
              @click="move(i, 1)"
            />
          </span>
          <i :class="m.kind === 'gate' ? 'pi pi-stop diamond' : 'pi pi-circle-fill dot'" aria-hidden="true" />
          <SelectButton
            :model-value="m.kind"
            :options="kinds"
            option-label="label"
            option-value="value"
            :allow-empty="false"
            size="small"
            :aria-label="`Kind of milestone ${i + 1}`"
            :disabled="disabled"
            @update:model-value="(k: MilestoneKind) => update(i, { kind: k })"
          />
          <div class="cell title">
            <InputText
              :id="`ms-title-${i}`"
              :model-value="m.title"
              size="small"
              maxlength="300"
              placeholder="Title"
              :aria-label="`Title of milestone ${i + 1}`"
              :invalid="!!errors[`milestones.${i}.title`]"
              :disabled="disabled"
              @update:model-value="(v) => update(i, { title: v ?? '' })"
            />
            <small v-if="errors[`milestones.${i}.title`]" class="field-error">{{
              errors[`milestones.${i}.title`]
            }}</small>
          </div>
          <div class="cell offset">
            <InputNumber
              :model-value="m.offsetDays"
              :input-id="`ms-offset-${i}`"
              size="small"
              suffix=" d"
              placeholder="Days"
              :use-grouping="false"
              :aria-label="`Due days after the start of milestone ${i + 1}`"
              :disabled="disabled"
              @update:model-value="(v: number | null) => update(i, { offsetDays: v })"
            />
            <small class="muted">{{ offsetText(m.offsetDays) }}</small>
          </div>
          <Button
            icon="pi pi-trash"
            text
            size="small"
            severity="secondary"
            :disabled="disabled"
            :aria-label="`Remove ${m.title || `milestone ${i + 1}`}`"
            @click="remove(i)"
          />
        </div>
        <div class="body">
          <Textarea
            :model-value="m.description"
            rows="2"
            auto-resize
            placeholder="Description (Markdown)"
            :aria-label="`Description of milestone ${i + 1}`"
            :disabled="disabled"
            @update:model-value="(v) => update(i, { description: v ?? '' })"
          />
          <div v-if="m.kind === 'gate'" class="form-grid">
            <div class="field">
              <label :for="`ms-go-${i}`">On <strong>go</strong> suggest</label>
              <Select
                :model-value="m.onGoStatusId"
                :input-id="`ms-go-${i}`"
                :options="statusOptions()"
                option-label="label"
                option-value="value"
                size="small"
                :invalid="!!errors[`milestones.${i}.onGoStatusId`]"
                :disabled="disabled"
                @update:model-value="(v: string | null) => update(i, { onGoStatusId: v })"
              />
              <small v-if="errors[`milestones.${i}.onGoStatusId`]" class="field-error">{{
                errors[`milestones.${i}.onGoStatusId`]
              }}</small>
            </div>
            <div class="field">
              <label :for="`ms-nogo-${i}`">On <strong>no-go</strong> suggest</label>
              <Select
                :model-value="m.onNoGoStatusId"
                :input-id="`ms-nogo-${i}`"
                :options="statusOptions()"
                option-label="label"
                option-value="value"
                size="small"
                :invalid="!!errors[`milestones.${i}.onNoGoStatusId`]"
                :disabled="disabled"
                @update:model-value="(v: string | null) => update(i, { onNoGoStatusId: v })"
              />
              <small v-if="errors[`milestones.${i}.onNoGoStatusId`]" class="field-error">{{
                errors[`milestones.${i}.onNoGoStatusId`]
              }}</small>
            </div>
          </div>
          <div class="dod">
            <span class="label">Definition of Done</span>
            <ol v-if="m.dod.length" class="criteria">
              <li v-for="(text, d) in m.dod" :key="d">
                <span class="order">
                  <Button
                    icon="pi pi-angle-up"
                    text
                    size="small"
                    severity="secondary"
                    :disabled="disabled || d === 0"
                    :aria-label="`Move criterion ${d + 1} up`"
                    @click="moveDod(i, d, -1)"
                  />
                  <Button
                    icon="pi pi-angle-down"
                    text
                    size="small"
                    severity="secondary"
                    :disabled="disabled || d === m.dod.length - 1"
                    :aria-label="`Move criterion ${d + 1} down`"
                    @click="moveDod(i, d, 1)"
                  />
                </span>
                <InputText
                  :model-value="text"
                  size="small"
                  maxlength="500"
                  :aria-label="`Criterion ${d + 1} of milestone ${i + 1}`"
                  :disabled="disabled"
                  @update:model-value="
                    (v) =>
                      setDod(
                        i,
                        m.dod.map((x, k) => (k === d ? (v ?? '') : x)),
                      )
                  "
                />
                <Button
                  icon="pi pi-times"
                  text
                  size="small"
                  severity="secondary"
                  :disabled="disabled"
                  :aria-label="`Remove criterion ${d + 1}`"
                  @click="
                    setDod(
                      i,
                      m.dod.filter((_, k) => k !== d),
                    )
                  "
                />
              </li>
            </ol>
            <small v-if="errors[`milestones.${i}.dod`]" class="field-error">{{ errors[`milestones.${i}.dod`] }}</small>
            <Button
              label="Add criterion"
              icon="pi pi-plus"
              size="small"
              text
              :disabled="disabled"
              @click="setDod(i, [...m.dod, ''])"
            />
          </div>
        </div>
      </li>
    </ol>
    <p v-else class="muted none">No milestones or gates.</p>
    <span class="add">
      <Button
        label="Add milestone"
        icon="pi pi-plus"
        size="small"
        text
        :disabled="disabled"
        @click="add('milestone')"
      />
      <Button label="Add gate" icon="pi pi-plus" size="small" text :disabled="disabled" @click="add('gate')" />
    </span>
  </div>
</template>

<style scoped>
.milestones-editor {
  display: flex;
  flex-direction: column;
  gap: var(--pgpm-space-2);
}

.list {
  list-style: none;
  margin: 0;
  padding: 0;
  display: flex;
  flex-direction: column;
  gap: var(--pgpm-space-2);
}

.milestone {
  border: 1px solid var(--pgpm-border);
  border-radius: var(--p-border-radius-md);
  padding: var(--pgpm-space-2);
  display: flex;
  flex-direction: column;
  gap: var(--pgpm-space-2);
}

.head {
  display: flex;
  align-items: flex-start;
  gap: var(--pgpm-space-2);
  flex-wrap: wrap;
}

.order {
  display: inline-flex;
  flex: none;
}

.diamond {
  transform: rotate(45deg);
  margin-top: 8px;
  color: var(--p-primary-color);
}

.dot {
  margin-top: 8px;
  font-size: 0.7rem;
  color: var(--pgpm-muted);
}

.cell {
  display: flex;
  flex-direction: column;
  min-width: 0;
}

.title {
  flex: 1;
  min-width: 12rem;
}

.offset {
  width: 8rem;
}

.offset :deep(input) {
  width: 100%;
}

.body {
  display: flex;
  flex-direction: column;
  gap: var(--pgpm-space-2);
  padding-left: 4.5rem;
}

.dod {
  display: flex;
  flex-direction: column;
  align-items: flex-start;
  gap: var(--pgpm-space-1);
}

.label {
  font-weight: 500;
}

.criteria {
  list-style: none;
  margin: 0;
  padding: 0;
  width: 100%;
  display: flex;
  flex-direction: column;
  gap: 2px;
}

.criteria li {
  display: flex;
  align-items: center;
  gap: var(--pgpm-space-1);
}

.criteria li :deep(input) {
  flex: 1;
}

.none {
  margin: 0;
}

.add {
  display: inline-flex;
  gap: var(--pgpm-space-1);
}

@media (max-width: 600px) {
  .body {
    padding-left: 0;
  }
}
</style>

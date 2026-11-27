<script setup lang="ts">
import Button from 'primevue/button'
import Checkbox from 'primevue/checkbox'
import Popover from 'primevue/popover'
import { computed, ref } from 'vue'

/**
 * The column chooser of a list (docs/01 §4.3): show, hide and reorder the columns; the required ones (key, title) stay.
 * v-model is the shown column ids in order; `columns` every column the list can show; `defaults` what *Reset* restores.
 */
const props = defineProps<{
  columns: readonly { id: string; label: string; required?: boolean }[]
  defaults: readonly string[]
}>()
const shown = defineModel<string[]>({ required: true })
const panel = ref<InstanceType<typeof Popover> | null>(null)
const id = `columns-${Math.random().toString(36).slice(2, 8)}`

/** The shown ones in their order, then the hidden ones in the list's order. */
const rows = computed(() => {
  const known = new Map(props.columns.map((c) => [c.id, c]))
  const visible = shown.value.filter((c) => known.has(c)).map((c) => ({ ...known.get(c)!, visible: true }))
  const hidden = props.columns.filter((c) => !shown.value.includes(c.id)).map((c) => ({ ...c, visible: false }))
  return [...visible, ...hidden]
})

function toggle(columnId: string, on: boolean) {
  const column = props.columns.find((c) => c.id === columnId)
  if (column?.required) return
  shown.value = on
    ? [...shown.value.filter((c) => c !== columnId), columnId]
    : shown.value.filter((c) => c !== columnId)
}

function move(columnId: string, by: -1 | 1) {
  const list = shown.value.filter((c) => props.columns.some((k) => k.id === c))
  const at = list.indexOf(columnId)
  const to = at + by
  if (at < 0 || to < 0 || to >= list.length) return
  ;[list[at], list[to]] = [list[to]!, list[at]!]
  shown.value = list
}
</script>

<template>
  <Button
    icon="pi pi-table"
    label="Columns"
    size="small"
    severity="secondary"
    outlined
    aria-haspopup="dialog"
    :aria-controls="id"
    @click="(e: Event) => panel?.toggle(e)"
  />
  <Popover :id="id" ref="panel">
    <div class="column-chooser" role="group" aria-label="Columns">
      <ul>
        <li v-for="(c, i) in rows" :key="c.id">
          <Checkbox
            :input-id="`${id}-${c.id}`"
            :model-value="c.visible"
            binary
            :disabled="c.required"
            @update:model-value="(on: boolean) => toggle(c.id, on)"
          />
          <label :for="`${id}-${c.id}`" class="name">{{ c.label }}</label>
          <template v-if="c.visible">
            <Button
              icon="pi pi-arrow-up"
              text
              rounded
              size="small"
              severity="secondary"
              :disabled="i === 0"
              :aria-label="`Move ${c.label} up`"
              @click="move(c.id, -1)"
            />
            <Button
              icon="pi pi-arrow-down"
              text
              rounded
              size="small"
              severity="secondary"
              :disabled="i === shown.length - 1 || !rows[i + 1]?.visible"
              :aria-label="`Move ${c.label} down`"
              @click="move(c.id, 1)"
            />
          </template>
        </li>
      </ul>
      <Button label="Reset" size="small" text @click="shown = [...defaults]" />
    </div>
  </Popover>
</template>

<style scoped>
.column-chooser ul {
  list-style: none;
  margin: 0 0 var(--pgpm-space-2);
  padding: 0;
  min-width: 15rem;
}

.column-chooser li {
  display: flex;
  align-items: center;
  gap: var(--pgpm-space-2);
  min-height: 32px;
}

.name {
  flex: 1;
}
</style>

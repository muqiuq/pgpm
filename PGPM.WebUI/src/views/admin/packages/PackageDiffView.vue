<script setup lang="ts">
import Checkbox from 'primevue/checkbox'
import Tag from 'primevue/tag'
import ToggleSwitch from 'primevue/toggleswitch'
import { computed, ref } from 'vue'
import type { PackageDiff } from '@/api/config'
import { actionLabels, actionOf, conflictKeys, groupDiff, toggleKey, type DiffAction } from './packageDiff'

/**
 * A dry run's diff (docs/03 §17): the counts, then the entries grouped by entity – *New*, *Update*, *Unchanged*,
 * *Changed here* (a conflict: edited in this installation since the last import) – with the members that differ and
 * whether an existing entity was adopted. Conflicts are kept unless chosen to be overwritten; v-model is the chosen
 * keys.
 */
const props = defineProps<{ diff: PackageDiff; disabled?: boolean }>()
const overwrite = defineModel<string[]>('overwrite', { default: () => [] })

const hideUnchanged = ref(true)
const groups = computed(() => groupDiff(props.diff, { hideUnchanged: hideUnchanged.value }))
const conflicts = computed(() => conflictKeys(props.diff))
/** Chosen keys the new dry run shows as updates (overwritten) stay selectable. */
const selectable = computed(() => new Set([...conflicts.value, ...overwrite.value]))
const allChosen = computed(
  () => conflicts.value.length > 0 && conflicts.value.every((k) => overwrite.value.includes(k)),
)

const severities: Record<DiffAction, 'success' | 'info' | 'secondary' | 'warn'> = {
  create: 'success',
  update: 'info',
  unchanged: 'secondary',
  conflict: 'warn',
}

function chooseAll(on: boolean) {
  overwrite.value = on
    ? [...new Set([...overwrite.value, ...conflicts.value])]
    : overwrite.value.filter((k) => !conflicts.value.includes(k))
}
</script>

<template>
  <div class="package-diff">
    <div class="counts" data-testid="diff-counts">
      <Tag :value="`${diff.counts.create} new`" severity="success" />
      <Tag :value="`${diff.counts.update} to update`" severity="info" />
      <Tag :value="`${diff.counts.unchanged} unchanged`" severity="secondary" />
      <Tag :value="`${diff.counts.conflict} changed here`" severity="warn" />
      <span class="switch">
        <ToggleSwitch v-model="hideUnchanged" input-id="pd-hide" />
        <label for="pd-hide">Hide unchanged</label>
      </span>
    </div>
    <div v-if="conflicts.length" class="choose-all">
      <Checkbox
        :model-value="allChosen"
        binary
        input-id="pd-all"
        :disabled="disabled"
        @update:model-value="(on: boolean) => chooseAll(on)"
      />
      <label for="pd-all">Overwrite all {{ conflicts.length }} entities changed here</label>
    </div>
    <p v-if="!groups.length" class="muted none">Nothing differs: applying it changes nothing.</p>
    <section v-for="g in groups" :key="g.entity" class="group" :data-testid="`diff-${g.entity}`">
      <h3>
        {{ g.label }}
        <small class="muted">
          <template v-for="a in ['create', 'update', 'conflict', 'unchanged'] as const" :key="a">
            <template v-if="g.counts[a]"> · {{ g.counts[a] }} {{ actionLabels[a].toLowerCase() }}</template>
          </template>
        </small>
      </h3>
      <ul>
        <li v-for="e in g.entries" :key="e.key" :data-testid="`entry-${e.key}`">
          <Tag :value="actionLabels[actionOf(e)]" :severity="severities[actionOf(e)]" class="action" />
          <span class="name">
            <span class="ellipsis">{{ e.name }}</span>
            <small class="mono muted ellipsis">{{ e.key }}</small>
          </span>
          <Tag
            v-if="e.adopted"
            value="Adopted"
            severity="secondary"
            title="Matched an existing entity by its name or key"
          />
          <small v-if="e.changed.length" class="muted changed ellipsis" :title="e.changed.join(', ')">
            {{ e.changed.join(', ') }}
          </small>
          <span v-if="selectable.has(e.key)" class="overwrite">
            <Checkbox
              :model-value="overwrite.includes(e.key)"
              binary
              :input-id="`pd-ow-${e.key}`"
              :disabled="disabled"
              @update:model-value="(on: boolean) => (overwrite = toggleKey(overwrite, e.key, on))"
            />
            <label :for="`pd-ow-${e.key}`">Overwrite</label>
          </span>
        </li>
      </ul>
    </section>
  </div>
</template>

<style scoped>
.package-diff {
  display: flex;
  flex-direction: column;
  gap: var(--pgpm-space-3);
}

.counts,
.choose-all,
.switch {
  display: flex;
  align-items: center;
  gap: var(--pgpm-space-2);
  flex-wrap: wrap;
}

.switch {
  margin-left: auto;
}

.none {
  margin: 0;
}

.group h3 {
  font-size: 1rem;
  margin: 0 0 var(--pgpm-space-1);
}

.group ul {
  list-style: none;
  margin: 0;
  padding: 0;
}

.group li {
  display: flex;
  align-items: center;
  gap: var(--pgpm-space-2);
  min-height: 36px;
  border-bottom: 1px solid var(--pgpm-border);
}

.group li:last-child {
  border-bottom: none;
}

.action {
  width: 7rem;
  justify-content: center;
  flex: none;
}

.name {
  display: flex;
  flex-direction: column;
  min-width: 0;
  flex: 1;
}

.changed {
  max-width: 16rem;
}

.overwrite {
  display: inline-flex;
  align-items: center;
  gap: var(--pgpm-space-1);
  flex: none;
}
</style>

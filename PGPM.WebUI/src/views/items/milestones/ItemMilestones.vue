<script setup lang="ts">
import Button from 'primevue/button'
import { computed, nextTick, ref } from 'vue'
import EmptyState from '@/components/EmptyState.vue'
import { useNow } from '@/composables/useNow'
import { compareMilestones, countsAsDone } from '@/domain/milestones'
import { localDb } from '@/local/db'
import { useMarkdownFiles } from '@/local/files'
import { today } from '@/local/items'
import type { AssignmentRow, DodItemRow, ItemRow, MilestoneRow, Stored } from '@/local/rows'
import { useLive } from '@/local/useLive'
import { groupBy } from '@/views/items/itemList'
import MilestoneCard from './MilestoneCard.vue'
import MilestoneDialog from './MilestoneDialog.vue'
import MilestoneTimeline from './MilestoneTimeline.vue'

/**
 * A project's *Milestones* tab (docs/01 §11): the milestones and gates in their order with a mini timeline above
 * (gates as diamonds), *New milestone*, and per milestone its status, due date, persons, description, Definition of
 * Done and – gates – the decision. Everything is local data (offline too); read-only without *edit*. A link to the
 * *Gate decisions* of all projects.
 */
const props = defineProps<{ item: Stored<ItemRow>; canEdit: boolean }>()
const now = useNow()
const files = useMarkdownFiles(() => props.item.id)

const milestones = useLive(
  async () =>
    (
      (await localDb().synced('milestones').where('itemId').equals(props.item.id).toArray()) as unknown as Stored<
        MilestoneRow[]
      >
    )
      .filter((m) => !m.deleted)
      .sort(compareMilestones),
  [] as Stored<MilestoneRow>[],
  () => props.item.id,
)
const ids = computed(() => milestones.value.value.map((m) => m.id))
const dod = useLive(
  async () =>
    ids.value.length
      ? ((await localDb().synced('dod_items').where('milestoneId').anyOf(ids.value).toArray()) as unknown as Stored<
          DodItemRow[]
        >)
      : [],
  [] as Stored<DodItemRow>[],
  () => ids.value.join(','),
)
const assignments = useLive(
  async () =>
    ids.value.length
      ? ((await localDb().synced('assignments').where('milestoneId').anyOf(ids.value).toArray()) as unknown as Stored<
          AssignmentRow[]
        >)
      : [],
  [] as Stored<AssignmentRow>[],
  () => ids.value.join(','),
)
const byOrder = <T extends { sortOrder: string; id: string }>(rows: T[]) =>
  [...rows].sort((a, b) => (a.sortOrder < b.sortOrder ? -1 : a.sortOrder > b.sortOrder ? 1 : a.id < b.id ? -1 : 1))
const dodBy = computed(() => groupBy(byOrder(dod.value.value), (d) => d.milestoneId))
const assignmentsBy = computed(() => groupBy(byOrder(assignments.value.value), (a) => a.milestoneId))

const day = computed(() => today(now.value))
const doneCount = computed(
  () => milestones.value.value.filter((m) => countsAsDone(m, dodBy.value.get(m.id) ?? [])).length,
)
const creating = ref(false)
const selected = ref<string | null>(null)

async function select(id: string) {
  selected.value = id
  await nextTick()
  document.getElementById(`milestone-${id}`)?.scrollIntoView?.({ behavior: 'smooth', block: 'start' })
}
</script>

<template>
  <div class="milestones">
    <div class="toolbar">
      <span class="muted" data-testid="milestones-summary">
        <template v-if="milestones.value.value.length">
          {{ doneCount }} of {{ milestones.value.value.length }} done
        </template>
      </span>
      <span class="actions">
        <RouterLink :to="{ name: 'gates' }" class="all-gates">Gate decisions of all projects</RouterLink>
        <Button
          v-if="canEdit"
          label="New milestone"
          icon="pi pi-plus"
          size="small"
          :disabled="item.deleted"
          @click="creating = true"
        />
      </span>
    </div>
    <section v-if="milestones.value.value.length" class="panel">
      <div class="panel-body">
        <MilestoneTimeline :milestones="milestones.value.value" :today="day" @select="select" />
      </div>
    </section>
    <EmptyState
      v-if="milestones.loaded.value && !milestones.value.value.length"
      icon="pi pi-flag"
      title="No milestones yet."
      :text="
        canEdit
          ? 'Plan milestones and gates with their Definition of Done – a template brings them along.'
          : 'Nobody planned any milestones for this project yet.'
      "
    />
    <MilestoneCard
      v-for="(m, i) in milestones.value.value"
      :key="m.id"
      :milestone="m"
      :project="item"
      :dod="dodBy.get(m.id) ?? []"
      :assignments="assignmentsBy.get(m.id) ?? []"
      :can-edit="canEdit"
      :today="day"
      :files="files"
      :first="i === 0"
      :last="i === milestones.value.value.length - 1"
      :selected="selected === m.id"
    />
    <MilestoneDialog v-model:visible="creating" :item-id="item.id" @created="(m) => select(m.id)" />
  </div>
</template>

<style scoped>
.milestones {
  display: flex;
  flex-direction: column;
  gap: var(--pgpm-space-4);
}

.toolbar {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: var(--pgpm-space-3);
  flex-wrap: wrap;
}

.actions {
  display: inline-flex;
  align-items: center;
  gap: var(--pgpm-space-3);
}

.all-gates {
  font-size: 0.857rem;
}
</style>

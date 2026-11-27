<script setup lang="ts">
import Button from 'primevue/button'
import Select from 'primevue/select'
import { useConfirm } from 'primevue/useconfirm'
import { computed } from 'vue'
import { useRouter } from 'vue-router'
import GateDecision from '@/components/items/GateDecision.vue'
import { useNotify } from '@/composables/useNotify'
import { itemRoute } from '@/domain/itemTypes'
import { overrideMessage, statusOptions } from '@/domain/milestones'
import { localDb } from '@/local/db'
import { updateMilestone } from '@/local/milestones'
import { repo } from '@/local/repo'
import {
  allowsAccess,
  type DodItemRow,
  type ItemRow,
  type MilestoneRow,
  type MilestoneStatus,
  type Stored,
} from '@/local/rows'
import { useLive } from '@/local/useLive'
import DodChecklist from '@/views/items/milestones/DodChecklist.vue'

/**
 * A milestone's or gate's work in the map's side panel (MapCanvas' `#milestone-panel`, docs/03 §13.7): its status –
 * *done* with an incomplete Definition of Done (or a gate without decision) after a confirmation, as on the project's
 * Milestones tab –, the Definition of Done checklist and a gate's decision with the suggested status change. The
 * project's access decides (not the map's), on the device, offline too.
 */
const props = defineProps<{ milestoneId: string }>()
const router = useRouter()
const notify = useNotify()
const confirm = useConfirm()

const data = useLive(
  async () => {
    const db = localDb()
    const milestone = (await db.synced('milestones').get(props.milestoneId)) as unknown as
      Stored<MilestoneRow> | undefined
    if (!milestone || milestone.deleted) return null
    const project = (await db.synced('items').get(milestone.itemId)) as unknown as Stored<ItemRow> | undefined
    const dod = (
      (await db.synced('dod_items').where('milestoneId').equals(milestone.id).toArray()) as unknown as Stored<
        DodItemRow[]
      >
    )
      .filter((d) => !d.deleted)
      .sort((a, b) => (a.sortOrder < b.sortOrder ? -1 : a.sortOrder > b.sortOrder ? 1 : a.id < b.id ? -1 : 1))
    return { milestone, project: project && !project.deleted ? project : null, dod }
  },
  null as { milestone: Stored<MilestoneRow>; project: Stored<ItemRow> | null; dod: Stored<DodItemRow>[] } | null,
  () => props.milestoneId,
)
const milestone = computed(() => data.value.value?.milestone ?? null)
const project = computed(() => data.value.value?.project ?? null)
const dod = computed(() => data.value.value?.dod ?? [])
const canEdit = computed(() => !!project.value && allowsAccess(project.value.access, 'edit'))

async function save(status: MilestoneStatus) {
  try {
    await updateMilestone(repo, props.milestoneId, { status })
  } catch (e) {
    notify.error(e, 'Status not changed')
  }
}

function setStatus(status: MilestoneStatus | null) {
  const m = milestone.value
  if (!m || !status || status === m.status) return
  const override = status === 'done' ? overrideMessage(m, dod.value) : null
  if (!override) {
    void save(status)
    return
  }
  confirm.require({
    header: `Mark ${m.title} done?`,
    message: override,
    icon: 'pi pi-exclamation-triangle',
    acceptProps: { label: 'Mark done anyway', severity: 'warn' },
    rejectProps: { label: 'Cancel', severity: 'secondary', text: true },
    accept: () => void save(status),
  })
}
</script>

<template>
  <section v-if="milestone" class="milestone-panel" :data-testid="`map-milestone-${milestone.id}`">
    <div v-if="canEdit" class="field">
      <label :id="`map-ms-status-${milestone.id}-label`" :for="`map-ms-status-${milestone.id}`">Status</label>
      <Select
        :model-value="milestone.status"
        :options="[...statusOptions]"
        option-label="label"
        option-value="value"
        size="small"
        :input-id="`map-ms-status-${milestone.id}`"
        :aria-labelledby="`map-ms-status-${milestone.id}-label`"
        @update:model-value="setStatus"
      />
    </div>
    <DodChecklist :milestone-id="milestone.id" :items="dod" :can-edit="canEdit" />
    <GateDecision
      v-if="milestone.kind === 'gate' && project"
      :milestone="milestone"
      :project="project"
      :can-edit="canEdit"
    />
    <Button
      v-if="project"
      label="Open the project’s milestones"
      icon="pi pi-external-link"
      size="small"
      text
      @click="router.push(itemRoute({ type: 'project', id: project.id }, 'milestones'))"
    />
  </section>
</template>

<style scoped>
.milestone-panel,
.field {
  display: flex;
  flex-direction: column;
  gap: var(--pgpm-space-2);
}
</style>

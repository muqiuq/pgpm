<script setup lang="ts">
import Button from 'primevue/button'
import { computed, ref } from 'vue'
import PersonPicker from '@/components/items/PersonPicker.vue'
import RoleInput from '@/components/items/RoleInput.vue'
import { useNotify } from '@/composables/useNotify'
import { usePersons } from '@/local/lookups'
import { assignToMilestone, GATE_ROLE, unassignFromMilestone } from '@/local/milestones'
import { repo } from '@/local/repo'
import type { AssignmentRow, MilestoneKind, Stored } from '@/local/rows'

/**
 * The persons responsible for a milestone (docs/01 §11): assignments with `milestoneId`, each person once; for a gate
 * the deciders, with the role *Review board* preset. Read-only without *edit*.
 */
const props = defineProps<{
  milestoneId: string
  kind: MilestoneKind
  assignments: readonly Stored<AssignmentRow>[]
  canEdit: boolean
}>()
const notify = useNotify()
const persons = usePersons()

const rows = computed(() =>
  props.assignments.map((a) => ({ ...a, person: persons.byId.value.get(a.personId) ?? null })),
)
const assigned = computed(() => props.assignments.map((a) => a.personId))
const adding = ref(false)
const personId = ref<string | null>(null)
const role = ref<string | null>(null)
const label = computed(() => (props.kind === 'gate' ? 'Deciders' : 'Responsible'))

function open() {
  personId.value = null
  role.value = props.kind === 'gate' ? GATE_ROLE : null
  adding.value = true
}

async function add() {
  if (!personId.value) return
  try {
    await assignToMilestone(repo, props.milestoneId, personId.value, role.value)
    adding.value = false
  } catch (e) {
    notify.error(e, 'Not assigned')
  }
}

async function remove(a: Stored<AssignmentRow>) {
  try {
    await unassignFromMilestone(repo, a.id)
  } catch (e) {
    notify.error(e, 'Not removed')
  }
}
</script>

<template>
  <div class="persons">
    <span class="label">{{ label }}</span>
    <span v-if="!rows.length && !adding" class="muted">Nobody yet.</span>
    <span v-for="a in rows" :key="a.id" class="chip">
      <RouterLink v-if="a.person" :to="{ name: 'person', params: { id: a.personId } }">{{
        a.person.displayName
      }}</RouterLink>
      <span v-else class="muted">Unknown person</span>
      <small v-if="a.role" class="muted">{{ a.role }}</small>
      <Button
        v-if="canEdit"
        icon="pi pi-times"
        size="small"
        text
        severity="secondary"
        :aria-label="`Remove ${a.person?.displayName ?? 'person'}`"
        @click="remove(a)"
      />
    </span>
    <Button
      v-if="canEdit && !adding"
      :label="kind === 'gate' ? 'Add decider' : 'Assign person'"
      icon="pi pi-user-plus"
      size="small"
      text
      @click="open"
    />
    <form v-if="adding" class="add" @submit.prevent="add">
      <PersonPicker
        v-model="personId"
        :exclude="assigned"
        :input-id="`ms-person-${milestoneId}`"
        placeholder="Person…"
      />
      <RoleInput v-model="role" :input-id="`ms-role-${milestoneId}`" />
      <Button type="submit" label="Assign" size="small" :disabled="!personId" />
      <Button label="Cancel" size="small" severity="secondary" text @click="adding = false" />
    </form>
  </div>
</template>

<style scoped>
.persons {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: var(--pgpm-space-2);
}

.label {
  font-weight: 600;
}

.chip {
  display: inline-flex;
  align-items: center;
  gap: var(--pgpm-space-1);
  padding: 0 0 0 var(--pgpm-space-2);
  border: 1px solid var(--pgpm-border);
  border-radius: 999px;
}

.add {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: var(--pgpm-space-1);
  width: 100%;
}
</style>

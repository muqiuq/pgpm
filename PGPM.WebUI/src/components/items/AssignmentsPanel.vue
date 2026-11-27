<script setup lang="ts">
import Button from 'primevue/button'
import Tag from 'primevue/tag'
import { computed, ref } from 'vue'
import { useNotify } from '@/composables/useNotify'
import { localDb } from '@/local/db'
import { assignmentsOf, assignPerson } from '@/local/items'
import { usePersons } from '@/local/lookups'
import { repo } from '@/local/repo'
import type { AssignmentRow, Stored } from '@/local/rows'
import { useLive } from '@/local/useLive'
import PersonPicker from './PersonPicker.vue'
import RoleInput from './RoleInput.vue'

/**
 * The persons assigned to an item (docs/01 §4.6): each once, with an optional role (free text, suggestions from
 * `assignment_roles`). Added from the directory by type-ahead (inactive persons aren't offered, but stay shown where
 * assigned). Written to the local data at once – offline too – and synced.
 */
const props = defineProps<{ itemId: string; canEdit: boolean }>()
const notify = useNotify()
const persons = usePersons()

const assignments = useLive(
  () => assignmentsOf(localDb(), props.itemId),
  [] as Stored<AssignmentRow>[],
  () => props.itemId,
)
const rows = computed(() =>
  assignments.value.value.map((a) => ({ ...a, person: persons.byId.value.get(a.personId) ?? null })),
)
const assigned = computed(() => assignments.value.value.map((a) => a.personId))

const adding = ref(false)
const personId = ref<string | null>(null)
const role = ref<string | null>(null)
const editing = ref<string | null>(null)
const editedRole = ref<string | null>(null)

async function add() {
  if (!personId.value) return
  try {
    await repo.transaction((tx) => assignPerson(tx, props.itemId, personId.value!, role.value))
    personId.value = null
    role.value = null
    adding.value = false
  } catch (e) {
    notify.error(e, 'Not assigned')
  }
}

function editRole(a: Stored<AssignmentRow>) {
  editing.value = a.id
  editedRole.value = a.role
}

async function saveRole(a: Stored<AssignmentRow>) {
  try {
    await repo.patch('assignments', a.id, { role: editedRole.value?.trim() || null })
    editing.value = null
  } catch (e) {
    notify.error(e, 'Role not saved')
  }
}

async function unassign(a: Stored<AssignmentRow>) {
  try {
    await repo.remove('assignments', a.id)
  } catch (e) {
    notify.error(e, 'Not removed')
  }
}
</script>

<template>
  <section class="panel" aria-labelledby="persons-title" data-testid="assignments">
    <div class="panel-header with-action">
      <span id="persons-title"
        >Persons <span v-if="rows.length" class="muted">({{ rows.length }})</span></span
      >
      <Button
        v-if="canEdit && !adding"
        label="Assign person"
        icon="pi pi-user-plus"
        size="small"
        text
        @click="adding = true"
      />
    </div>
    <div class="panel-body">
      <form v-if="adding" class="add" @submit.prevent="add">
        <PersonPicker v-model="personId" :exclude="assigned" input-id="assign-person" placeholder="Person…" />
        <RoleInput v-model="role" input-id="assign-role" />
        <span class="actions">
          <Button type="submit" label="Assign" size="small" :disabled="!personId" />
          <Button label="Cancel" size="small" severity="secondary" text @click="adding = false" />
        </span>
      </form>
      <p v-if="!rows.length && !adding" class="muted empty">Nobody assigned yet.</p>
      <ul v-else class="list">
        <li v-for="a in rows" :key="a.id">
          <div class="who">
            <RouterLink v-if="a.person" :to="{ name: 'person', params: { id: a.personId } }" class="ellipsis">{{
              a.person.displayName
            }}</RouterLink>
            <span v-else class="muted">Unknown person</span>
            <Tag v-if="a.person && a.person.isActive === false" value="Inactive" severity="secondary" />
            <i
              v-if="a._local?.pending"
              v-tooltip.top="'Not synced yet'"
              role="img"
              class="pi pi-cloud-upload muted"
              aria-label="Not synced yet"
            />
          </div>
          <form v-if="editing === a.id" class="role-edit" @submit.prevent="saveRole(a)">
            <RoleInput v-model="editedRole" :input-id="`role-${a.id}`" />
            <Button
              type="submit"
              icon="pi pi-check"
              size="small"
              :aria-label="`Save role of ${a.person?.displayName}`"
            />
            <Button
              icon="pi pi-times"
              size="small"
              severity="secondary"
              text
              aria-label="Discard"
              @click="editing = null"
            />
          </form>
          <template v-else>
            <span class="role muted ellipsis">{{ a.role ?? '' }}</span>
            <span v-if="canEdit" class="row-actions">
              <Button
                icon="pi pi-pencil"
                size="small"
                text
                severity="secondary"
                :aria-label="`Edit role of ${a.person?.displayName ?? 'person'}`"
                @click="editRole(a)"
              />
              <Button
                icon="pi pi-times"
                size="small"
                text
                severity="secondary"
                :aria-label="`Remove ${a.person?.displayName ?? 'person'}`"
                @click="unassign(a)"
              />
            </span>
          </template>
        </li>
      </ul>
    </div>
  </section>
</template>

<style scoped>
.with-action {
  display: flex;
  align-items: center;
  justify-content: space-between;
}

.add {
  display: grid;
  grid-template-columns: 1fr 1fr auto;
  gap: var(--pgpm-space-2);
  align-items: center;
  margin-bottom: var(--pgpm-space-3);
}

.actions {
  display: flex;
  gap: var(--pgpm-space-1);
}

.list {
  list-style: none;
  margin: 0;
  padding: 0;
}

.list li {
  display: grid;
  grid-template-columns: minmax(0, 1fr) minmax(0, 1fr) auto;
  align-items: center;
  gap: var(--pgpm-space-2);
  min-height: 32px;
  border-bottom: 1px solid var(--pgpm-border);
}

.list li:last-child {
  border-bottom: none;
}

.who {
  display: flex;
  align-items: center;
  gap: var(--pgpm-space-2);
  min-width: 0;
}

.role-edit {
  grid-column: 2 / 4;
  display: flex;
  gap: var(--pgpm-space-1);
  align-items: center;
}

.row-actions {
  display: flex;
}

.empty {
  margin: 0;
}

@media (max-width: 599px) {
  .add {
    grid-template-columns: 1fr;
  }
}
</style>

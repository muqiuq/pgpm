<script setup lang="ts">
import Button from 'primevue/button'
import ProgressSpinner from 'primevue/progressspinner'
import Tag from 'primevue/tag'
import type { MenuItem } from 'primevue/menuitem'
import { computed, ref } from 'vue'
import { useRouter } from 'vue-router'
import EmptyState from '@/components/EmptyState.vue'
import MoreMenu from '@/components/MoreMenu.vue'
import PageHeader from '@/components/PageHeader.vue'
import RelativeTime from '@/components/RelativeTime.vue'
import { useNotify } from '@/composables/useNotify'
import { itemRoute, itemTypes } from '@/domain/itemTypes'
import { localDb } from '@/local/db'
import { repo } from '@/local/repo'
import { itemKey, type AssignmentRow, type ItemRow, type PersonRow, type Stored } from '@/local/rows'
import { useLive } from '@/local/useLive'
import { useSession } from '@/stores/session'
import PersonDialog from './PersonDialog.vue'

/**
 * A person's page (docs/01 §16): the directory fields, the user account linked to the person, and every assignment of
 * the person – the item (or milestone) and the role; an item not on this device is *restricted*. *Edit* and
 * *Deactivate* with Persons: Edit; a Microsoft person's directory fields are the directory sync's (only the notes are
 * edited here), and the sync deactivates it.
 */
const props = defineProps<{ id: string }>()
const router = useRouter()
const session = useSession()
const notify = useNotify()

const person = useLive(
  async () => (await localDb().synced('persons').get(props.id)) as Stored<PersonRow> | undefined,
  undefined as Stored<PersonRow> | undefined,
  () => props.id,
)

interface AssignmentLine {
  id: string
  role: string | null
  item: ItemRow | null
  milestone: string | null
}

const assignments = useLive(
  async (): Promise<AssignmentLine[]> => {
    const db = localDb()
    const rows = (
      (await db.synced('assignments').where('personId').equals(props.id).toArray()) as unknown as AssignmentRow[]
    ).filter((a) => !a.deleted)
    const lines: AssignmentLine[] = []
    for (const a of rows) {
      let itemId = a.itemId
      let milestone: string | null = null
      if (a.milestoneId) {
        const m = (await db.synced('milestones').get(a.milestoneId)) as { itemId?: string; title?: string } | undefined
        milestone = m?.title ?? 'A milestone'
        itemId = m?.itemId ?? null
      }
      const item = itemId ? ((await db.synced('items').get(itemId)) as unknown as ItemRow | undefined) : undefined
      lines.push({ id: a.id, role: a.role, item: item && !item.deleted ? item : null, milestone })
    }
    return lines.sort(
      (a, b) =>
        Number(!a.item) - Number(!b.item) ||
        (a.item?.type ?? '').localeCompare(b.item?.type ?? '') ||
        (a.item?.title ?? '').localeCompare(b.item?.title ?? ''),
    )
  },
  [] as AssignmentLine[],
  () => props.id,
)

const p = computed(() => (person.value.value && !person.value.value.deleted ? person.value.value : undefined))
const canEdit = computed(() => session.allows(['Persons', 'Edit']))
const manual = computed(() => p.value?.source === 'manual')
const editing = ref(false)

const facts = computed(() => {
  const x = p.value
  if (!x) return []
  return [
    { label: 'Given name', value: x.givenName },
    { label: 'Surname', value: x.surname },
    { label: 'Job title', value: x.jobTitle },
    { label: 'Department', value: x.department },
    { label: 'Company', value: x.companyName },
    { label: 'Phone', value: x.phone, href: x.phone ? `tel:${x.phone}` : undefined },
    { label: 'Mobile phone', value: x.mobilePhone, href: x.mobilePhone ? `tel:${x.mobilePhone}` : undefined },
    { label: 'User principal name', value: x.userPrincipalName },
  ].filter((f) => !!f.value)
})

async function setActive(isActive: boolean) {
  try {
    await repo.patch('persons', props.id, { isActive })
    notify.success(isActive ? 'Person activated' : 'Person deactivated')
  } catch (e) {
    notify.error(e, 'Not changed')
  }
}

const more = computed<MenuItem[]>(() =>
  canEdit.value && manual.value && p.value
    ? [
        p.value.isActive === false
          ? { label: 'Activate', icon: 'pi pi-check-circle', command: () => setActive(true) }
          : { label: 'Deactivate', icon: 'pi pi-ban', command: () => setActive(false) },
      ]
    : [],
)
</script>

<template>
  <div class="page">
    <div v-if="!person.loaded.value" class="center"><ProgressSpinner style="width: 32px; height: 32px" /></div>
    <template v-else-if="!p">
      <PageHeader title="Person" :crumbs="[{ label: 'Persons', to: { name: 'persons' } }]" />
      <EmptyState icon="pi pi-eye-slash" title="This person is not on this device." text="It may have been removed.">
        <Button
          label="All persons"
          size="small"
          severity="secondary"
          outlined
          @click="router.push({ name: 'persons' })"
        />
      </EmptyState>
    </template>
    <template v-else>
      <PageHeader :title="p.displayName" :crumbs="[{ label: 'Persons', to: { name: 'persons' } }]">
        <template #status>
          <Tag
            :value="p.source === 'microsoft' ? 'Microsoft' : 'Manual'"
            :icon="p.source === 'microsoft' ? 'pi pi-microsoft' : 'pi pi-user-edit'"
            severity="secondary"
          />
          <Tag v-if="p.userType === 'Guest'" value="Guest" severity="secondary" />
          <Tag v-if="p.isActive === false" value="Inactive" severity="warn" />
          <Tag v-if="p._local?.pending" icon="pi pi-cloud-upload" value="Not synced yet" severity="secondary" />
        </template>
        <template #actions>
          <Button
            v-if="canEdit"
            :label="manual ? 'Edit' : 'Edit notes'"
            icon="pi pi-pencil"
            size="small"
            severity="secondary"
            outlined
            @click="editing = true"
          />
          <MoreMenu v-if="more.length" :items="more" label="More person actions" />
        </template>
      </PageHeader>
      <div class="two-columns">
        <section class="panel" aria-labelledby="person-details">
          <div id="person-details" class="panel-header">Details</div>
          <div class="panel-body">
            <dl class="facts">
              <dt>E-mail</dt>
              <dd>
                <a v-if="p.email" :href="`mailto:${p.email}`">{{ p.email }}</a>
                <span v-else class="muted">–</span>
              </dd>
              <template v-for="f in facts" :key="f.label">
                <dt>{{ f.label }}</dt>
                <dd>
                  <a v-if="f.href" :href="f.href">{{ f.value }}</a>
                  <template v-else>{{ f.value }}</template>
                </dd>
              </template>
              <dt>User account</dt>
              <dd>
                <template v-if="p.userId">
                  Linked to a user account<template v-if="p.userId === session.user?.id"> (you)</template>
                </template>
                <span v-else class="muted">None</span>
              </dd>
              <template v-if="p.source === 'microsoft'">
                <dt>Directory sync</dt>
                <dd>
                  <RelativeTime v-if="p.directorySyncedAt" :value="p.directorySyncedAt" />
                  <span v-else class="muted">–</span>
                </dd>
              </template>
              <dt>Notes</dt>
              <dd class="notes">
                <template v-if="p.notes">{{ p.notes }}</template>
                <span v-else class="muted">–</span>
              </dd>
            </dl>
          </div>
        </section>
        <section class="panel" aria-labelledby="person-assignments">
          <div id="person-assignments" class="panel-header">All assignments ({{ assignments.value.value.length }})</div>
          <EmptyState
            v-if="!assignments.value.value.length"
            icon="pi pi-users"
            title="Not assigned to anything on this device."
          />
          <ul v-else class="assignments">
            <li v-for="a in assignments.value.value" :key="a.id">
              <template v-if="a.item">
                <i :class="itemTypes[a.item.type].icon" aria-hidden="true" />
                <span class="mono muted key">{{ itemKey(a.item) }}</span>
                <RouterLink :to="itemRoute(a.item)" class="ellipsis">{{ a.item.title }}</RouterLink>
                <span v-if="a.milestone" class="muted ellipsis">· {{ a.milestone }}</span>
              </template>
              <template v-else>
                <i class="pi pi-lock muted" aria-hidden="true" />
                <span class="muted">Restricted</span>
              </template>
              <Tag v-if="a.role" :value="a.role" severity="secondary" class="role" />
            </li>
          </ul>
        </section>
      </div>
      <PersonDialog v-model:visible="editing" :person="p" />
    </template>
  </div>
</template>

<style scoped>
.center {
  display: flex;
  justify-content: center;
  padding: var(--pgpm-space-6);
}

.notes {
  white-space: pre-wrap;
}

.assignments {
  list-style: none;
  margin: 0;
  padding: 0;
}

.assignments li {
  display: flex;
  align-items: center;
  gap: var(--pgpm-space-2);
  min-height: 36px;
  padding: 0 var(--pgpm-space-3);
  border-bottom: 1px solid var(--pgpm-border);
  min-width: 0;
}

.assignments li:last-child {
  border-bottom: none;
}

.key {
  flex: none;
  font-size: 0.857rem;
}

.role {
  margin-left: auto;
  flex: none;
}
</style>

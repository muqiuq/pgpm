<script setup lang="ts">
import Button from 'primevue/button'
import Column from 'primevue/column'
import Select from 'primevue/select'
import Tag from 'primevue/tag'
import ToggleButton from 'primevue/togglebutton'
import { computed, ref } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import FilterBar from '@/components/FilterBar.vue'
import ListTable from '@/components/ListTable.vue'
import FirstSyncNotice from '@/components/sync/FirstSyncNotice.vue'
import PageHeader from '@/components/PageHeader.vue'
import { useLocalTable } from '@/composables/useLocalTable'
import { localDb } from '@/local/db'
import type { PersonRow, Stored } from '@/local/rows'
import { useLive } from '@/local/useLive'
import { useSession } from '@/stores/session'
import { useSyncStore } from '@/stores/sync'
import PersonDialog from './PersonDialog.vue'

/**
 * *Directory → Persons* (docs/01 §16): everybody of the directory on this device – Microsoft Entra persons (members and
 * guests) and manual ones – searched by name, e-mail, job title, department and company, filtered by source, member /
 * guest and department, active ones unless asked otherwise. Offline, in memory; the list state is remembered per user
 * (`user_settings` `list:person`). *New person* (Persons: Edit) creates a manual person, offline too.
 */
type Person = Stored<PersonRow>
type Source = 'microsoft' | 'manual'
type Kind = 'Member' | 'Guest'

const route = useRoute()
const router = useRouter()
const session = useSession()
const sync = useSyncStore()

const text = (v: unknown) => (typeof v === 'string' && v ? v : null)
const source = ref<Source | null>((text(route.query.source) as Source | null) ?? null)
const kind = ref<Kind | null>((text(route.query.kind) as Kind | null) ?? null)
const department = ref<string | null>(text(route.query.department))
const inactive = ref(route.query.inactive === '1')

const filters = computed(() => ({
  source: source.value ?? undefined,
  kind: kind.value ?? undefined,
  department: department.value ?? undefined,
  inactive: inactive.value || undefined,
}))
const urlQuery = computed(() => ({
  source: source.value ?? undefined,
  kind: kind.value ?? undefined,
  department: department.value ?? undefined,
  inactive: inactive.value ? '1' : undefined,
}))

const all = () => localDb().synced('persons').toArray() as unknown as Promise<Person[]>

const table = useLocalTable<Person>({
  rows: all,
  text: (p) => [p.displayName, p.email, p.jobTitle, p.department, p.companyName].filter(Boolean).join(' '),
  filters,
  match: (p, f) =>
    !p.deleted &&
    (!!f.inactive || p.isActive !== false) &&
    (!f.source || p.source === f.source) &&
    (!f.kind || (f.kind === 'Guest' ? p.userType === 'Guest' : p.userType !== 'Guest')) &&
    (!f.department || p.department === f.department),
  defaultSort: 'displayName',
  syncUrl: true,
  urlQuery,
  settingsKey: 'list:person',
  remember: () => ({
    source: source.value,
    kind: kind.value,
    department: department.value,
    inactive: inactive.value,
  }),
  restore: (f) => {
    source.value = f.source === 'microsoft' || f.source === 'manual' ? f.source : null
    kind.value = f.kind === 'Member' || f.kind === 'Guest' ? f.kind : null
    department.value = typeof f.department === 'string' ? f.department : null
    inactive.value = f.inactive === true
  },
})

const departments = useLive(
  async () =>
    [...new Set((await all()).filter((p) => !p.deleted && p.department).map((p) => p.department!))].sort((a, b) =>
      a.localeCompare(b),
    ),
  [] as string[],
)

const sources = [
  { label: 'Microsoft', value: 'microsoft' },
  { label: 'Manual', value: 'manual' },
]
const kinds = [
  { label: 'Members', value: 'Member' },
  { label: 'Guests', value: 'Guest' },
]
const active = computed(
  () => Number(!!source.value) + Number(!!kind.value) + Number(!!department.value) + Number(inactive.value),
)
const canEdit = computed(() => session.allows(['Persons', 'Edit']))
const creating = ref(false)

const open = (p: Person) => router.push({ name: 'person', params: { id: p.id } })
const created = (id: string) => router.push({ name: 'person', params: { id } })
</script>

<template>
  <div class="page">
    <PageHeader title="Persons">
      <template #actions>
        <Button v-if="canEdit" label="New person" icon="pi pi-plus" size="small" @click="creating = true" />
      </template>
    </PageHeader>
    <FilterBar v-model:search="table.search.value" placeholder="Search persons…" :active="active">
      <Select
        v-model="source"
        :options="sources"
        option-label="label"
        option-value="value"
        placeholder="Any source"
        show-clear
        size="small"
        aria-label="Source"
      />
      <Select
        v-model="kind"
        :options="kinds"
        option-label="label"
        option-value="value"
        placeholder="Members and guests"
        show-clear
        size="small"
        aria-label="Members or guests"
      />
      <Select
        v-model="department"
        :options="departments.value.value"
        placeholder="Any department"
        show-clear
        filter
        size="small"
        aria-label="Department"
      />
      <ToggleButton
        v-model="inactive"
        on-label="With inactive"
        off-label="With inactive"
        on-icon="pi pi-eye"
        off-icon="pi pi-eye-slash"
        size="small"
      />
    </FilterBar>
    <ListTable :table="table" what="persons" selectable fixed row-links @select="open">
      <template v-if="sync.firstSync" #empty><FirstSyncNotice /></template>
      <Column field="displayName" header="Name" sortable>
        <template #body="{ data }">
          <div class="name-cell">
            <RouterLink :to="{ name: 'person', params: { id: data.id } }" class="strong ellipsis" @click.stop>{{
              data.displayName
            }}</RouterLink>
            <Tag v-if="data.userType === 'Guest'" value="Guest" severity="secondary" />
            <Tag v-if="data.isActive === false" value="Inactive" severity="warn" />
          </div>
        </template>
      </Column>
      <Column field="email" header="E-mail" sortable>
        <template #body="{ data }"
          ><span class="ellipsis">{{ data.email ?? '' }}</span></template
        >
      </Column>
      <Column field="jobTitle" header="Job title" sortable>
        <template #body="{ data }"
          ><span class="ellipsis">{{ data.jobTitle ?? '' }}</span></template
        >
      </Column>
      <Column field="department" header="Department" sortable>
        <template #body="{ data }"
          ><span class="ellipsis">{{ data.department ?? '' }}</span></template
        >
      </Column>
      <Column field="source" header="Source" sortable style="width: 8rem">
        <template #body="{ data }">
          <Tag
            :value="data.source === 'microsoft' ? 'Microsoft' : 'Manual'"
            :icon="data.source === 'microsoft' ? 'pi pi-microsoft' : 'pi pi-user-edit'"
            severity="secondary"
          />
        </template>
      </Column>
      <template #mobile="{ item }">
        <RouterLink :to="{ name: 'person', params: { id: item.id } }" class="mobile-row">
          <span class="strong ellipsis">{{ item.displayName }}</span>
          <span class="muted small ellipsis">
            {{ [item.email, item.jobTitle].filter(Boolean).join(' · ') }}
            <template v-if="item.userType === 'Guest'"> · Guest</template>
            <template v-if="item.isActive === false"> · Inactive</template>
          </span>
        </RouterLink>
      </template>
    </ListTable>
    <PersonDialog v-model:visible="creating" :person="null" @saved="created" />
  </div>
</template>

<style scoped>
.name-cell {
  display: flex;
  align-items: center;
  gap: var(--pgpm-space-2);
  min-width: 0;
}

.small {
  font-size: 0.857rem;
}

.mobile-row {
  display: flex;
  flex-direction: column;
  color: inherit;
  text-decoration: none;
}
</style>

<script setup lang="ts">
import Button from 'primevue/button'
import Toast from 'primevue/toast'
import ConfirmDialog from 'primevue/confirmdialog'
import { useToast } from 'primevue/usetoast'
import { onBeforeUnmount, watch } from 'vue'
import { useRoute } from 'vue-router'
import DailyTotpDialog from '@/components/auth/DailyTotpDialog.vue'
import { provideMarkdownServices } from '@/components/markdown/services'
import AppLayout from '@/layouts/AppLayout.vue'
import AuthLayout from '@/layouts/AuthLayout.vue'
import { onSyncNotice } from '@/local/boot'
import { databaseBlocked } from '@/local/db'
import { appDrafts } from '@/local/drafts'
import { applyUpdate, waitingWorker } from '@/pwa/register'
import { usePreferences } from '@/stores/preferences'

// Applies the theme before the first page renders.
usePreferences()
const route = useRoute()
const toast = useToast()

// The editor's drafts in IndexedDB with the localStorage mirror (docs/03 §18.5); files come with phase 3.
provideMarkdownServices({ drafts: appDrafts })

// The sync's notices: conflicts once per run, refused changes, removed access (docs/03 §11.7, §11.8).
const off = onSyncNotice((notice) =>
  toast.add({
    severity: notice.severity,
    summary: notice.summary,
    detail: notice.detail,
    life: notice.severity === 'error' ? 8000 : 5000,
  }),
)
onBeforeUnmount(off)

// Another tab holds the database open with an older version (docs/03 §11.3).
watch(databaseBlocked, (blocked) => {
  toast.removeGroup('blocked')
  if (blocked) {
    toast.add({
      group: 'blocked',
      severity: 'warn',
      summary: 'PGPM is open in another tab with an older version',
      detail: 'Close the other tab so that this one can update its local data.',
    })
  }
})

// A new version waits (docs/03 §11.1): one sticky toast until Reload.
watch(waitingWorker, (worker) => {
  toast.removeGroup('update')
  if (worker) {
    toast.add({ group: 'update', severity: 'info', summary: 'New version available' })
  }
})
</script>

<template>
  <template v-if="route.matched.length">
    <AuthLayout v-if="route.meta.layout === 'auth'"><RouterView /></AuthLayout>
    <AppLayout v-else><RouterView /></AppLayout>
  </template>
  <Toast position="bottom-right" />
  <Toast position="bottom-right" group="update">
    <template #message="{ message }">
      <div class="update-toast">
        <i class="pi pi-download" aria-hidden="true" />
        <span class="text">{{ message.summary }}</span>
        <Button label="Reload" size="small" @click="applyUpdate()" />
      </div>
    </template>
  </Toast>
  <Toast position="top-center" group="blocked" />
  <ConfirmDialog />
  <DailyTotpDialog />
</template>

<style scoped>
.update-toast {
  display: flex;
  align-items: center;
  gap: var(--pgpm-space-2);
  width: 100%;
}

.update-toast .text {
  flex: 1;
}
</style>

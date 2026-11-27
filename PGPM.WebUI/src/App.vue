<script setup lang="ts">
import Button from 'primevue/button'
import Toast from 'primevue/toast'
import ConfirmDialog from 'primevue/confirmdialog'
import { useToast } from 'primevue/usetoast'
import { watch } from 'vue'
import { useRoute } from 'vue-router'
import AppLayout from '@/layouts/AppLayout.vue'
import AuthLayout from '@/layouts/AuthLayout.vue'
import { applyUpdate, waitingWorker } from '@/pwa/register'
import { usePreferences } from '@/stores/preferences'

// Applies the theme before the first page renders.
usePreferences()
const route = useRoute()
const toast = useToast()

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
  <ConfirmDialog />
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

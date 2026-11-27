<script setup lang="ts">
import Button from 'primevue/button'
import InputText from 'primevue/inputtext'
import { ref, useId } from 'vue'
import { adminApi, type UserItem } from '@/api/admin'
import { ApiError } from '@/api/client'
import { useActions } from '@/composables/useActions'
import { formatAbsolute } from '@/format'

/**
 * A user's Microsoft account (GGITSV's UserMicrosoftAccount, docs/03 §5.3): matched by tenant and object id only, never
 * by name. An admin links an existing local user – after an account-conflict refusal, say – or unlinks it.
 */
const props = defineProps<{ user: UserItem }>()
const emit = defineEmits<{ updated: [user: UserItem] }>()
const { ask, notify } = useActions()

const UUID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i
const ids = { objectId: useId(), tenantId: useId() }
const objectId = ref('')
const tenantId = ref('')
const errors = ref<{ objectId?: string; tenantId?: string }>({})
const busy = ref(false)

async function link() {
  errors.value = {
    objectId: UUID.test(objectId.value.trim()) ? undefined : 'An object id: 8-4-4-4-12 hexadecimal digits.',
    tenantId: !tenantId.value.trim() || UUID.test(tenantId.value.trim()) ? undefined : 'A tenant id, or empty.',
  }
  if (errors.value.objectId || errors.value.tenantId) return
  busy.value = true
  try {
    const updated = await adminApi.users.linkMicrosoft(
      props.user.id,
      objectId.value.trim(),
      tenantId.value.trim() || null,
    )
    objectId.value = tenantId.value = ''
    notify.success('Microsoft account linked', `${props.user.username} signs in with it from now on.`)
    emit('updated', updated)
  } catch (e) {
    if (e instanceof ApiError && (Object.keys(e.errors).length || e.code === 'duplicate_key')) {
      errors.value = {
        objectId: e.field('objectId') ?? (e.code === 'duplicate_key' ? e.message : undefined),
        tenantId: e.field('tenantId'),
      }
    } else {
      notify.error(e, 'Not linked')
    }
  } finally {
    busy.value = false
  }
}

async function unlink() {
  const passwordless = !props.user.hasPassword
  if (
    !(await ask(
      `Unlink the Microsoft account of ${props.user.username}?`,
      `Their sessions end.${passwordless ? ' They have no password: until one is reset they cannot sign in.' : ''}`,
      'Unlink',
    ))
  )
    return
  busy.value = true
  try {
    emit('updated', await adminApi.users.unlinkMicrosoft(props.user.id))
    notify.success('Microsoft account unlinked')
  } catch (e) {
    notify.error(e, 'Not unlinked')
  } finally {
    busy.value = false
  }
}
</script>

<template>
  <div class="microsoft">
    <template v-if="user.microsoftAccount">
      <dl class="facts">
        <dt>Object id</dt>
        <dd class="mono">{{ user.microsoftAccount.objectId }}</dd>
        <dt>Tenant</dt>
        <dd class="mono">{{ user.microsoftAccount.tenantId }}</dd>
        <dt>User name</dt>
        <dd>{{ user.microsoftAccount.userName ?? '–' }}</dd>
        <dt>Directory read</dt>
        <dd>{{ formatAbsolute(user.microsoftAccount.directoryReadAt) }}</dd>
      </dl>
      <div>
        <Button
          label="Unlink…"
          icon="pi pi-times"
          size="small"
          severity="danger"
          text
          :loading="busy"
          @click="unlink"
        />
      </div>
    </template>
    <form v-else class="form" novalidate @submit.prevent="link">
      <p class="muted text">Not linked. Link an account so that {{ user.username }} signs in with Microsoft.</p>
      <div class="field">
        <label :for="ids.objectId">Object id</label>
        <InputText :id="ids.objectId" v-model="objectId" class="mono" :invalid="!!errors.objectId" autocomplete="off" />
        <small v-if="errors.objectId" class="field-error">{{ errors.objectId }}</small>
        <small v-else class="field-hint">In the Entra admin center under Users, “Object ID”.</small>
      </div>
      <div class="field">
        <label :for="ids.tenantId">Tenant id</label>
        <InputText :id="ids.tenantId" v-model="tenantId" class="mono" :invalid="!!errors.tenantId" autocomplete="off" />
        <small v-if="errors.tenantId" class="field-error">{{ errors.tenantId }}</small>
        <small v-else class="field-hint">Leave empty for the configured tenant.</small>
      </div>
      <div class="form-actions"><Button type="submit" label="Link" size="small" :loading="busy" /></div>
    </form>
  </div>
</template>

<style scoped>
.text {
  margin: 0;
}
</style>

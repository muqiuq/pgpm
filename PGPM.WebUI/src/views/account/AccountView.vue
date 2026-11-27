<script setup lang="ts">
import Tag from 'primevue/tag'
import PageHeader from '@/components/PageHeader.vue'
import { useSession } from '@/stores/session'
import PasswordForm from './PasswordForm.vue'

/**
 * The own account (PNMTD): who you are, how you sign in, your password. The person the directory knows you as comes
 * with the Persons page (phase 3).
 */
const session = useSession()
</script>

<template>
  <div class="page">
    <PageHeader title="Account" />
    <div class="two-columns">
      <section class="panel" aria-labelledby="profile-title">
        <div id="profile-title" class="panel-header">Profile</div>
        <dl class="panel-body facts">
          <dt>Username</dt>
          <dd class="mono">{{ session.user?.username }}</dd>
          <dt>Name</dt>
          <dd>{{ session.user?.displayName }}</dd>
          <dt>E-mail</dt>
          <dd>{{ session.user?.email ?? '–' }}</dd>
          <dt>Access</dt>
          <dd>
            <Tag v-if="session.isAdmin" value="Admin" severity="info" />
            <RouterLink v-else :to="{ name: 'security', hash: '#access-title' }">Your groups and rights ›</RouterLink>
          </dd>
          <dt>Signed in with</dt>
          <dd>
            {{ session.times?.authMethod === 'Microsoft' ? 'Microsoft' : 'Password' }}
            <span v-if="session.user?.hasMicrosoftAccount && session.times?.authMethod !== 'Microsoft'" class="muted">
              · Microsoft account linked</span
            >
          </dd>
          <dt>Two-factor</dt>
          <dd>
            <span v-if="session.user?.isTotpEnabled" class="on"><i class="pi pi-shield" /> On</span>
            <span v-else-if="!session.user?.hasPassword" class="muted">Microsoft's</span>
            <span v-else class="muted">Off</span>
            · <RouterLink :to="{ name: 'security' }">Security</RouterLink>
          </dd>
        </dl>
      </section>
      <section class="panel" aria-labelledby="password-title">
        <div id="password-title" class="panel-header">Change password</div>
        <div class="panel-body narrow">
          <PasswordForm v-if="session.user?.hasPassword" />
          <p v-else class="text muted">You sign in with Microsoft: there is no PGPM password to change.</p>
        </div>
      </section>
    </div>
  </div>
</template>

<style scoped>
.on {
  color: var(--pgpm-ok);
}

.narrow {
  max-width: 420px;
}

.text {
  margin: 0;
}
</style>

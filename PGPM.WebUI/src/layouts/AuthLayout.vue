<script setup lang="ts">
import LogoMark from '@/components/LogoMark.vue'
import { useSession } from '@/stores/session'

/** The sign-in screens (PNMTD, with GGITSV's Microsoft button): a centred card with the logo. */
const session = useSession()
</script>

<template>
  <div class="auth-layout">
    <main class="auth-card panel">
      <header class="brand">
        <LogoMark :size="32" />
        <div class="wordmark">PGPM</div>
      </header>
      <slot />
    </main>
    <footer v-if="session.info?.version" class="muted">PGPM {{ session.info.version }}</footer>
  </div>
</template>

<style scoped>
.auth-layout {
  min-height: 100vh;
  display: flex;
  flex-direction: column;
  align-items: center;
  justify-content: center;
  gap: var(--pgpm-space-4);
  padding: var(--pgpm-space-4);
}

.auth-card {
  width: 100%;
  max-width: 400px;
  padding: var(--pgpm-space-6);
  display: flex;
  flex-direction: column;
  gap: var(--pgpm-space-4);
}

.brand {
  display: flex;
  align-items: center;
  gap: var(--pgpm-space-3);
  color: var(--p-primary-color);
}

.wordmark {
  font-size: 1.5rem;
  font-weight: 500;
  letter-spacing: 0.06em;
  line-height: 1.1;
}

footer {
  font-size: 0.857rem;
}

@media (max-width: 599px) {
  .auth-card {
    padding: var(--pgpm-space-4);
    border: none;
    background: transparent;
  }
}
</style>

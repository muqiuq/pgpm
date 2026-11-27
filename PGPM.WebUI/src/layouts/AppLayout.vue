<script setup lang="ts">
import Button from 'primevue/button'
import Menu from 'primevue/menu'
import type { MenuItem } from 'primevue/menuitem'
import { computed, ref, watch } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import GlobalSearch from '@/components/GlobalSearch.vue'
import LogoMark from '@/components/LogoMark.vue'
import SyncIndicator from '@/components/SyncIndicator.vue'
import { useMedia } from '@/composables/useMedia'
import { useNotify } from '@/composables/useNotify'
import { installStatus, manualInstallSteps, promptInstall } from '@/pwa/install'
import { usePreferences } from '@/stores/preferences'
import { useSession } from '@/stores/session'
import { type NavGroup, visibleNavigation } from './navigation'

/**
 * The shell (PNMTD, docs/03 §12.2): a drawer with the menu's groups, an app bar of 48 px, the page. On a desktop the
 * drawer folds to an icon rail; below 960 px it is a rail; below 600 px an overlay that starts closed. The app bar: the
 * global search, the sync indicator, the theme and the user menu.
 */
const session = useSession()
const preferences = usePreferences()
const route = useRoute()
const router = useRouter()
const notify = useNotify()

const phone = useMedia('(max-width: 599px)')
const narrow = useMedia('(max-width: 959px)')
const overlayOpen = ref(false)

const groups = computed(() => visibleNavigation(session.allows))

// A page can hold the drawer as a rail while it is open (the map canvas); opening it then is for that page only.
const openedWhileHeld = ref(false)
const rail = computed(
  () => !phone.value && (narrow.value || (preferences.railHeld ? !openedWhileHeld.value : preferences.drawerCollapsed)),
)
const drawerVisible = computed(() => !phone.value || overlayOpen.value)

watch(
  () => route.fullPath,
  () => (overlayOpen.value = false),
)
watch(
  () => preferences.railHeld,
  () => (openedWhileHeld.value = false),
)

function toggleDrawer() {
  if (phone.value) {
    overlayOpen.value = !overlayOpen.value
  } else if (narrow.value) {
    return
  } else if (preferences.railHeld) {
    openedWhileHeld.value = !openedWhileHeld.value
  } else {
    preferences.drawerCollapsed = !preferences.drawerCollapsed
  }
}

function isActive(name: string, to: string) {
  return route.name === name || route.path === to || route.path.startsWith(`${to}/`)
}

// Folded groups (Administration starts folded); a folded group still shows the page one is on. The rail shows every
// entry: it has no labels to unfold.
const folded = (group: NavGroup) => !rail.value && preferences.isFolded(group.label, !!group.folded)
const shownItems = (group: NavGroup) =>
  folded(group) ? group.items.filter((i) => isActive(i.name, i.to)) : group.items

async function install() {
  if (installStatus.value === 'ready') {
    await promptInstall()
  } else {
    notify.info('Install PGPM', manualInstallSteps())
  }
}

// Phase 1 fills in the signed-in user, the security page (own MCP tokens) and Sign out.
const userMenu = ref<InstanceType<typeof Menu> | null>(null)
const userItems = computed<MenuItem[]>(() => [
  {
    label: 'Not signed in (phase 1)',
    items: [
      { label: 'Account', icon: 'pi pi-user', command: () => router.push({ name: 'account' }) },
      ...(installStatus.value === 'standalone' || installStatus.value === 'installed'
        ? []
        : [{ label: 'Install app', icon: 'pi pi-download', command: install }]),
      { separator: true },
      { label: 'Sign out', icon: 'pi pi-sign-out', disabled: true },
    ],
  },
])
</script>

<template>
  <div class="app-layout" :class="{ rail, phone }">
    <a class="skip-link" href="#main">Skip to content</a>
    <div v-if="phone && overlayOpen" class="backdrop" @click="overlayOpen = false" />
    <aside v-show="drawerVisible" class="drawer" :class="{ overlay: phone }" aria-label="Navigation">
      <RouterLink to="/" class="drawer-brand" aria-label="PGPM home">
        <LogoMark :size="24" />
        <span v-if="!rail" class="wordmark">PGPM</span>
      </RouterLink>
      <nav class="drawer-nav">
        <section v-for="group in groups" :key="group.label" class="group">
          <button
            v-if="!rail"
            type="button"
            class="group-label"
            :aria-expanded="!folded(group)"
            :aria-controls="`nav-${group.label}`"
            @click="preferences.toggleFold(group.label, !!group.folded)"
          >
            <span>{{ group.label }}</span>
            <i :class="folded(group) ? 'pi pi-chevron-right' : 'pi pi-chevron-down'" aria-hidden="true" />
          </button>
          <div v-else class="group-divider" />
          <div :id="`nav-${group.label}`">
            <RouterLink
              v-for="item in shownItems(group)"
              :key="item.name"
              v-tooltip.right="rail ? item.label : undefined"
              :to="item.to"
              class="nav-item"
              :class="{ active: isActive(item.name, item.to) }"
              :aria-current="isActive(item.name, item.to) ? 'page' : undefined"
            >
              <i :class="item.icon" aria-hidden="true" />
              <span v-if="!rail" class="label">{{ item.label }}</span>
              <span v-else class="sr-only">{{ item.label }}</span>
            </RouterLink>
          </div>
        </section>
      </nav>
      <footer v-if="!rail" class="drawer-footer">
        <div class="muted">PGPM {{ session.info?.version ?? '' }}</div>
      </footer>
    </aside>

    <div class="main-column">
      <header class="app-bar">
        <Button
          icon="pi pi-bars"
          text
          rounded
          severity="secondary"
          :aria-label="phone ? 'Open navigation' : 'Fold navigation'"
          :aria-expanded="phone ? overlayOpen : !rail"
          @click="toggleDrawer"
        />
        <div class="app-bar-center"><GlobalSearch v-if="!phone" /></div>
        <div class="app-bar-end">
          <SyncIndicator />
          <Button
            v-tooltip.bottom="preferences.isDark() ? 'Light theme' : 'Dark theme'"
            :icon="preferences.isDark() ? 'pi pi-sun' : 'pi pi-moon'"
            text
            rounded
            severity="secondary"
            :aria-label="preferences.isDark() ? 'Light theme' : 'Dark theme'"
            @click="preferences.toggleTheme()"
          />
          <Button
            text
            severity="secondary"
            class="user-button"
            aria-haspopup="true"
            aria-controls="user-menu"
            aria-label="User menu"
            @click="(e: Event) => userMenu?.toggle(e)"
          >
            <span class="avatar" aria-hidden="true"><i class="pi pi-user" /></span>
            <i class="pi pi-angle-down" aria-hidden="true" />
          </Button>
          <Menu id="user-menu" ref="userMenu" :model="userItems" popup />
        </div>
      </header>
      <main id="main" class="content" tabindex="-1">
        <slot />
      </main>
    </div>
  </div>
</template>

<style scoped>
.app-layout {
  display: flex;
  min-height: 100vh;
}

.skip-link {
  position: absolute;
  left: -999px;
  top: 0;
  z-index: 100;
  background: var(--pgpm-panel);
  padding: var(--pgpm-space-2);
}

.skip-link:focus {
  left: var(--pgpm-space-2);
}

.drawer {
  position: sticky;
  top: 0;
  height: 100vh;
  width: var(--pgpm-drawer-width);
  flex: 0 0 var(--pgpm-drawer-width);
  display: flex;
  flex-direction: column;
  background: var(--pgpm-panel);
  border-right: 1px solid var(--pgpm-border);
  z-index: 20;
}

.rail .drawer {
  width: var(--pgpm-rail-width);
  flex-basis: var(--pgpm-rail-width);
}

.drawer.overlay {
  position: fixed;
  left: 0;
  top: 0;
  box-shadow: var(--p-overlay-modal-shadow);
}

.backdrop {
  position: fixed;
  inset: 0;
  background: var(--p-mask-background);
  z-index: 19;
}

.drawer-brand {
  display: flex;
  align-items: center;
  gap: var(--pgpm-space-2);
  height: var(--pgpm-header-height);
  padding: 0 var(--pgpm-space-4);
  color: var(--p-primary-color);
  flex: 0 0 auto;
}

.rail .drawer-brand {
  padding: 0;
  justify-content: center;
}

.drawer-brand:hover {
  text-decoration: none;
}

.wordmark {
  font-size: 1.2rem;
  font-weight: 500;
  letter-spacing: 0.06em;
}

.drawer-nav {
  flex: 1 1 auto;
  overflow-y: auto;
  padding: 0 var(--pgpm-space-2) var(--pgpm-space-2);
}

.group-label {
  display: flex;
  align-items: center;
  justify-content: space-between;
  width: 100%;
  border: none;
  background: none;
  cursor: pointer;
  padding: var(--pgpm-space-3) var(--pgpm-space-2) var(--pgpm-space-1);
  font: inherit;
  font-size: 0.786rem;
  font-weight: 600;
  text-transform: uppercase;
  letter-spacing: 0.05em;
  color: var(--pgpm-muted);
  text-align: left;
}

.group-label:hover {
  color: var(--pgpm-text);
}

.group-label i {
  font-size: 0.7rem;
}

.group-divider {
  height: 1px;
  background: var(--pgpm-border);
  margin: var(--pgpm-space-2) var(--pgpm-space-1);
}

.nav-item {
  display: flex;
  align-items: center;
  gap: var(--pgpm-space-3);
  position: relative;
  height: 34px;
  padding: 0 var(--pgpm-space-2);
  border-radius: var(--p-border-radius-md);
  color: var(--pgpm-text);
}

.rail .nav-item {
  justify-content: center;
  padding: 0;
}

.nav-item:hover {
  background: var(--p-content-hover-background);
  text-decoration: none;
}

.nav-item.active {
  background: var(--p-highlight-background);
  color: var(--p-highlight-color);
  font-weight: 500;
}

.nav-item i {
  font-size: 1rem;
  width: 1.25rem;
  text-align: center;
  color: var(--pgpm-muted);
}

.nav-item.active i {
  color: inherit;
}

.drawer-footer {
  padding: var(--pgpm-space-3) var(--pgpm-space-4);
  border-top: 1px solid var(--pgpm-border);
  font-size: 0.857rem;
}

.main-column {
  flex: 1 1 auto;
  min-width: 0;
  display: flex;
  flex-direction: column;
}

.app-bar {
  position: sticky;
  top: 0;
  z-index: 10;
  display: flex;
  align-items: center;
  gap: var(--pgpm-space-2);
  height: var(--pgpm-header-height);
  padding: 0 var(--pgpm-space-2) 0 var(--pgpm-space-1);
  background: var(--pgpm-panel);
  border-bottom: 1px solid var(--pgpm-border);
}

.app-bar-center {
  flex: 1 1 auto;
  display: flex;
  justify-content: center;
  min-width: 0;
}

.app-bar-end {
  display: flex;
  align-items: center;
  gap: var(--pgpm-space-1);
}

.user-button {
  gap: var(--pgpm-space-2);
}

.avatar {
  display: inline-grid;
  place-items: center;
  width: 26px;
  height: 26px;
  border-radius: 50%;
  background: var(--p-primary-color);
  color: var(--p-primary-contrast-color);
  font-size: 0.857rem;
}

.avatar i {
  font-size: 0.8rem;
}

.content {
  flex: 1 1 auto;
  min-width: 0;
  outline: none;
}

.sr-only {
  position: absolute;
  width: 1px;
  height: 1px;
  overflow: hidden;
  clip: rect(0 0 0 0);
}
</style>

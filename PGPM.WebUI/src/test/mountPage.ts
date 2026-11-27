import { QueryClient, VueQueryPlugin } from '@tanstack/vue-query'
import { mount } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import PrimeVue from 'primevue/config'
import ConfirmationService from 'primevue/confirmationservice'
import ConfirmDialog from 'primevue/confirmdialog'
import ToastService from 'primevue/toastservice'
import Tooltip from 'primevue/tooltip'
import { defineComponent, h, type Component } from 'vue'
import type { MeResponse } from '@/api/auth'
import { useSession } from '@/stores/session'

/**
 * A page as the app shows it, for the view tests: PrimeVue (unstyled), toasts, the confirmation dialog, Pinia with a
 * signed-in session (rights, groups), vue-query; RouterLink stubbed. Attached to the body (dialogs teleport there).
 */
export interface PageUser {
  id?: string
  rights?: Record<string, string>
  groupIds?: string[]
  isAdmin?: boolean
  personId?: string | null
}

export function signIn(user: PageUser = {}) {
  const pinia = createPinia()
  setActivePinia(pinia)
  useSession().set({
    user: {
      id: user.id ?? 'u-ana',
      displayName: 'Ana',
      groupIds: user.groupIds ?? [],
      isAdmin: user.isAdmin ?? false,
      rights: user.rights ?? {},
      personId: user.personId ?? null,
    },
    flags: { mustChangePassword: false, totpSetupRequired: false },
    session: { totpDue: false },
  } as unknown as MeResponse)
  return pinia
}

export function mountPage(component: Component, props: Record<string, unknown> = {}, user: PageUser = {}) {
  const pinia = signIn(user)
  const Page = defineComponent({ render: () => [h(component, props), h(ConfirmDialog)] })
  return mount(Page, {
    attachTo: document.body,
    global: {
      plugins: [
        [PrimeVue, { unstyled: true }],
        ToastService,
        ConfirmationService,
        pinia,
        [VueQueryPlugin, { queryClient: new QueryClient() }],
      ],
      directives: { tooltip: Tooltip },
      stubs: { RouterLink: { props: ['to'], template: '<a :data-to="JSON.stringify(to)"><slot /></a>' } },
    },
  })
}

/** A button by its accessible name or its text. */
export function button(name: string | RegExp): HTMLButtonElement | undefined {
  return [...document.body.querySelectorAll('button')].find((b) => {
    const label = b.getAttribute('aria-label') ?? ''
    const text = b.textContent?.trim() ?? ''
    return typeof name === 'string' ? label === name || text === name : name.test(label) || name.test(text)
  })
}

/** A menu entry (PrimeVue's Menu) by its label. */
export function menuItem(label: string): HTMLElement | undefined {
  return [...document.body.querySelectorAll<HTMLElement>('[role="menuitem"]')].find(
    (e) => e.textContent?.trim() === label,
  )
}

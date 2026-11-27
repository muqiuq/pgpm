// First: the install prompt is fired once, early (src/pwa/install.ts).
import './pwa/install'
import { createApp } from 'vue'
import { createPinia } from 'pinia'
import { VueQueryPlugin } from '@tanstack/vue-query'
import PrimeVue from 'primevue/config'
import ToastService from 'primevue/toastservice'
import ConfirmationService from 'primevue/confirmationservice'
import Tooltip from 'primevue/tooltip'
// Self-hosted fonts (the CSP allows fonts from 'self' only).
import '@fontsource-variable/inter'
import '@fontsource-variable/jetbrains-mono'
import 'primeicons/primeicons.css'
import './design/base.css'
import { PgpmPreset, darkClass } from './design/preset'
import { queryClient } from './api/queries'
import { installSessionHandling } from './composables/sessionHandling'
import { installSync } from './local/boot'
import { registerServiceWorker } from './pwa/register'
import App from './App.vue'
import { router } from './router'

const app = createApp(App)
app.use(createPinia())
app.use(router)
app.use(VueQueryPlugin, { queryClient })
app.use(PrimeVue, {
  theme: { preset: PgpmPreset, options: { darkModeSelector: `.${darkClass}`, cssLayer: false } },
  ripple: false,
})
app.use(ToastService)
app.use(ConfirmationService)
app.directive('tooltip', Tooltip)
installSessionHandling(router)
installSync()
app.mount('#app')

void registerServiceWorker()

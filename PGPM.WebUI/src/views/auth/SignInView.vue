<script setup lang="ts">
import { passwordInputProps } from '@/components/passwordInput'
import Button from 'primevue/button'
import InputText from 'primevue/inputtext'
import Message from 'primevue/message'
import Password from 'primevue/password'
import ProgressSpinner from 'primevue/progressspinner'
import { computed, onMounted, ref, useId } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { authApi, microsoftSignInUrl, type SignInOptions } from '@/api/auth'
import { ApiError } from '@/api/client'
import { nextAfterSignIn, safeReturnUrl } from '@/router/guard'
import { useSession } from '@/stores/session'
import { signInMessage, ssoErrors } from './messages'

/**
 * The sign-in (GGITSV's page on PNMTD's login, docs/03 §5.4): *Sign in with Microsoft* as a plain link when the server
 * offers it (a full navigation to Entra and back), the local form below it – collapsed while Microsoft is there – and
 * the reason a Microsoft sign-in came back with. With 2FA the code follows at /sign-in/2fa.
 */
const route = useRoute()
const router = useRouter()
const session = useSession()
const localId = useId()

const options = ref<SignInOptions | null>(null)
const optionsFailed = ref(false)
const localOpen = ref(false)
const username = ref('')
const password = ref('')
const busy = ref(false)
const error = ref<string | null>(null)

const returnUrl = computed(() => safeReturnUrl(route.query.returnUrl))
const microsoftUrl = computed(() => microsoftSignInUrl(returnUrl.value))
const ssoError = computed(() => {
  const reason = route.query.ssoError
  return typeof reason === 'string' && reason ? (ssoErrors[reason] ?? ssoErrors.failed) : null
})
const notice = computed(() =>
  route.query.reason === 'expired'
    ? 'Please sign in again: PGPM asks for it once a month.'
    : route.query.expired
      ? 'The sign-in has expired. Enter your password again.'
      : null,
)

async function loadOptions() {
  optionsFailed.value = false
  try {
    options.value = await authApi.options()
  } catch {
    optionsFailed.value = true
    // Offline or unreachable: the local form, which says so when it is tried.
    options.value = { microsoft: false, local: true }
  }
}

onMounted(loadOptions)

async function submit() {
  if (!username.value.trim() || !password.value) {
    error.value = 'Enter your username and password.'
    return
  }
  busy.value = true
  error.value = null
  try {
    const result = await authApi.login(username.value.trim(), password.value)
    if (result.next === 'totp') {
      await router.push({ name: 'sign-in-2fa', query: returnUrl.value ? { returnUrl: returnUrl.value } : {} })
      return
    }
    await session.signedIn(result.me!)
    await router.replace(nextAfterSignIn(result.me!.flags, returnUrl.value))
  } catch (e) {
    error.value = signInMessage(e)
    if (e instanceof ApiError && e.code === 'invalid_credentials') {
      password.value = ''
    }
  } finally {
    busy.value = false
  }
}
</script>

<template>
  <div class="form">
    <h1>Sign in</h1>
    <Message v-if="ssoError" severity="error" :closable="false" role="alert">{{ ssoError }}</Message>
    <Message v-else-if="notice && !error" severity="info" :closable="false">{{ notice }}</Message>
    <Message v-if="error" severity="error" :closable="false" role="alert">{{ error }}</Message>

    <div v-if="!options" class="center"><ProgressSpinner style="width: 32px; height: 32px" /></div>
    <template v-else>
      <!-- A link, because it leaves the app: the server redirects to Microsoft. -->
      <Button
        v-if="options.microsoft"
        as="a"
        :href="microsoftUrl"
        icon="pi pi-microsoft"
        label="Sign in with Microsoft"
        fluid
      />
      <template v-if="options.local">
        <Button
          v-if="options.microsoft"
          text
          class="toggle"
          :icon="localOpen ? 'pi pi-chevron-down' : 'pi pi-chevron-right'"
          label="Sign in with username and password"
          :aria-expanded="localOpen"
          :aria-controls="localId"
          @click="localOpen = !localOpen"
        />
        <!-- v-show: what was typed survives folding, and aria-controls always names an element. -->
        <form v-show="localOpen || !options.microsoft" :id="localId" class="form" novalidate @submit.prevent="submit">
          <div class="field">
            <label for="username">Username</label>
            <InputText
              id="username"
              v-model="username"
              autocomplete="username"
              :autofocus="!options.microsoft"
              autocapitalize="none"
              spellcheck="false"
            />
          </div>
          <div class="field">
            <label for="password">Password</label>
            <Password
              v-model="password"
              input-id="password"
              :feedback="false"
              toggle-mask
              fluid
              :input-props="passwordInputProps('current-password')"
            />
          </div>
          <Button
            type="submit"
            label="Sign in"
            :loading="busy"
            :severity="options.microsoft ? 'secondary' : undefined"
            fluid
          />
        </form>
      </template>
      <Message v-if="!options.microsoft && !options.local" severity="warn" :closable="false">
        Signing in with a password is not allowed from this network.
      </Message>
      <p v-if="optionsFailed" class="muted small">The server cannot be reached right now.</p>
    </template>
  </div>
</template>

<style scoped>
.toggle {
  align-self: flex-start;
  padding-left: 0;
}

.center {
  display: flex;
  justify-content: center;
}

.small {
  margin: 0;
  font-size: 0.857rem;
}
</style>

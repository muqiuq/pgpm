import { createRouter, createWebHistory, type RouteComponent, type RouteRecordRaw } from 'vue-router'
import type { Right } from '@/api/rights'
import { itemTabs, itemTypeList, itemTypes, typeOfList } from '@/domain/itemTypes'
import { navigation } from '@/layouts/navigation'
import { useSession } from '@/stores/session'
import { decide } from './guard'

declare module 'vue-router' {
  interface RouteMeta {
    /** AuthLayout (a centred card) for the sign-in screens; AppLayout otherwise. */
    layout?: 'auth'
    /** No sign-in needed (the sign-in screens). */
    public?: boolean
    /** What it needs (docs/03 §6.1); the API enforces the same. */
    right?: Right
    title?: string
    /** Breadcrumbs above the title. */
    crumbs?: { label: string; to?: string }[]
    /** A page that is still a placeholder: the build plan's phase that brings it (docs/05 §3), and what it will show. */
    phase?: number
    summary?: string
    icon?: string
  }
}

const placeholder = () => import('@/views/PlaceholderView.vue')

const itemList = () => import('@/views/items/ItemsView.vue')
const itemPage = () => import('@/views/items/ItemView.vue')
const itemEdit = () => import('@/views/items/ItemEditView.vue')

/** The menu entries that are pages already; the others show their placeholder. */
const views: Record<string, () => Promise<RouteComponent>> = {
  users: () => import('@/views/admin/UsersView.vue'),
  groups: () => import('@/views/admin/GroupsView.vue'),
  'audit-log': () => import('@/views/admin/AuditLogView.vue'),
  ...Object.fromEntries(itemTypeList.map((t) => [itemTypes[t].list, itemList])),
  trash: () => import('@/views/trash/TrashView.vue'),
  persons: () => import('@/views/persons/PersonsView.vue'),
  statuses: () => import('@/views/admin/StatusesView.vue'),
}

/** Each menu entry's placeholder until its phase replaces the component (docs/05 §3). */
const pages: Record<string, { phase: number; summary: string }> = {
  maps: { phase: 5, summary: 'Canvas and matrix maps of ideas, projects, milestones and shapes, with templates.' },
  'admin-mcp': { phase: 6, summary: 'MCP tokens of all users and the MCP log.' },
  sections: { phase: 4, summary: 'Sections and fields of ideas and projects: ratings, scores, selects and more.' },
  templates: { phase: 4, summary: 'Project templates: statuses, sections, defaults, milestones and gates.' },
  packages: { phase: 4, summary: 'Import and export configuration packages such as the innovation method.' },
  microsoft: { phase: 6, summary: 'Microsoft SSO and the directory sync: settings, last result, Run now.' },
  system: { phase: 7, summary: 'Sync clients, live connections, storage and versions.' },
}

/** The menu's entries as routes: path, name, title, icon and right from navigation.ts, the phase from pages. */
const menuRoutes: RouteRecordRaw[] = navigation.flatMap((group) =>
  group.items.map((item) => ({
    path: item.to,
    name: item.name,
    component: views[item.name] ?? placeholder,
    // The item lists are one view for every type.
    ...(typeOfList(item.name) ? { props: { type: typeOfList(item.name) } } : {}),
    meta: {
      title: item.label,
      icon: item.icon,
      right: item.right,
      crumbs: group.label === 'Administration' ? [{ label: group.label }] : undefined,
      ...pages[item.name],
    },
  })),
)

export const routes: RouteRecordRaw[] = [
  { path: '/', redirect: { name: 'maps' } },
  ...menuRoutes,
  // Per item type: the description's editing page (docs/03 §18.2) before the page with its tabs (§12.2).
  ...itemTypeList.flatMap((type): RouteRecordRaw[] => {
    const info = itemTypes[type]
    return [
      {
        path: `/${info.list}/:id/edit`,
        name: info.edit,
        component: itemEdit,
        props: (route) => ({ type, id: route.params.id }),
        meta: { title: `Edit ${info.label}`, icon: info.icon },
      },
      {
        path: `/${info.list}/:id/:tab(${itemTabs(type).join('|')})?`,
        name: info.page,
        component: itemPage,
        props: (route) => ({ type, id: route.params.id, tab: route.params.tab || undefined }),
        meta: { title: info.title, icon: info.icon, crumbs: [{ label: info.plural, to: `/${info.list}` }] },
      },
    ]
  }),
  {
    path: '/persons/:id',
    name: 'person',
    component: () => import('@/views/persons/PersonView.vue'),
    props: true,
    meta: { title: 'Person', icon: 'pi pi-address-book', crumbs: [{ label: 'Persons', to: '/persons' }] },
  },
  {
    path: '/admin/groups/:id',
    name: 'group',
    component: () => import('@/views/admin/GroupView.vue'),
    props: true,
    meta: { title: 'Group', right: 'admin', icon: 'pi pi-sitemap' },
  },
  // The sign-in screens (AuthLayout): public, or – for what a session must do first – signed in.
  {
    path: '/sign-in',
    name: 'sign-in',
    component: () => import('@/views/auth/SignInView.vue'),
    meta: { title: 'Sign in', layout: 'auth', public: true },
  },
  {
    path: '/sign-in/2fa',
    name: 'sign-in-2fa',
    component: () => import('@/views/auth/SignInTotpView.vue'),
    meta: { title: 'Sign in', layout: 'auth', public: true },
  },
  {
    path: '/change-password',
    name: 'change-password',
    component: () => import('@/views/auth/ChangePasswordView.vue'),
    meta: { title: 'New password', layout: 'auth' },
  },
  {
    path: '/account/setup-2fa',
    name: 'setup-2fa',
    component: () => import('@/views/account/SetupTotpView.vue'),
    meta: { title: 'Two-factor authentication', layout: 'auth' },
  },
  {
    path: '/account',
    name: 'account',
    component: () => import('@/views/account/AccountView.vue'),
    meta: { title: 'Account', icon: 'pi pi-user' },
  },
  {
    path: '/account/security',
    name: 'security',
    component: () => import('@/views/account/SecurityView.vue'),
    meta: { title: 'Security', icon: 'pi pi-shield' },
  },
  // The Markdown editor and renderer side by side, for checking them by hand (docs/03 §18): development only, the
  // build leaves it out.
  ...(import.meta.env.DEV
    ? [
        {
          path: '/dev/markdown',
          name: 'dev-markdown',
          component: () => import('@/views/dev/MarkdownPlayground.vue'),
          meta: { title: 'Markdown playground', public: true },
        },
        // The map canvas and matrix over the in-memory data source, with the frame-time benchmark (docs/03 §13).
        {
          path: '/dev/map',
          name: 'dev-map',
          component: () => import('@/maps/playground/MapPlayground.vue'),
          meta: { title: 'Map playground', public: true },
        },
      ]
    : []),
  {
    path: '/:pathMatch(.*)*',
    name: 'not-found',
    component: () => import('@/views/NotFoundView.vue'),
    meta: { title: 'Not found' },
  },
]

export const router = createRouter({
  history: createWebHistory(import.meta.env.BASE_URL),
  routes,
  scrollBehavior: (_to, _from, saved) => saved ?? { top: 0 },
})

router.beforeEach(async (to) => {
  const session = useSession()
  // With the offline copy of /auth/me this answers at once; only a first visit waits for the server.
  await session.load()
  return decide(
    { name: to.name, fullPath: to.fullPath, public: to.meta.public, right: to.meta.right },
    { signedIn: session.user !== null, flags: session.flags, allows: session.allows },
  )
})

/** "Projects · PGPM". */
export function pageTitle(title: string | undefined): string {
  return [title, 'PGPM'].filter((p) => p).join(' · ')
}

router.afterEach((to) => {
  document.title = pageTitle(to.meta.title)
})

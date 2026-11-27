import { createRouter, createWebHistory, type RouteRecordRaw } from 'vue-router'
import type { Right } from '@/api/rights'
import { navigation } from '@/layouts/navigation'
import { useSession } from '@/stores/session'
import { decide } from './guard'

declare module 'vue-router' {
  interface RouteMeta {
    /** AuthLayout (a centred card) for the sign-in screens (phase 1); AppLayout otherwise. */
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

/** Each menu entry's placeholder until its phase replaces the component (docs/05 §3). */
const pages: Record<string, { phase: number; summary: string }> = {
  maps: { phase: 5, summary: 'Canvas and matrix maps of ideas, projects, milestones and shapes, with templates.' },
  projects: { phase: 3, summary: 'Projects with templates, statuses, sections, milestones and gates.' },
  leads: { phase: 3, summary: 'External opportunities from New to Won or Lost, converted into projects.' },
  ideas: { phase: 3, summary: 'Candidate use cases with assessments, scores and review-board decisions.' },
  notes: { phase: 2, summary: 'Quick capture and learnings – the first data to sync offline and live.' },
  trash: { phase: 2, summary: 'Deleted items and maps, restorable until an admin purges them.' },
  persons: { phase: 3, summary: 'The people directory: Microsoft Entra users and manual externals.' },
  users: { phase: 1, summary: 'Local and Microsoft accounts, 2FA, lockout and sessions.' },
  groups: { phase: 1, summary: 'Groups, their members and their rights.' },
  'admin-mcp': { phase: 6, summary: 'MCP tokens of all users and the MCP log.' },
  statuses: { phase: 3, summary: 'Statuses per item type: open, active or closed, and how long an item may stay.' },
  sections: { phase: 4, summary: 'Sections and fields of ideas and projects: ratings, scores, selects and more.' },
  templates: { phase: 4, summary: 'Project templates: statuses, sections, defaults, milestones and gates.' },
  packages: { phase: 4, summary: 'Import and export configuration packages such as the innovation method.' },
  microsoft: { phase: 6, summary: 'Microsoft SSO and the directory sync: settings, last result, Run now.' },
  'audit-log': { phase: 1, summary: 'Who changed what in the administration, and every sign-in.' },
  system: { phase: 7, summary: 'Sync clients, live connections, storage and versions.' },
}

/** The menu's entries as routes: path, name, title, icon and right from navigation.ts, the phase from pages. */
const menuRoutes: RouteRecordRaw[] = navigation.flatMap((group) =>
  group.items.map((item) => ({
    path: item.to,
    name: item.name,
    component: placeholder,
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
  {
    path: '/account',
    name: 'account',
    component: placeholder,
    meta: { title: 'Account', icon: 'pi pi-user', phase: 1, summary: 'Your password, 2FA, sessions and MCP tokens.' },
  },
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

router.beforeEach((to) => {
  const session = useSession()
  session.load()
  return decide(
    { name: to.name, fullPath: to.fullPath, public: to.meta.public, right: to.meta.right },
    { signedIn: session.user !== null, allows: session.allows },
  )
})

/** "Projects · PGPM". */
export function pageTitle(title: string | undefined): string {
  return [title, 'PGPM'].filter((p) => p).join(' · ')
}

router.afterEach((to) => {
  document.title = pageTitle(to.meta.title)
})

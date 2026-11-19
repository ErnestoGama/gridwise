import { createRouter, createWebHistory } from 'vue-router'

import { useSessionStore } from '@/stores/session'
import BuildingDetailView from '@/views/BuildingDetailView.vue'
import BuildingsView from '@/views/BuildingsView.vue'
import LoginView from '@/views/LoginView.vue'
import StatusView from '@/views/StatusView.vue'

declare module 'vue-router' {
  interface RouteMeta {
    public?: boolean
  }
}

const router = createRouter({
  history: createWebHistory(import.meta.env.BASE_URL),
  routes: [
    { path: '/', name: 'home', component: { render: () => null } },
    { path: '/login', name: 'login', component: LoginView, meta: { public: true } },
    { path: '/status', name: 'status', component: StatusView, meta: { public: true } },
    {
      path: '/organizations/:organizationId/buildings',
      name: 'buildings',
      component: BuildingsView,
      props: true,
    },
    {
      path: '/organizations/:organizationId/buildings/:buildingId',
      name: 'building',
      component: BuildingDetailView,
      props: true,
    },
    { path: '/:pathMatch(.*)*', redirect: { name: 'home' } },
  ],
})

router.beforeEach(async (to) => {
  const session = useSessionStore()
  try {
    await session.ensureLoaded()
  } catch {
    // API down: the status page explains which dependency failed.
    return to.name === 'status' ? true : { name: 'status' }
  }

  if (to.meta.public) {
    return to.name === 'login' && session.signedIn ? { name: 'home' } : true
  }
  if (!session.signedIn) {
    return { name: 'login', query: { next: to.fullPath } }
  }
  if (to.name === 'home') {
    const first = session.me?.memberships[0]
    return first
      ? { name: 'buildings', params: { organizationId: first.organizationId } }
      : { name: 'status' }
  }
  return true
})

export default router

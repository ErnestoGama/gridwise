import { createRouter, createWebHistory } from 'vue-router'

import StatusView from '@/views/StatusView.vue'

export default createRouter({
  history: createWebHistory(import.meta.env.BASE_URL),
  routes: [{ path: '/', name: 'status', component: StatusView }],
})

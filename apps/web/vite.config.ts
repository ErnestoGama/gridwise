import { fileURLToPath, URL } from 'node:url'
import { defineConfig } from 'vite'
import vue from '@vitejs/plugin-vue'

const apiTarget = process.env.GRIDWISE_API_URL ?? 'http://localhost:5080'

export default defineConfig({
  plugins: [vue()],
  resolve: {
    alias: {
      '@': fileURLToPath(new URL('./src', import.meta.url)),
    },
  },
  server: {
    host: '127.0.0.1',
    proxy: {
      '/health': apiTarget,
      '/v1': apiTarget,
      // SignalR negotiates over HTTP, then upgrades to a WebSocket.
      '/hubs': { target: apiTarget, ws: true },
    },
  },
})

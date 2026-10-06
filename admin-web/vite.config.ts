import react from '@vitejs/plugin-react'
import { defineConfig } from 'vite'

// Served by the WordOS API itself under /admin in production (same origin, so
// no CORS and no second deployment). In development, /api is proxied to the
// local backend that `./wordos start` brings up.
export default defineConfig({
  base: '/admin/',
  plugins: [react()],
  server: {
    port: 5180,
    // Reachable from other devices on the same Wi-Fi, so a teammate can open
    // the dashboard from this Mac's address. Development only — production is
    // served by the API itself.
    host: true,
    proxy: {
      '/api': { target: 'http://127.0.0.1:5199', changeOrigin: false },
    },
  },
})

import { defineConfig } from 'vitest/config'
import vue from '@vitejs/plugin-vue'
import { VitePWA } from 'vite-plugin-pwa'
import { fileURLToPath, URL } from 'node:url'

// The UI lives at / (PGPM serves it from wwwroot, docs/03 §2.4). In development (WEB=dev ./start.sh) Vite serves it with
// HMR and proxies the API to the app; PGPM_API_URL is the app's address (inside start.sh's container: its name on the
// network). The service worker exists in the build only.
const api = process.env.PGPM_API_URL ?? 'http://localhost:7330'

/** The installed app (docs/03 §11.2): static, served as /manifest.webmanifest. */
const manifest = {
  id: '/',
  name: 'PGPM',
  short_name: 'PGPM',
  description: 'Capture and drive innovation work – ideas, leads, projects and maps, offline and live.',
  lang: 'en',
  start_url: '/',
  scope: '/',
  display: 'standalone' as const,
  launch_handler: { client_mode: 'focus-existing' as const },
  theme_color: '#3b59a5',
  background_color: '#f1f5f9',
  icons: [
    { src: '/pwa-192.png', sizes: '192x192', type: 'image/png', purpose: 'any' },
    { src: '/pwa-512.png', sizes: '512x512', type: 'image/png', purpose: 'any' },
    { src: '/pwa-maskable-512.png', sizes: '512x512', type: 'image/png', purpose: 'maskable' },
    { src: '/favicon.svg', sizes: 'any', type: 'image/svg+xml', purpose: 'any' },
  ],
  // Phase 3 turns these into "New note" and "New idea".
  shortcuts: [
    { name: 'Maps', url: '/maps' },
    { name: 'Ideas', url: '/ideas' },
    { name: 'Notes', url: '/notes' },
  ],
}

export default defineConfig({
  base: '/',
  plugins: [
    vue(),
    // Not under Vitest: it has nothing to build there.
    ...(process.env.VITEST
      ? []
      : [
          VitePWA({
            // src/sw.ts is ours (PLM's behaviours, docs/03 §11.1); the plugin builds it and injects the precache list.
            strategies: 'injectManifest',
            srcDir: 'src',
            filename: 'sw.ts',
            // src/pwa/register.ts registers it, with the update toast.
            injectRegister: false,
            manifestFilename: 'manifest.webmanifest',
            includeAssets: ['favicon.svg', 'apple-touch-icon.png'],
            manifest,
            injectManifest: {
              rollupFormat: 'iife',
              // Everything the shell needs offline: code, styles and woff2 fonts (the plugin adds the manifest, its icons and
              // includeAssets itself; PrimeIcons' svg/ttf/eot fonts are for browsers without woff2). Images imported into
              // assets/ join with phase 3. 10 MB: the editor's chunks (Monaco) from phase 3.
              globPatterns: ['**/*.{js,css,html,woff2}'],
              maximumFileSizeToCacheInBytes: 10 * 1024 * 1024,
            },
            devOptions: { enabled: false },
          }),
        ]),
  ],
  resolve: { alias: { '@': fileURLToPath(new URL('./src', import.meta.url)) } },
  server: {
    host: true,
    port: 5173,
    strictPort: true,
    proxy: {
      // ws: the live sync channel (phase 2, /api/v1/sync/live).
      '/api': { target: api, changeOrigin: false, secure: false, ws: true },
      '/healthz': { target: api, changeOrigin: false, secure: false },
    },
  },
  build: {
    outDir: 'dist',
    assetsDir: 'assets',
    sourcemap: false,
    // No data: URIs: the CSP allows fonts, images and scripts from 'self' (and blob: images) only (PGPM/Hosting/HostingSetup.cs).
    assetsInlineLimit: 0,
    chunkSizeWarningLimit: 1200,
  },
  worker: { format: 'es' },
  test: {
    environment: 'jsdom',
    include: ['src/**/*.test.ts'],
    restoreMocks: true,
  },
})

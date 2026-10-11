/// <reference types="vitest/config" />
import { fileURLToPath, URL } from 'node:url';
import preact from '@preact/preset-vite';
import { defineConfig } from 'vite';

// One bundle for both hosts: the server serves dist/ at /altruist/dashboard (embedded in Altruist.Dashboard), and the
// VS Code extension loads the same files into its webview (VSCode/media/app). Relative paths work under both.
export default defineConfig({
  base: './',
  plugins: [preact()],
  resolve: { alias: { '@': fileURLToPath(new URL('./src', import.meta.url)) } },
  build: {
    outDir: 'dist',
    emptyOutDir: true,
    assetsDir: 'assets',
    sourcemap: false,
    chunkSizeWarningLimit: 1500,
    rollupOptions: { output: { entryFileNames: 'assets/[name].js', chunkFileNames: 'assets/[name].js', assetFileNames: 'assets/[name][extname]' } },
  },
  // `npm run dev` against a local server: ALTRUIST_SERVER=http://localhost:8000 (default 8080).
  server: {
    proxy: { '/dashboard': process.env.ALTRUIST_SERVER ?? 'http://localhost:8080' },
  },
  test: { environment: 'node', include: ['tests/**/*.test.ts'] },
});

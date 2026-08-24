import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';
import { configDefaults } from 'vitest/config';
import pkg from './package.json' with { type: 'json' };

export default defineConfig({
  base: '/dashboard/',
  plugins: [react()],
  define: {
    __APP_VERSION__: JSON.stringify(pkg.version),
  },
  test: {
    environment: 'jsdom',
    globals: true,
    pool: 'threads',
    setupFiles: './src/test/setup.ts',
    // This checkout lives on an exFAT volume: macOS writes AppleDouble `._*`
    // sidecars beside real files, and vitest must never treat them as tests.
    exclude: [...(configDefaults.exclude ?? []), '**/._*'],
  },
  build: {
    chunkSizeWarningLimit: 1000,
  },
  server: {
    port: 3000,
    proxy: {
      '/api': {
        target: process.env.VITE_ARMADA_SERVER_URL || 'http://localhost:5000',
        changeOrigin: true,
      },
    },
  },
});

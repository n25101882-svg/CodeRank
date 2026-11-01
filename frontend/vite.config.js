import { defineConfig } from 'vite';
import { fileURLToPath } from 'node:url';

export default defineConfig({
  appType: 'spa',
  server: {
    host: 'localhost', port: 5173, strictPort: true,
    fs: { allow: [fileURLToPath(new URL('.', import.meta.url)), fileURLToPath(new URL('../shared/validation', import.meta.url))] },
  },
  preview: { host: 'localhost', port: 5173, strictPort: true },
});

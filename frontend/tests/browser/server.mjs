import { fileURLToPath } from 'node:url';
import { createServer } from 'vite';

// Own the server in-process so teardown doesn't require killing a Windows shell/process tree.
// A busy port fails through strictPort; an unrelated development server is never reused.
export default async function setup() {
  process.env.VITE_API_BASE_URL = 'https://localhost:7160';
  const server = await createServer({
    root: fileURLToPath(new URL('../../', import.meta.url)),
    configFile: fileURLToPath(new URL('../../vite.config.js', import.meta.url)),
    mode: 'test',
    logLevel: 'error',
    server: { port: Number(process.env.CODERANK_TEST_PORT || 5173), strictPort: true },
  });
  try { await server.listen(); }
  catch (error) { await server.close(); throw error; }
  return () => server.close();
}

export class ApiError extends Error {
  constructor(code, { status = 0, fields = [] } = {}) {
    super(code);
    this.name = 'ApiError';
    this.code = code;
    this.status = status;
    this.fields = fields;
  }
}

export function createApiClient({ baseUrl, fetchImpl = globalThis.fetch, timeoutMs = 15_000 }) {
  const base = new URL(baseUrl);
  if (!['http:', 'https:'].includes(base.protocol) || base.username || base.password || base.search || base.hash) {
    throw new Error('Invalid API base URL');
  }
  const root = base.href.replace(/\/$/, '');

  async function request(path, { body, token, signal } = {}) {
    const controller = new AbortController();
    let timedOut = false;
    const cancel = () => controller.abort();
    if (signal?.aborted) throw new DOMException('Request cancelled', 'AbortError');
    signal?.addEventListener('abort', cancel, { once: true });
    const timeout = setTimeout(() => { timedOut = true; controller.abort(); }, timeoutMs);
    try {
      const headers = { Accept: 'application/json' };
      if (body) headers['Content-Type'] = 'application/json';
      if (token) headers.Authorization = `Bearer ${token}`;
      const response = await fetchImpl(`${root}${path}`, {
        method: body ? 'POST' : 'GET', headers,
        body: body ? JSON.stringify(body) : undefined,
        credentials: 'omit', cache: 'no-store', redirect: 'error', signal: controller.signal,
      });
      let data = null;
      if (response.headers.get('content-type')?.includes('json')) {
        try { data = await response.json(); } catch { /* Never display raw server bodies. */ }
      }
      if (!response.ok) {
        const fields = data?.errors && typeof data.errors === 'object' ? Object.keys(data.errors) : [];
        throw new ApiError('http', { status: response.status, fields });
      }
      if (!data || typeof data !== 'object') throw new ApiError('response');
      return data;
    } catch (error) {
      if (signal?.aborted) throw new DOMException('Request cancelled', 'AbortError');
      if (timedOut) throw new ApiError('timeout');
      if (error instanceof ApiError) throw error;
      throw new ApiError('network');
    } finally {
      clearTimeout(timeout);
      signal?.removeEventListener('abort', cancel);
    }
  }

  return {
    register: (data, options = {}) => request('/api/v1/auth/register', { ...options, body: data }),
    async login(data, options = {}) {
      const result = await request('/api/v1/auth/login', { ...options, body: data });
      if (typeof result.accessToken !== 'string' || !result.accessToken || /\s/.test(result.accessToken)
        || result.tokenType !== 'Bearer' || !Number.isFinite(Date.parse(result.expiresAtUtc))) {
        throw new ApiError('response');
      }
      return result;
    },
    async me(token, options = {}) {
      const user = await request('/api/v1/users/me', { ...options, token });
      if (typeof user.email !== 'string' || typeof user.role !== 'string'
        || !(user.firstName === null || typeof user.firstName === 'string')
        || !(user.lastName === null || typeof user.lastName === 'string')) {
        throw new ApiError('response');
      }
      return { email: user.email, firstName: user.firstName, lastName: user.lastName, role: user.role };
    },
  };
}

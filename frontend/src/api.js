import { PROBLEM_FIELDS, isProblemResponse, validateProblem } from './problems-model.js';

export class ApiError extends Error {
  constructor(code, { status = 0, fields = [] } = {}) {
    super(code);
    this.name = 'ApiError';
    this.code = code;
    this.status = status;
    this.fields = fields;
  }
}

function utcMilliseconds(value) {
  if (typeof value !== 'string' || !/^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d{1,7})?Z$/.test(value)) return NaN;
  const milliseconds = Date.parse(value);
  // Date.parse alone normalizes impossible dates such as February 30.
  return Number.isFinite(milliseconds) && new Date(milliseconds).toISOString().slice(0, 19) === value.slice(0, 19)
    ? milliseconds : NaN;
}

const isPositiveInteger = (value) => Number.isSafeInteger(value) && value > 0;
const isOptionalText = (value) => value === null || typeof value === 'string';

function olympiadPath(id) {
  if (!isPositiveInteger(id)) throw new ApiError('http', { status: 404 });
  return `/api/v1/olympiads/${id}`;
}

function olympiadResponse(item) {
  const starts = utcMilliseconds(item?.startsAtUtc);
  const ends = utcMilliseconds(item?.endsAtUtc);
  if (!item || !isPositiveInteger(item.id) || typeof item.title !== 'string' || !item.title.trim()
    || !isOptionalText(item.description) || !Number.isFinite(starts) || !(ends > starts)) {
    throw new ApiError('response');
  }
  const { id, title, description, startsAtUtc, endsAtUtc } = item;
  return { id, title, description, startsAtUtc, endsAtUtc };
}

function olympiadProblemSummary(item) {
  if (!item || !isPositiveInteger(item.id) || typeof item.title !== 'string' || !item.title.trim()
    || !isOptionalText(item.difficulty) || !isPositiveInteger(item.maxPoints)) {
    throw new ApiError('response');
  }
  const { id, title, difficulty, maxPoints } = item;
  return { id, title, difficulty, maxPoints };
}

function olympiadProblemDetail(item, problemId) {
  const summary = olympiadProblemSummary(item);
  const { statement, inputFormat, outputFormat, examples } = item;
  if (summary.id !== problemId || typeof statement !== 'string'
    || !isOptionalText(inputFormat) || !isOptionalText(outputFormat) || !Array.isArray(examples)) {
    throw new ApiError('response');
  }
  const parsedExamples = examples.map((example) => {
    if (typeof example?.inputData !== 'string' || typeof example.expectedOutput !== 'string'
      || !isOptionalText(example.explanation)) {
      throw new ApiError('response');
    }
    const { inputData, expectedOutput, explanation } = example;
    return { inputData, expectedOutput, explanation };
  });
  return { ...summary, statement, inputFormat, outputFormat, examples: parsedExamples };
}

export function createApiClient({ baseUrl, fetchImpl = globalThis.fetch, timeoutMs = 15_000 }) {
  const base = new URL(baseUrl);
  if (!['http:', 'https:'].includes(base.protocol) || base.username || base.password || base.search || base.hash) {
    throw new Error('Invalid API base URL');
  }
  const root = base.href.replace(/\/$/, '');

  async function request(path, { body, token, signal, method = body ? 'POST' : 'GET', expectedStatus } = {}) {
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
        method, headers,
        body: body ? JSON.stringify(body) : undefined,
        credentials: 'omit', cache: 'no-store', redirect: 'error', signal: controller.signal,
      });
      if (response.status === 204) {
        if (expectedStatus === 204) return;
        throw new ApiError('response');
      }
      let data = null;
      if (response.headers.get('content-type')?.includes('json')) {
        try { data = await response.json(); } catch { /* Never display raw server bodies. */ }
      }
      if (!response.ok) {
        const fields = data?.errors && typeof data.errors === 'object' ? Object.keys(data.errors) : [];
        throw new ApiError('http', { status: response.status, fields });
      }
      if (expectedStatus !== undefined && response.status !== expectedStatus) throw new ApiError('response');
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

  function authorized(path, token, options) {
    if (typeof token !== 'string' || !token || /\s/.test(token)) return Promise.reject(new ApiError('http', { status: 401 }));
    return request(path, { ...options, token });
  }

  function problemPath(id) {
    if (!Number.isSafeInteger(id) || id <= 0) throw new ApiError('response');
    return `/api/v1/problems/${id}`;
  }

  function problemBody(data) {
    const { values, errors } = validateProblem(data);
    if (Object.keys(errors).length) throw new ApiError('validation', { fields: Object.keys(errors) });
    return values; // Explicit writable fields only; no id, owner or creation timestamp.
  }

  function problemResponse(value) {
    if (!isProblemResponse(value)) throw new ApiError('response');
    return Object.fromEntries(['id', ...PROBLEM_FIELDS, 'createdAtUtc'].map((field) => [field, value[field]]));
  }

  return {
    async olympiads({ signal } = {}) {
      // Public endpoint: never forward the client session or arbitrary request options.
      const result = await request('/api/v1/olympiads', { signal });
      if (!Array.isArray(result)) throw new ApiError('response');
      return result.map(olympiadResponse);
    },
    async getOlympiad(id, { signal } = {}) {
      const result = olympiadResponse(await request(olympiadPath(id), { signal, expectedStatus: 200 }));
      if (result.id !== id) throw new ApiError('response');
      return result;
    },
    async listOlympiadProblems(id, { signal } = {}) {
      const result = await request(`${olympiadPath(id)}/problems`, { signal, expectedStatus: 200 });
      if (!Array.isArray(result)) throw new ApiError('response');
      return result.map(olympiadProblemSummary);
    },
    async getOlympiadProblem(id, problemId, { signal } = {}) {
      if (!isPositiveInteger(problemId)) throw new ApiError('http', { status: 404 });
      return olympiadProblemDetail(await request(`${olympiadPath(id)}/problems/${problemId}`,
        { signal, expectedStatus: 200 }), problemId);
    },
    async listProblems(token, { signal } = {}) {
      const result = await authorized('/api/v1/problems', token, { signal, expectedStatus: 200 });
      if (!Array.isArray(result)) throw new ApiError('response');
      return result.map(problemResponse);
    },
    async getProblem(id, token, { signal } = {}) {
      return problemResponse(await authorized(problemPath(id), token, { signal, expectedStatus: 200 }));
    },
    async createProblem(data, token, { signal } = {}) {
      return problemResponse(await authorized('/api/v1/problems', token,
        { signal, method: 'POST', body: problemBody(data), expectedStatus: 201 }));
    },
    updateProblem: (id, data, token, { signal } = {}) => authorized(problemPath(id), token,
      { signal, method: 'PUT', body: problemBody(data), expectedStatus: 204 }),
    deleteProblem: (id, token, { signal } = {}) => authorized(problemPath(id), token,
      { signal, method: 'DELETE', expectedStatus: 204 }),
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

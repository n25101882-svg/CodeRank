import test from 'node:test';
import assert from 'node:assert/strict';
import { ApiError, createApiClient } from '../../src/api.js';
import { validateProblem } from '../../src/problems-model.js';

const row = { id: 1, title: 'Сума чисел', statement: 'Знайдіть суму.', inputFormat: null,
  outputFormat: null, difficulty: 'Easy', createdAtUtc: '2026-11-01T10:00:00Z' };
const json = (data, status = 200) => new Response(JSON.stringify(data), { status, headers: { 'content-type': 'application/json' } });
const token = () => crypto.randomUUID();
const data = () => ({ title: '  Назва  ', statement: '  Умова\n', inputFormat: '', outputFormat: ' Результат ', difficulty: '' });

test('problem validation normalizes only Title and empty optional values and excludes server fields', () => {
  const result = validateProblem({ ...data(), id: 9, createdByUserId: 99, createdAtUtc: '2000-01-01' });
  assert.deepEqual(result.errors, {});
  assert.deepEqual(result.values, { title: 'Назва', statement: '  Умова\n', inputFormat: null, outputFormat: ' Результат ', difficulty: null });
});

for (const [name, patch, field] of [
  ['missing title', { title: undefined }, 'title'], ['blank title', { title: ' \t ' }, 'title'],
  ['long title', { title: 'x'.repeat(201) }, 'title'], ['missing statement', { statement: null }, 'statement'],
  ['blank statement', { statement: '\r\n ' }, 'statement'], ['wrong difficulty', { difficulty: 'Expert' }, 'difficulty'],
  ['lowercase difficulty', { difficulty: 'easy' }, 'difficulty'], ['array difficulty', { difficulty: ['Easy'] }, 'difficulty'],
  ['object input format', { inputFormat: {} }, 'inputFormat'], ['numeric output format', { outputFormat: 1 }, 'outputFormat'],
]) {
  test(`problem validation rejects ${name}`, () => {
    assert.ok(validateProblem({ ...data(), ...patch }).errors[field]);
  });
}

for (const difficulty of [null, '', 'Easy', 'Medium', 'Hard']) {
  test(`problem validation accepts difficulty ${JSON.stringify(difficulty)} and the Title boundary`, () => {
    assert.deepEqual(validateProblem({ ...data(), title: 'x'.repeat(200), difficulty }).errors, {});
  });
}

test('list and detail use authenticated GETs and expose only known DTO fields', async () => {
  const accessToken = token();
  const paths = [];
  const api = createApiClient({ baseUrl: 'https://example.test', fetchImpl: async (url, options) => {
    paths.push(url);
    assert.equal(options.method, 'GET');
    assert.equal(options.headers.Authorization, `Bearer ${accessToken}`);
    assert.equal(options.headers['Content-Type'], undefined);
    assert.equal(options.credentials, 'omit');
    assert.ok(!url.includes(accessToken));
    const record = { ...row, internalField: true, createdByUserId: 77 };
    return json(url.endsWith('/1') ? record : [record]);
  } });
  assert.deepEqual(await api.listProblems(accessToken), [row]);
  assert.deepEqual(await api.getProblem(1, accessToken), row);
  assert.deepEqual(paths, ['https://example.test/api/v1/problems', 'https://example.test/api/v1/problems/1']);
});

for (const method of ['POST', 'PUT', 'DELETE']) {
  test(`${method} sends the Bearer header and correct writable DTO; 204 is never parsed`, async () => {
    const accessToken = token();
    let parsed = 0;
    const api = createApiClient({ baseUrl: 'https://example.test', fetchImpl: async (url, options) => {
      assert.equal(options.method, method);
      assert.equal(options.headers.Authorization, `Bearer ${accessToken}`);
      assert.equal(options.redirect, 'error');
      assert.ok(!url.includes(accessToken));
      assert.equal(url, `https://example.test/api/v1/problems${method === 'POST' ? '' : '/1'}`);
      if (method !== 'DELETE') {
        assert.deepEqual(JSON.parse(options.body), validateProblem(data()).values);
        assert.equal(options.headers['Content-Type'], 'application/json');
      } else {
        assert.equal(options.body, undefined);
        assert.equal(options.headers['Content-Type'], undefined);
      }
      if (method === 'POST') return json(row, 201);
      const response = new Response(null, { status: 204, headers: { 'content-type': 'application/json' } });
      response.json = () => { parsed++; throw new Error('Must not parse a 204 body'); };
      return response;
    } });
    const unsafe = { ...data(), id: 88, createdByUserId: 99, createdAtUtc: row.createdAtUtc };
    const result = method === 'POST' ? await api.createProblem(unsafe, accessToken)
      : method === 'PUT' ? await api.updateProblem(1, unsafe, accessToken) : await api.deleteProblem(1, accessToken);
    assert.deepEqual(result, method === 'POST' ? row : undefined);
    assert.equal(parsed, 0);
  });
}

test('an empty problem list remains valid', async () => {
  const api = createApiClient({ baseUrl: 'https://example.test', fetchImpl: async () => json([]) });
  assert.deepEqual(await api.listProblems(token()), []);
});

for (const [name, record] of [
  ['unsafe ID', { ...row, id: Number.MAX_SAFE_INTEGER + 1 }], ['bad difficulty', { ...row, difficulty: 'Expert' }],
  ['missing statement', { ...row, statement: undefined }], ['bad optional field', { ...row, inputFormat: {} }],
  ['bad timestamp', { ...row, createdAtUtc: 'not-a-date' }],
]) {
  test(`problem responses reject ${name}`, async () => {
    const api = createApiClient({ baseUrl: 'https://example.test', fetchImpl: async () => json([record]) });
    await assert.rejects(api.listProblems(token()), (error) => error.code === 'response');
  });
}

for (const status of [400, 401, 404, 409, 500]) {
  test(`Problems HTTP ${status} is a safe structured error`, async () => {
    const detail = crypto.randomUUID();
    const api = createApiClient({ baseUrl: 'https://example.test', fetchImpl: async () => json({ detail, errors: { Title: [detail] } }, status) });
    await assert.rejects(api.deleteProblem(1, token()), (error) => error instanceof ApiError
      && error.status === status && !error.message.includes(detail) && error.fields[0] === 'Title');
  });
}

test('protected client methods refuse a missing token before fetch', async () => {
  const api = createApiClient({ baseUrl: 'https://example.test', fetchImpl: () => { throw new Error('Must not fetch'); } });
  for (const action of [() => api.listProblems(null), () => api.getProblem(1, null),
    () => api.createProblem(data(), null), () => api.updateProblem(1, data(), null), () => api.deleteProblem(1, null)]) {
    await assert.rejects(action(), (error) => error.status === 401);
  }
});

test('invalid writable data and path IDs fail before network access', async () => {
  const api = createApiClient({ baseUrl: 'https://example.test', fetchImpl: () => { throw new Error('Must not fetch'); } });
  await assert.rejects(api.createProblem({ title: '', statement: '' }, token()), (error) => error.code === 'validation');
  assert.throws(() => api.updateProblem(1, { title: '', statement: '' }, token()), (error) => error.code === 'validation');
  for (const id of [0, -1, '1?token=value', Number.MAX_SAFE_INTEGER + 1]) {
    await assert.rejects(api.getProblem(id, token()), (error) => error.code === 'response');
  }
});

test('unexpected 204 for a GET cannot masquerade as a valid list', async () => {
  const api = createApiClient({ baseUrl: 'https://example.test', fetchImpl: async () => new Response(null, { status: 204 }) });
  await assert.rejects(api.listProblems(token()), (error) => error.code === 'response');
});

test('a response with an incorrect mutation status is rejected', async () => {
  const api = createApiClient({ baseUrl: 'https://example.test', fetchImpl: async () => json(row, 200) });
  await assert.rejects(api.createProblem(data(), token()), (error) => error.code === 'response');
  await assert.rejects(api.updateProblem(1, data(), token()), (error) => error.code === 'response');
});

test('Problems network errors and timeouts do not expose provider details', async () => {
  const failing = createApiClient({ baseUrl: 'https://example.test', fetchImpl: async () => { throw new Error('Private details'); } });
  await assert.rejects(failing.listProblems(token()), (error) => error.code === 'network' && error.message === 'network');
  const api = createApiClient({ baseUrl: 'https://example.test', timeoutMs: 5, fetchImpl: (_, { signal }) =>
    new Promise((resolve, reject) => signal.addEventListener('abort', () => reject(new DOMException('', 'AbortError')))) });
  await assert.rejects(api.listProblems(token()), (error) => error.code === 'timeout');
});

test('a pending PUT is cancelled by the route AbortSignal', async () => {
  const controller = new AbortController();
  let fetchSignal;
  const api = createApiClient({ baseUrl: 'https://example.test', fetchImpl: (_, { signal }) => {
    fetchSignal = signal;
    return new Promise((resolve, reject) => signal.addEventListener('abort', () => reject(new DOMException('', 'AbortError'))));
  } });
  const request = api.updateProblem(1, data(), token(), { signal: controller.signal });
  controller.abort();
  await assert.rejects(request, (error) => error.name === 'AbortError');
  assert.equal(fetchSignal.aborted, true);
});

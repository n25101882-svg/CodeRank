import test from 'node:test';
import assert from 'node:assert/strict';
import { ApiError, createApiClient } from '../../src/api.js';

const json = (value, status = 200) => new Response(JSON.stringify(value), { status, headers: { 'content-type': 'application/json' } });

test('login follows the actual endpoint and DTO without cookies or redirecting credentials', async () => {
  const token = crypto.randomUUID();
  const password = crypto.randomUUID();
  const api = createApiClient({ baseUrl: 'https://localhost:7160/', fetchImpl: async (url, options) => {
    assert.equal(url, 'https://localhost:7160/api/v1/auth/login');
    assert.deepEqual(JSON.parse(options.body), { email: 'student@example.test', password });
    assert.equal(options.method, 'POST');
    assert.equal(options.headers['Content-Type'], 'application/json');
    assert.equal(options.headers.Authorization, undefined);
    assert.equal(options.credentials, 'omit');
    assert.equal(options.cache, 'no-store');
    assert.equal(options.redirect, 'error');
    return json({ accessToken: token, expiresAtUtc: new Date(Date.now() + 60_000).toISOString(), tokenType: 'Bearer' });
  } });
  assert.equal((await api.login({ email: 'student@example.test', password })).accessToken, token);
});

test('me puts the token only in the Authorization header and exposes known profile fields', async () => {
  const token = crypto.randomUUID();
  const api = createApiClient({ baseUrl: 'https://localhost:7160', fetchImpl: async (url, options) => {
    assert.equal(url, 'https://localhost:7160/api/v1/users/me');
    assert.ok(!url.includes(token));
    assert.equal(options.headers.Authorization, `Bearer ${token}`);
    assert.equal(options.headers['Content-Type'], undefined);
    return json({ id: 1, email: 'student@example.test', firstName: null, lastName: null, role: 'User', passwordHash: 'not-exposed' });
  } });
  assert.deepEqual(await api.me(token), { email: 'student@example.test', firstName: null, lastName: null, role: 'User' });
});

test('registration uses its actual endpoint and returns no invented token', async () => {
  const api = createApiClient({ baseUrl: 'https://localhost:7160', fetchImpl: async (url) => {
    assert.equal(url, 'https://localhost:7160/api/v1/auth/register');
    return json({ id: 1, email: 'student@example.test', firstName: 'Test', lastName: 'Student', role: 'User' }, 201);
  } });
  assert.equal((await api.register({})).accessToken, undefined);
});

for (const status of [400, 401, 409, 500]) {
  test(`HTTP ${status} becomes a safe structured error without raw detail`, async () => {
    const detail = crypto.randomUUID();
    const api = createApiClient({ baseUrl: 'https://localhost:7160', fetchImpl: async () =>
      json({ title: detail, detail, errors: { Email: [detail] } }, status) });
    await assert.rejects(api.me(crypto.randomUUID()), (error) => {
      assert.equal(error.status, status);
      assert.deepEqual(error.fields, ['Email']);
      assert.ok(!error.message.includes(detail));
      return error instanceof ApiError;
    });
  });
}

test('invalid login payloads cannot create a session', async () => {
  const api = createApiClient({ baseUrl: 'https://localhost:7160', fetchImpl: async () => json({ accessToken: 'bad\nheader' }) });
  await assert.rejects(api.login({}), (error) => error.code === 'response');
});

test('network failures are redacted', async () => {
  const api = createApiClient({ baseUrl: 'https://localhost:7160', fetchImpl: async () => { throw new Error(crypto.randomUUID()); } });
  await assert.rejects(api.me(crypto.randomUUID()), (error) => error.code === 'network' && error.message === 'network');
});

test('a request timeout ends the loading state with a timeout error', async () => {
  const api = createApiClient({ baseUrl: 'https://localhost:7160', timeoutMs: 5, fetchImpl: (_, { signal }) =>
    new Promise((resolve, reject) => signal.addEventListener('abort', () => reject(new DOMException('cancelled', 'AbortError')))) });
  await assert.rejects(api.me(crypto.randomUUID()), (error) => error.code === 'timeout');
});

test('route cancellation remains distinguishable from a network failure', async () => {
  const controller = new AbortController();
  controller.abort();
  const api = createApiClient({ baseUrl: 'https://localhost:7160', fetchImpl: () => { throw new Error('Must not fetch'); } });
  await assert.rejects(api.me(crypto.randomUUID(), { signal: controller.signal }), (error) => error.name === 'AbortError');
});

test('unsafe configuration URLs are rejected before requests', () => {
  for (const baseUrl of ['file:///tmp/private', 'https://user:pass@example.test', 'https://example.test?key=value']) {
    assert.throws(() => createApiClient({ baseUrl }));
  }
});

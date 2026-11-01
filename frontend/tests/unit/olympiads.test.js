import test from 'node:test';
import assert from 'node:assert/strict';
import { ApiError, createApiClient } from '../../src/api.js';
import { formatUtcDate } from '../../src/olympiads.js';

const contest = { id: 1, title: 'Test contest', description: null,
  startsAtUtc: '2026-11-10T10:00:00Z', endsAtUtc: '2026-11-10T12:00:00Z' };
const json = (value, status = 200) => new Response(JSON.stringify(value), { status, headers: { 'content-type': 'application/json' } });
const apiFor = (value) => createApiClient({ baseUrl: 'https://localhost:7160', fetchImpl: async () => json(value) });

test('olympiads uses the configured base URL and a public GET without credentials', async () => {
  const api = createApiClient({ baseUrl: 'https://example.test', fetchImpl: async (url, options) => {
    assert.equal(url, 'https://example.test/api/v1/olympiads');
    assert.equal(options.method, 'GET');
    assert.equal(options.headers.Authorization, undefined);
    assert.equal(options.headers['Content-Type'], undefined);
    assert.equal(options.body, undefined);
    assert.equal(options.credentials, 'omit');
    assert.equal(options.redirect, 'error');
    return json([contest]);
  } });
  assert.deepEqual(await api.olympiads({ token: crypto.randomUUID() }), [contest]);
});

test('olympiads preserves Backend order, nullable descriptions and only known DTO fields', async () => {
  const first = { ...contest, id: 20, title: '<img src=x>', description: '  Text  ' };
  assert.deepEqual(await apiFor([{ ...first, internalField: true }, contest]).olympiads(), [first, contest]);
});

test('an empty array is a valid response', async () => {
  assert.deepEqual(await apiFor([]).olympiads(), []);
});

for (const [name, value] of [
  ['object wrapper', { items: [contest] }], ['null', null], ['null item', [null]],
  ['string ID', [{ ...contest, id: '1' }]], ['fractional ID', [{ ...contest, id: 1.5 }]],
  ['empty title', [{ ...contest, title: '  ' }]], ['missing title', [{ ...contest, title: undefined }]],
  ['missing description', [{ ...contest, description: undefined }]],
  ['object description', [{ ...contest, description: {} }]],
  ['invalid date', [{ ...contest, startsAtUtc: 'not-a-date' }]],
  ['impossible date', [{ ...contest, startsAtUtc: '2026-02-30T10:00:00Z' }]],
  ['missing timezone', [{ ...contest, startsAtUtc: '2026-11-10T10:00:00' }]],
  ['non-UTC offset', [{ ...contest, startsAtUtc: '2026-11-10T10:00:00+02:00' }]],
  ['reversed dates', [{ ...contest, endsAtUtc: '2026-11-10T09:00:00Z' }]],
]) {
  test(`olympiads rejects ${name} as an invalid response`, async () => {
    await assert.rejects(apiFor(value).olympiads(), (error) => error instanceof ApiError && error.code === 'response');
  });
}

test('SQL datetime2 fractional seconds are accepted and UTC is displayed explicitly', async () => {
  const fractional = { ...contest, startsAtUtc: '2026-11-10T10:00:00.1234567Z' };
  assert.deepEqual(await apiFor([fractional]).olympiads(), [fractional]);
  assert.match(formatUtcDate(fractional.startsAtUtc), /10\.11\.2026,?\s+10:00 UTC$/);
  assert.match(formatUtcDate('2026-11-10T00:00:00Z'), /10\.11\.2026,?\s+00:00 UTC$/);
});

for (const status of [401, 404, 500]) {
  test(`olympiads HTTP ${status} exposes only a safe structured error`, async () => {
    const detail = crypto.randomUUID();
    const api = createApiClient({ baseUrl: 'https://localhost:7160', fetchImpl: async () => json({ detail }, status) });
    await assert.rejects(api.olympiads(), (error) => error.status === status && !error.message.includes(detail));
  });
}

for (const [name, response] of [
  ['non-JSON', () => new Response('<html>Private server details</html>')],
  ['malformed JSON', () => new Response('{invalid', { headers: { 'content-type': 'application/json' } })],
]) {
  test(`olympiads rejects a ${name} success response`, async () => {
    const api = createApiClient({ baseUrl: 'https://localhost:7160', fetchImpl: async () => response() });
    await assert.rejects(api.olympiads(), (error) => error.code === 'response');
  });
}

test('olympiads redacts network failures', async () => {
  const api = createApiClient({ baseUrl: 'https://localhost:7160', fetchImpl: async () => { throw new Error('Private details'); } });
  await assert.rejects(api.olympiads(), (error) => error.code === 'network' && error.message === 'network');
});

test('olympiads requests time out', async () => {
  const api = createApiClient({ baseUrl: 'https://localhost:7160', timeoutMs: 5, fetchImpl: (_, { signal }) =>
    new Promise((resolve, reject) => signal.addEventListener('abort', () => reject(new DOMException('', 'AbortError')))) });
  await assert.rejects(api.olympiads(), (error) => error.code === 'timeout');
});

test('route cancellation aborts an in-flight olympiads fetch', async () => {
  const controller = new AbortController();
  let fetchSignal;
  const api = createApiClient({ baseUrl: 'https://localhost:7160', fetchImpl: (_, { signal }) => {
    fetchSignal = signal;
    return new Promise((resolve, reject) => signal.addEventListener('abort', () => reject(new DOMException('', 'AbortError'))));
  } });
  const result = api.olympiads({ signal: controller.signal });
  controller.abort();
  await assert.rejects(result, (error) => error.name === 'AbortError');
  assert.equal(fetchSignal.aborted, true);
});

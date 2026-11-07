import test from 'node:test';
import assert from 'node:assert/strict';
import { createApiClient } from '../../src/api.js';
import { dateTimeInput, matchAdminOlympiadRoute, nextOlympiadPosition, validateOlympiadForm, validateOlympiadLinks } from '../../src/admin-olympiads-model.js';

const fields = { title: '  Contest  ', description: '', startsAtUtc: '2030-07-01T12:00', endsAtUtc: '2030-07-01T14:00' };
const dates = { startsAtUtc: '2030-07-01T12:00:00.000Z', endsAtUtc: '2030-07-01T14:00:00.000Z' };
const olympiad = { id: 31, title: 'Contest', description: null, ...dates, isOwn: true, isPublished: false };
const emptyLinks = { revision: 'A'.repeat(64), items: [] };
const link = { problemId: 19, title: 'Shared task', position: '2', maxPoints: '125', isVisible: true };
const baseUrl = 'https://example.test';

test('admin list and detail paths are separate from public and Problems routes', () => {
  assert.deepEqual(matchAdminOlympiadRoute('/admin/olympiads'), { id: undefined });
  assert.deepEqual(matchAdminOlympiadRoute('/admin/olympiads/31'), { id: 31 });
  for (const bad of ['0', '-1', '1e2', 'abc', '9007199254740992']) {
    assert.deepEqual(matchAdminOlympiadRoute(`/admin/olympiads/${bad}`), { id: null });
  }
  for (const other of ['/olympiads/31', '/problems', '/admin/olympiads/31/extra']) {
    assert.equal(matchAdminOlympiadRoute(other), null);
  }
});

test('metadata uses UTC and emits only editable fields', () => {
  assert.deepEqual(validateOlympiadForm({ ...fields, isPublished: true, createdByUserId: 99 }), {
    value: { title: 'Contest', description: null, ...dates },
  });
});

test('editing metadata preserves unchanged datetime2 precision', () => {
  const saved = { startsAtUtc: '2030-07-01T12:00:00.1234567Z', endsAtUtc: '2030-07-01T12:00:00.1234568Z' };
  assert.deepEqual(validateOlympiadForm({ ...fields,
    startsAtUtc: dateTimeInput(saved.startsAtUtc), endsAtUtc: dateTimeInput(saved.endsAtUtc),
  }, saved).value, { title: 'Contest', description: null, ...saved });
});

for (const patch of [
  { title: ' ' }, { title: 'x'.repeat(201) }, { endsAtUtc: '' }, { endsAtUtc: fields.startsAtUtc },
  { startsAtUtc: '2030-02-30T12:00' }, { endsAtUtc: '2030-07-01T11:59' },
]) {
  test(`rejects invalid metadata ${Object.keys(patch)[0]} ${JSON.stringify(Object.values(patch)[0]).slice(0, 27)}`, () => {
    assert.ok(validateOlympiadForm({ ...fields, ...patch }).error);
  });
}

test('settings use integer positions and scores and allow gaps or an empty set', () => {
  assert.deepEqual(validateOlympiadLinks([link]).value, [{ problemId: 19, position: 2, maxPoints: 125, isVisible: true }]);
  assert.deepEqual(validateOlympiadLinks([]).value, []);
  assert.equal(nextOlympiadPosition([{ position: '3' }, { position: '1' }]), 2);
});

test('link form rejects duplicate tasks, occupied positions, invalid integers and exposing legacy private tasks', () => {
  for (const row of [
    { ...link, position: 0 }, { ...link, maxPoints: '4.5' }, { ...link, maxPoints: 2_147_483_648 },
    { ...link, title: null }, { ...link, isVisible: 'true' },
  ]) assert.ok(validateOlympiadLinks([row]).error);
  assert.ok(validateOlympiadLinks([link, { ...link, position: 3 }]).error);
  assert.ok(validateOlympiadLinks([link, { ...link, problemId: 20 }]).error);
  assert.ok(validateOlympiadLinks([{ ...link, title: null, isVisible: false }]).value);
});

test('admin API sends authenticated GET, POST and PUT with the documented paths and statuses', async () => {
  const token = crypto.randomUUID();
  const calls = [
    ['GET', '/api/v1/admin/olympiads', undefined, 200, [olympiad]],
    ['GET', '/api/v1/admin/olympiads/31', undefined, 200, { olympiad, problems: emptyLinks }],
    ['GET', '/api/v1/admin/olympiads/problems', undefined, 200, [{ id: 19, title: 'Shared task' }]],
    ['POST', '/api/v1/admin/olympiads', dates, 201, olympiad],
    ['PUT', '/api/v1/admin/olympiads/31', dates, 204],
    ['PUT', '/api/v1/admin/olympiads/31/publication', { isPublished: true }, 204],
    ['PUT', '/api/v1/admin/olympiads/31/problems', emptyLinks, 200, emptyLinks],
  ];
  const api = createApiClient({ baseUrl, fetchImpl: async (url, request) => {
    const [method, path, body, status, response] = calls.shift();
    assert.equal(url, `${baseUrl}${path}`);
    assert.equal(request.method, method);
    assert.equal(request.headers.Authorization, `Bearer ${token}`);
    assert.equal(request.credentials, 'omit');
    assert.deepEqual(request.body === undefined ? undefined : JSON.parse(request.body), body);
    return status === 204 ? new Response(null, { status }) : Response.json(response, { status });
  } }).adminOlympiads;
  assert.deepEqual(await api.list(token), [olympiad]);
  assert.deepEqual(await api.get(31, token), { olympiad, problems: emptyLinks });
  assert.deepEqual(await api.catalog(token), [{ id: 19, title: 'Shared task' }]);
  assert.deepEqual(await api.create(dates, token), olympiad);
  await api.update(31, dates, token);
  await api.setPublication(31, true, token);
  assert.deepEqual(await api.replaceProblems(31, emptyLinks, token), emptyLinks);
  assert.equal(calls.length, 0);
});

test('admin client rejects missing credentials, malformed DTOs and returns only a safe conflict code', async () => {
  const never = createApiClient({ baseUrl, fetchImpl: async () => assert.fail('Unexpected fetch') }).adminOlympiads;
  await assert.rejects(never.list(), error => error.status === 401);
  await assert.rejects(never.get(0, 'test-token'), error => error.status === 404);
  const mock = (data, status = 200) => createApiClient({ baseUrl,
    fetchImpl: async () => Response.json(data, { status }) }).adminOlympiads;
  await assert.rejects(mock([{ ...olympiad, isPublished: 'true' }]).list('test-token'), error => error.code === 'response');
  await assert.rejects(mock({ olympiad, problems: { revision: 'invalid', items: [] } }).get(31, 'test-token'),
    error => error.code === 'response');
  await assert.rejects(mock({ title: 'Private SQL details', code: 'stale_links' }, 409)
    .replaceProblems(31, emptyLinks, 'test-token'),
  error => error.status === 409 && error.problemCode === 'stale_links' && error.message === 'http');
});

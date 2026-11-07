import test from 'node:test';
import assert from 'node:assert/strict';
import { createApiClient } from '../../src/api.js';
import { matchOlympiadRoute } from '../../src/olympiad-problems.js';

const baseUrl = 'https://example.test';
const olympiad = { id: 31, title: 'Contest', description: null,
  startsAtUtc: '2030-06-01T10:00:00Z', endsAtUtc: '2030-06-01T12:00:00Z' };
const first = { id: 20, title: 'Sum', difficulty: 'Medium', maxPoints: 125 };
const second = { id: 5, title: 'Arrays', difficulty: null, maxPoints: 50 };
const detail = { ...first, statement: 'Add two integers.\nPrint the sum.', inputFormat: null,
  outputFormat: 'One integer.', examples: [
    { inputData: '1 2\n', expectedOutput: '3\n', explanation: 'One plus two.' },
    { inputData: '', expectedOutput: '', explanation: null },
  ] };
const apiFor = (data, status = 200) => createApiClient({ baseUrl, fetchImpl: async () => Response.json(data, { status }) });

test('the nested SPA paths resolve positive decimal IDs without matching other routes', () => {
  assert.deepEqual(matchOlympiadRoute('/olympiads/31'), { olympiadId: 31, problemId: undefined });
  assert.deepEqual(matchOlympiadRoute('/olympiads/31/problems/20'), { olympiadId: 31, problemId: 20 });
  for (const path of ['/olympiads', '/problems/20', '/olympiads/31/problems', '/olympiads/31/problems/20/extra']) {
    assert.equal(matchOlympiadRoute(path), null);
  }
});

for (const id of ['0', '-5', '31.0', '3e1', '%2F', 'abc', '9007199254740992']) {
  test(`invalid route ID ${id} can show an unavailable page`, () => {
    assert.equal(matchOlympiadRoute(`/olympiads/${id}`).olympiadId, null);
    assert.equal(matchOlympiadRoute(`/olympiads/31/problems/${id}`).problemId, null);
  });
}

test('requests the public endpoints with GET and no session credentials', async () => {
  const responses = new Map([
    ['/api/v1/olympiads/31', olympiad],
    ['/api/v1/olympiads/31/problems', [first, second]],
    ['/api/v1/olympiads/31/problems/20', detail],
  ]);
  const api = createApiClient({ baseUrl, fetchImpl: async (url, options) => {
    const path = new URL(url).pathname;
    assert.ok(responses.has(path), url);
    assert.equal(options.method, 'GET');
    assert.equal(options.headers.Authorization, undefined);
    assert.equal(options.credentials, 'omit');
    const result = responses.get(path);
    responses.delete(path);
    return Response.json(result);
  } });
  const options = { token: crypto.randomUUID() };
  assert.deepEqual(await api.getOlympiad(31, options), olympiad);
  assert.deepEqual(await api.listOlympiadProblems(31, options), [first, second]);
  assert.deepEqual(await api.getOlympiadProblem(31, 20, options), detail);
  assert.equal(responses.size, 0);
});

test('preserves server order and strips fields outside the public DTOs', async () => {
  assert.deepEqual(await apiFor([{ ...first, createdByUserId: 10, statement: 'unexpected' }, second])
    .listOlympiadProblems(31), [first, second]);
  assert.deepEqual(await apiFor({ ...detail, isVisible: true, examples: [
    { ...detail.examples[0], id: 15 }, detail.examples[1],
  ] }).getOlympiadProblem(31, 20), detail);
});

test('accepts empty lists and problems without formats, difficulty or examples', async () => {
  assert.deepEqual(await apiFor([]).listOlympiadProblems(31), []);
  const sparse = { ...second, statement: 'Read an array.', inputFormat: null, outputFormat: null, examples: [] };
  assert.deepEqual(await apiFor(sparse).getOlympiadProblem(31, 5), sparse);
});

test('bad IDs are rejected before a fetch and unavailable resources propagate 404', async () => {
  const invalid = createApiClient({ baseUrl, fetchImpl: async () => assert.fail('Unexpected fetch') });
  const notFound = (error) => error.code === 'http' && error.status === 404;
  await assert.rejects(invalid.listOlympiadProblems(-1), notFound);
  await assert.rejects(invalid.getOlympiadProblem(31, '20'), notFound);
  await assert.rejects(invalid.getOlympiadProblem(0, 20), notFound);
  await assert.rejects(apiFor({ detail: 'private' }, 404).listOlympiadProblems(31), notFound);
  await assert.rejects(apiFor({ detail: 'private' }, 404).getOlympiadProblem(31, 20), notFound);
});

test('malformed list and detail responses are rejected before rendering', async () => {
  const invalidResponse = (error) => error.code === 'response';
  for (const data of [{ problems: [] }, [first, null], [{ ...first, maxPoints: 0 }], [{ ...first, title: '' }]]) {
    await assert.rejects(apiFor(data).listOlympiadProblems(31), invalidResponse);
  }
  for (const data of [
    { ...detail, id: second.id }, { ...detail, statement: null }, { ...detail, inputFormat: [] },
    { ...detail, examples: null }, { ...detail, examples: [{ inputData: 5 }] },
  ]) {
    await assert.rejects(apiFor(data).getOlympiadProblem(31, first.id), invalidResponse);
  }
  await assert.rejects(apiFor({ ...olympiad, id: 5 }).getOlympiad(31), invalidResponse);
});

test('network failures surface only the client error code', async () => {
  const api = createApiClient({ baseUrl, fetchImpl: async () => { throw new Error('Private connection details'); } });
  await assert.rejects(api.listOlympiadProblems(31), (error) => error.code === 'network' && error.message === 'network');
});

test('leaving a problem page can cancel the public detail fetch', async () => {
  const controller = new AbortController();
  const api = createApiClient({ baseUrl, fetchImpl: (_, { signal }) =>
    new Promise((_, reject) => signal.addEventListener('abort', () => reject(new DOMException('', 'AbortError')))) });
  const result = api.getOlympiadProblem(31, first.id, { signal: controller.signal });
  controller.abort();
  await assert.rejects(result, (error) => error.name === 'AbortError');
});

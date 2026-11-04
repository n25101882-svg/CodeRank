import test from 'node:test';
import assert from 'node:assert/strict';
import { randomUUID } from 'node:crypto';
import { createApiClient } from '../../src/api.js';

test('one client keeps Olympiads public alongside authenticated Problems CRUD and 204 responses', async () => {
  const token = randomUUID();
  const contest = { id: 1, title: 'Олімпіада', description: null,
    startsAtUtc: '2026-11-10T10:00:00Z', endsAtUtc: '2026-11-10T12:00:00Z' };
  const data = { title: 'Задача', statement: 'Умова', inputFormat: null, outputFormat: null, difficulty: 'Easy' };
  const problem = { id: 7, ...data, createdAtUtc: '2026-11-01T10:00:00Z' };
  const requests = [];
  const client = createApiClient({ baseUrl: 'https://localhost:7160', fetchImpl: async (url, options) => {
    requests.push({ path: new URL(url).pathname, ...options });
    if (url.endsWith('/olympiads')) return Response.json([contest]);
    if (options.method === 'PUT' || options.method === 'DELETE') return new Response(null, { status: 204 });
    return Response.json(options.method === 'POST' ? problem : [problem], { status: options.method === 'POST' ? 201 : 200 });
  } });

  assert.deepEqual(await client.olympiads({ token }), [contest]);
  assert.deepEqual(await client.listProblems(token), [problem]);
  assert.deepEqual(await client.createProblem(data, token), problem);
  assert.equal(await client.updateProblem(problem.id, data, token), undefined);
  assert.equal(await client.deleteProblem(problem.id, token), undefined);
  assert.deepEqual(await client.olympiads({ token }), [contest]);

  assert.deepEqual(requests.map(({ method }) => method), ['GET', 'GET', 'POST', 'PUT', 'DELETE', 'GET']);
  for (const request of requests) {
    assert.equal(request.headers.Authorization, request.path.endsWith('/olympiads') ? undefined : `Bearer ${token}`);
    assert.equal(request.credentials, 'omit');
  }
});

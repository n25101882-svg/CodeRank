import test from 'node:test';
import assert from 'node:assert/strict';
import { createApiClient } from '../../src/api.js';
import { MAX_SOURCE_BYTES, readSubmissionFile, sourceSize, validateSubmission } from '../../src/submissions-model.js';

const baseUrl = 'https://example.test';
const receipt = { id: 11, status: 'Queued', submittedAtUtc: '2030-06-01T10:00:00.1234567Z' };
const filters = { olympiadId: 31, problemId: 20, page: 2 };
const body = { language: 'python3', sourceCode: '  # Привіт\r\nprint("🚀<&>")\n' };
const session = 'test-session';

test('validates UTF-8 boundaries without changing code, language or whitespace', () => {
  assert.deepEqual(validateSubmission(body), { body, errors: {} });
  const exact = 'ї'.repeat(MAX_SOURCE_BYTES / 2);
  assert.equal(sourceSize(exact), MAX_SOURCE_BYTES);
  assert.deepEqual(validateSubmission({ language: 'cpp17', sourceCode: exact }).errors, {});
  assert.ok(validateSubmission({ language: 'cpp17', sourceCode: exact + 'a' }).errors.sourceCode);
});

test('rejects blank code and noncanonical languages', () => {
  for (const sourceCode of ['', '  \n\t\u0085', null]) {
    assert.ok(validateSubmission({ language: 'cpp17', sourceCode }).errors.sourceCode);
  }
  for (const language of ['javascript', 'CPP17', 'constructor', ['cpp17']]) {
    assert.ok(validateSubmission({ language, sourceCode: 'print(1)' }).errors.language);
  }
});

test('decodes allowed local files as UTF-8 and preserves text before textarea normalization', async () => {
  for (const name of ['answer.CPP', 'answer.py', 'answer.txt']) {
    assert.equal(await readSubmissionFile(new File([body.sourceCode], name)), body.sourceCode);
  }
});

test('rejects unsupported, oversized or invalid UTF-8 files', async () => {
  await assert.rejects(readSubmissionFile(new File(['print(1)'], 'answer.exe')), /текстовий файл/);
  await assert.rejects(readSubmissionFile(new File(['x'.repeat(MAX_SOURCE_BYTES + 1)], 'answer.py')), /64 КіБ/);
  await assert.rejects(readSubmissionFile(new File([new Uint8Array([0xff])], 'answer.txt')), /UTF-8/);
});

test('authenticated create posts only language and source and validates the 201 receipt', async () => {
  const api = createApiClient({ baseUrl, fetchImpl: async (url, options) => {
    assert.equal(url, baseUrl + '/api/v1/olympiads/31/problems/20/submissions');
    assert.equal(options.method, 'POST');
    assert.equal(options.headers.Authorization, 'Bearer ' + session);
    assert.deepEqual(JSON.parse(options.body), body);
    return Response.json({ ...receipt, userId: 19 }, { status: 201 });
  } });
  assert.deepEqual(await api.createSubmission(31, 20, { ...body, status: 'Accepted', userId: 2 }, session), receipt);
});

test('invalid, oversized and unauthenticated writes issue no fetch', async () => {
  const api = createApiClient({ baseUrl, fetchImpl: async () => assert.fail('Unexpected request') });
  await assert.rejects(api.createSubmission(31, 20, body, null), { status: 401 });
  await assert.rejects(api.createSubmission(31, 0, body, session), { status: 404 });
  await assert.rejects(api.createSubmission(31, 20, { ...body, sourceCode: 'x'.repeat(MAX_SOURCE_BYTES + 1) }, session),
    { code: 'validation', fields: ['sourceCode'] });
});

test('history GET is private and uses both route IDs and requested page', async () => {
  const api = createApiClient({ baseUrl, fetchImpl: async (url, options) => {
    const request = new URL(url);
    assert.equal(request.pathname, '/api/v1/submissions');
    assert.deepEqual(Object.fromEntries(request.searchParams), { olympiadId: '31', problemId: '20', page: '2' });
    assert.equal(options.headers.Authorization, 'Bearer ' + session);
    assert.equal(options.cache, 'no-store');
    assert.equal(options.method, 'GET');
    return Response.json({ items: [{ ...receipt, olympiadId: 31, problemId: 20, language: 'cpp17', sourceCode: 'extra' }],
      page: 2, pageSize: 20, hasMore: true });
  } });
  assert.deepEqual(await api.listSubmissions(filters, session),
    { items: [{ ...receipt, language: 'cpp17' }], page: 2, hasMore: true });
});

test('rejects a receipt without 201 or Queued and rejects mismatched history entries', async () => {
  const apiFor = (payload, status) => createApiClient({ baseUrl, fetchImpl: async () => Response.json(payload, { status }) });
  await assert.rejects(apiFor(receipt, 200).createSubmission(31, 20, body, session), { code: 'response' });
  await assert.rejects(apiFor({ ...receipt, status: 'Accepted' }, 201).createSubmission(31, 20, body, session), { code: 'response' });
  await assert.rejects(apiFor({ items: [{ ...receipt, olympiadId: 31, problemId: 5, language: 'cpp17' }],
    page: 2, pageSize: 20, hasMore: false }, 200).listSubmissions(filters, session), { code: 'response' });
});

test('accepts queue progress and rejects unknown statuses in submission history', async () => {
  const getHistory = statuses => createApiClient({ baseUrl, fetchImpl: async () => Response.json({
    items: statuses.map(status => ({ ...receipt, olympiadId: 31, problemId: 20, language: 'cpp17', status })),
    page: 2, pageSize: 20, hasMore: false,
  }) }).listSubmissions(filters, session);

  const result = await getHistory(['Running', 'SystemError']);
  assert.deepEqual(result.items.map(item => item.status), ['Running', 'SystemError']);
  await assert.rejects(getHistory(['not-a-verdict']), { code: 'response' });
});

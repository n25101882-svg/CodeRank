import test from 'node:test';
import assert from 'node:assert/strict';
import { createDraftStore, DRAFT_KEYS } from '../../src/drafts.js';

function fixture() {
  const data = new Map();
  const store = createDraftStore(() => ({
    getItem: (key) => data.get(key) ?? null,
    setItem: (key, value) => data.set(key, value),
    removeItem: (key) => data.delete(key),
  }));
  return { store, data };
}

test('login draft stores only email and never a password or token', () => {
  const { store, data } = fixture();
  store.save('login', { email: ' draft@example.test ', password: crypto.randomUUID(), accessToken: crypto.randomUUID(), firstName: 'Ignored' });
  assert.deepEqual(JSON.parse(data.get(DRAFT_KEYS.login)), { email: ' draft@example.test ' });
});

test('register draft round-trips only names and email, preserving typed whitespace', () => {
  const { store } = fixture();
  const fields = { firstName: ' Мар’яна ', lastName: 'Анна-Марія', email: 'draft@example.test' };
  store.save('register', { ...fields, password: crypto.randomUUID(), role: 'Admin' });
  assert.deepEqual(store.read('register'), fields);
});

test('clearing one draft preserves the other and unrelated session data', () => {
  const { store, data } = fixture();
  data.set('coderank.accessToken', crypto.randomUUID());
  store.save('login', { email: 'login@example.test' });
  store.save('register', { firstName: 'Тест' });
  store.clear('register');
  assert.deepEqual(store.read('register'), {});
  assert.deepEqual(store.read('login'), { email: 'login@example.test' });
  store.clearAll();
  assert.deepEqual([...data.keys()], ['coderank.accessToken']);
});

test('empty fields remove obsolete drafts', () => {
  const { store, data } = fixture();
  store.save('login', { email: 'draft@example.test' });
  store.save('login', { email: '', password: crypto.randomUUID() });
  assert.equal(data.size, 0);
});

test('corrupted JSON and non-object drafts safely recover', () => {
  const { store, data } = fixture();
  for (const value of ['not-json', 'null', '[]', '"text"']) {
    data.set(DRAFT_KEYS.register, value);
    assert.deepEqual(store.read('register'), {});
    assert.equal(data.has(DRAFT_KEYS.register), false);
  }
});

test('cached unknown fields and invalid field types are removed on read', () => {
  const { store, data } = fixture();
  data.set(DRAFT_KEYS.login, JSON.stringify({ email: 'draft@example.test', password: null, role: 'Admin' }));
  assert.deepEqual(store.read('login'), { email: 'draft@example.test' });
  assert.deepEqual(Object.keys(JSON.parse(data.get(DRAFT_KEYS.login))), ['email']);
  store.save('register', { email: 123, firstName: 'А'.repeat(101), lastName: false });
  assert.deepEqual(store.read('register'), {});
});

test('blocked storage is a non-fatal optional-draft failure', () => {
  const store = createDraftStore(() => { throw new Error('Blocked'); });
  assert.deepEqual(store.read('register'), {});
  assert.equal(store.save('login', { email: 'draft@example.test' }), false);
  assert.doesNotThrow(() => store.clearAll());
});

test('unknown modes cannot read or overwrite unrelated storage keys', () => {
  const { store, data } = fixture();
  data.set('unrelated', 'value');
  assert.equal(store.save('__proto__', { email: 'draft@example.test' }), false);
  assert.deepEqual(store.read('unknown'), {});
  store.clear('unrelated');
  assert.equal(data.get('unrelated'), 'value');
});

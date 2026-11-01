import test from 'node:test';
import assert from 'node:assert/strict';
import { createSessionStore, SessionStorageError, TOKEN_KEY } from '../../src/session.js';

function fixture() {
  const data = new Map();
  const store = createSessionStore(() => ({
    getItem: (key) => data.get(key) ?? null,
    setItem: (key, value) => data.set(key, value),
    removeItem: (key) => data.delete(key),
  }));
  return { store, data };
}

test('session persists only the access token under its dedicated key', () => {
  const { store, data } = fixture();
  assert.equal(store.read(), null);
  const token = crypto.randomUUID();
  store.save(token);
  assert.equal(store.read(), token);
  assert.deepEqual([...data.keys()], [TOKEN_KEY]);
  store.clear();
  assert.equal(store.read(), null);
  assert.equal(data.size, 0);
  store.save(token);
  assert.equal(store.read(), token);
});

test('a blocked storage accessor safely denies access and refuses login persistence', () => {
  const store = createSessionStore(() => { throw new Error('Blocked'); });
  assert.equal(store.read(), null);
  assert.throws(() => store.save(crypto.randomUUID()), SessionStorageError);
  assert.doesNotThrow(() => store.clear());
});

test('invalid stored header content is removed rather than sent as a token', () => {
  const { store, data } = fixture();
  data.set(TOKEN_KEY, 'invalid\r\nheader');
  assert.equal(store.read(), null);
  assert.equal(data.size, 0);
  assert.throws(() => store.save('bad token'), SessionStorageError);
});

test('a failed storage write is never treated as a successful login', () => {
  const store = createSessionStore(() => ({ setItem() { throw new Error('Quota'); } }));
  assert.throws(() => store.save(crypto.randomUUID()), SessionStorageError);
});

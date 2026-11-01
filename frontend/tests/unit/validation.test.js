import test from 'node:test';
import assert from 'node:assert/strict';
import { validateLogin, validateRegistration, errorMessage } from '../../src/validation.js';

const valid = () => ({ email: '  student@example.test ', password: crypto.randomUUID(), firstName: ' Тест ', lastName: ' Учасник ' });

test('registration trims names/email, preserves password, and excludes unexpected fields', async () => {
  const input = { ...valid(), password: ` ${crypto.randomUUID()} `, role: 'Admin' };
  const result = await validateRegistration(input);
  assert.deepEqual(result.errors, {});
  assert.deepEqual(Object.keys(result.values).sort(), ['email', 'firstName', 'lastName', 'password']);
  assert.equal(result.values.email, 'student@example.test');
  assert.equal(result.values.firstName, 'Тест');
  assert.equal(result.values.lastName, 'Учасник');
  assert.ok(result.values.password === input.password, 'Password must remain unchanged');
});

for (const [name, field, value] of [
  ['missing email', 'email', ' '], ['invalid email', 'email', 'not-an-email'],
  ['long email', 'email', `${'x'.repeat(250)}@example.test`],
  ['missing password', 'password', ' '.repeat(12)], ['short password', 'password', 'x'.repeat(11)],
  ['long password', 'password', 'x'.repeat(129)], ['empty name', 'firstName', ' '],
  ['missing last name', 'lastName', undefined], ['long name', 'firstName', 'x'.repeat(101)],
]) {
  test(`registration rejects ${name}`, async () => {
    assert.ok((await validateRegistration({ ...valid(), [field]: value })).errors[field]);
  });
}

test('registration accepts the exact password length boundaries', async () => {
  for (const length of [12, 128]) {
    const password = [...crypto.getRandomValues(new Uint8Array(length))].map((byte) => String.fromCharCode(97 + byte % 26)).join('');
    assert.deepEqual((await validateRegistration({ ...valid(), password })).errors, {});
  }
});

test('login accepts existing passwords shorter than registration minimum, matching Backend', () => {
  assert.deepEqual(validateLogin({ email: 'student@example.test', password: 'x' }).errors, {});
  assert.ok(validateLogin({ email: 'student@example.test', password: ' ' }).errors.password);
  assert.ok(validateLogin({ email: 'student@example.test', password: 'x'.repeat(129) }).errors.password);
});

test('safe messages do not echo provider or server details', () => {
  const detail = crypto.randomUUID();
  for (const status of [400, 401, 409, 429, 500]) {
    assert.ok(!errorMessage({ status, message: detail }, 'login').includes(detail));
  }
  assert.match(errorMessage({ status: 401 }, 'login'), /Неправильний email або пароль/);
  assert.match(errorMessage({ status: 409 }, 'register'), /уже зареєстровано/);
});

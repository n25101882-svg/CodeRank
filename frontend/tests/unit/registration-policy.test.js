import test from 'node:test';
import assert from 'node:assert/strict';
import { validateRegistration, validateLogin } from '../../src/validation.js';
import { isAcceptableRegistrationPassword } from '../../src/password-policy.js';

const data = () => ({ email: 'validation@example.test', firstName: 'Тест', lastName: 'Учасник', password: crypto.randomUUID() });
const sequence = (zero = 48, step = 1) => Array.from({ length: 16 }, (_, i) => String.fromCodePoint(zero + i * step % 10)).join('');
const count = (first, terms, step = 1) => Array.from({ length: terms }, (_, i) => first + i * step).join('');

for (const [field, value] of [
  ['firstName', '22'], ['lastName', '22'], ['firstName', '— ’ ʼ -'], ['lastName', '٢٢'],
]) {
  test(`registration rejects a name without a letter: ${field}, ${value}`, async () => {
    assert.ok((await validateRegistration({ ...data(), [field]: value })).errors[field]);
  });
}

for (const [firstName, lastName] of [
  ['Мар’яна', 'Анна-Марія'], ["Лук'ян", 'Де Ла Крус'], ['Лукʼян', '李'],
  ['أحمد', 'عبد الرحمن'], ['A\u0301на', 'नमस्ते'], ['\u{10400}', 'Unicode'],
]) {
  test(`Unicode name support: ${firstName}, ${lastName}`, async () => {
    assert.deepEqual((await validateRegistration({ ...data(), firstName, lastName })).errors, {});
  });
}

const weakCases = {
  sequence: () => sequence(),
  reverseSequence: () => [...sequence()].reverse().join(''),
  stepSequence: () => sequence(48, 2),
  countThroughTen: () => count(1, 12),
  countThroughHundred: () => count(97, 7),
  countDownThroughTen: () => count(12, 12, -1),
  paddedCount: () => Array.from({ length: 6 }, (_, i) => String(i + 1).padStart(2, '0')).join(''),
  arabicIndicCount: () => [...count(1, 12)].map((digit) => String.fromCodePoint(0x0660 + Number(digit))).join(''),
  repeatCharacter: () => 'x'.repeat(16),
  repeatMotif: () => 'ab'.repeat(8),
  repeatLongMotif: () => Object.keys({ password: null })[0].repeat(2),
  common: () => Object.keys({ password: null })[0] + Array.from({ length: 5 }, (_, i) => i + 1).join(''),
  commonCaseAndPadding: () => ` ${weakCases.common().toUpperCase()} `,
  fullWidthSequence: () => sequence(0xff10),
  arabicIndicSequence: () => sequence(0x0660),
  supplementarySequence: () => sequence(0x1d7ce),
};
for (const [name, generate] of Object.entries(weakCases)) {
  test(`registration rejects predictable password category: ${name}`, async () => {
    const password = generate();
    assert.equal(await isAcceptableRegistrationPassword(password), false);
    assert.ok((await validateRegistration({ ...data(), password })).errors.password);
    assert.deepEqual(validateLogin({ email: data().email, password }).errors, {});
  });
}

test('nonsequential numeric passwords are permitted', async () => {
  const password = [...crypto.getRandomValues(new Uint8Array(24))].map((byte) => byte % 10).join('');
  assert.deepEqual((await validateRegistration({ ...data(), password })).errors, {});
});

test('counting-like prefix with a nonconsecutive tail is permitted', async () => {
  const password = count(1, 11) + '42';
  assert.deepEqual((await validateRegistration({ ...data(), password })).errors, {});
});

test('password length remains 12 to 128 UTF-16 units', async () => {
  for (const length of [11, 12, 128, 129]) {
    const password = [...crypto.getRandomValues(new Uint8Array(length))]
      .map((byte) => String.fromCharCode(97 + byte % 26)).join('');
    assert.equal(Boolean((await validateRegistration({ ...data(), password })).errors.password),
      length < 12 || length > 128);
  }
});

test('comparison normalization never changes the submitted password', async () => {
  const password = `  ${crypto.randomUUID()}Ａ\u0301  `;
  const result = await validateRegistration({ ...data(), password });
  assert.deepEqual(result.errors, {});
  assert.ok(result.values.password === password, 'Original password must be submitted unchanged');
});

test('name length still counts UTF-16 units consistently with Backend', async () => {
  assert.deepEqual((await validateRegistration({ ...data(), firstName: 'А'.repeat(100) })).errors, {});
  assert.ok((await validateRegistration({ ...data(), firstName: 'А'.repeat(101) })).errors.firstName);
  assert.deepEqual((await validateRegistration({ ...data(), firstName: '\u{10400}'.repeat(50) })).errors, {});
  assert.ok((await validateRegistration({ ...data(), firstName: '\u{10400}'.repeat(51) })).errors.firstName);
});

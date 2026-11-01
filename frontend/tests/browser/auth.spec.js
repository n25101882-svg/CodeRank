import { test, expect } from '@playwright/test';
import { randomUUID } from 'node:crypto';

const apiRoot = 'https://localhost:7160';
const tokenKey = 'coderank.accessToken';
const draftKeys = { login: 'coderank.draft.login', register: 'coderank.draft.register' };
const user = { id: 1, email: 'student@example.test', firstName: 'Тестовий', lastName: 'Учасник', role: 'User' };
const cors = { 'access-control-allow-origin': `http://localhost:${process.env.CODERANK_TEST_PORT || 5173}`, 'access-control-allow-methods': 'GET, POST', 'access-control-allow-headers': 'content-type, authorization' };
const loginResult = (token) => ({ accessToken: token, tokenType: 'Bearer', expiresAtUtc: new Date(Date.now() + 300_000).toISOString() });
const respond = (route, json, status = 200) => route.fulfill({ status, json, headers: cors });

async function mock(page, path, handler) {
  await page.route(`${apiRoot}/api/v1/${path}`, async (route) => {
    if (route.request().method() === 'OPTIONS') return route.fulfill({ status: 204, headers: cors });
    return handler(route);
  });
}
async function seedSession(page, token = randomUUID()) {
  await page.goto('/login');
  await page.evaluate(({ key, value }) => sessionStorage.setItem(key, value), { key: tokenKey, value: token });
  return token;
}
async function fillLogin(page) {
  await page.getByLabel('Email', { exact: true }).fill(user.email);
  await page.getByLabel('Пароль', { exact: true }).fill(randomUUID());
}
async function fillRegistration(page) {
  await page.getByLabel('Ім’я', { exact: true }).fill(` ${user.firstName} `);
  await page.getByLabel('Прізвище', { exact: true }).fill(` ${user.lastName} `);
  await fillLogin(page);
}

test.beforeEach(async ({ page }) => {
  // Catch every API call, including unexpected ones: no browser test can reach a real database.
  await page.route(`${apiRoot}/**`, (route) => route.request().method() === 'OPTIONS'
    ? route.fulfill({ status: 204, headers: cors })
    : respond(route, { title: 'Unconfigured test endpoint' }, 503));
});

for (const path of ['/login', '/register']) {
  test(`${path} opens directly, survives reload and fits the viewport`, async ({ page }, info) => {
    await page.goto(path);
    await expect(page.getByLabel('Email', { exact: true })).toBeVisible();
    await page.reload();
    await expect(page).toHaveURL(path);
    await expect(page.getByLabel('Пароль', { exact: true })).toBeVisible();
    await expect(page.locator('html')).toHaveAttribute('lang', 'uk');
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
    await page.screenshot({ path: info.outputPath(`${path.slice(1)}.png`), fullPage: true });
  });
}

test('dashboard without a token redirects before requesting or rendering profile data', async ({ page }) => {
  let calls = 0;
  await mock(page, 'users/me', (route) => { calls++; return respond(route, user); });
  await page.goto('/dashboard');
  await expect(page).toHaveURL('/login');
  await expect(page.locator('#profile-content')).toHaveCount(0);
  expect(calls).toBe(0);
});

test('registration validates required fields and the 12-character minimum before sending', async ({ page }) => {
  let calls = 0;
  await mock(page, 'auth/register', (route) => { calls++; return respond(route, user, 201); });
  await page.goto('/register');
  await page.getByRole('button', { name: 'Створити акаунт', exact: true }).click();
  await expect(page.getByLabel('Ім’я', { exact: true })).toBeFocused();
  await expect(page.getByLabel('Email', { exact: true })).toHaveAttribute('aria-invalid', 'true');
  await fillRegistration(page);
  await page.getByLabel('Пароль', { exact: true }).fill('x'.repeat(11));
  await page.getByRole('button', { name: 'Створити акаунт', exact: true }).click();
  await expect(page.locator('#password-error')).toContainText('щонайменше 12');
  expect(calls).toBe(0);
});

test('successful registration sends only the real DTO and returns to login without a session', async ({ page }) => {
  let request;
  await mock(page, 'auth/register', (route) => { request = route.request().postDataJSON(); return respond(route, user, 201); });
  await page.goto('/register');
  await fillRegistration(page);
  await page.getByRole('button', { name: 'Створити акаунт', exact: true }).click();
  await expect(page).toHaveURL('/login');
  await expect(page.getByRole('status')).toContainText('Акаунт створено');
  expect(Object.keys(request).sort()).toEqual(['email', 'firstName', 'lastName', 'password']);
  expect(request.firstName).toBe(user.firstName);
  expect(request.lastName).toBe(user.lastName);
  expect(await page.evaluate((key) => sessionStorage.getItem(key), tokenKey)).toBeNull();
});

test('duplicate email displays a safe error and restores the registration form', async ({ page }) => {
  await mock(page, 'auth/register', (route) => respond(route, { title: 'Private SQL provider details' }, 409));
  await page.goto('/register');
  await fillRegistration(page);
  await page.getByRole('button', { name: 'Створити акаунт', exact: true }).click();
  await expect(page.getByRole('alert')).toContainText('Цей email уже зареєстровано');
  await expect(page.getByText('Private SQL provider details')).toHaveCount(0);
  await expect(page.getByRole('button', { name: 'Створити акаунт', exact: true })).toBeEnabled();
});

test('wrong credentials return a generic error without creating a session', async ({ page }) => {
  await mock(page, 'auth/login', (route) => respond(route, { title: 'Invalid credentials.' }, 401));
  await page.goto('/login');
  await fillLogin(page);
  await page.getByRole('button', { name: 'Увійти', exact: true }).click();
  await expect(page.getByRole('alert')).toContainText('Неправильний email або пароль');
  await expect(page.getByRole('button', { name: 'Увійти', exact: true })).toBeEnabled();
  expect(await page.evaluate((key) => sessionStorage.getItem(key), tokenKey)).toBeNull();
});

test('login saves only a session token and waits for verified profile data', async ({ page }) => {
  const token = randomUUID();
  let finish;
  let authorization;
  const gate = new Promise((resolve) => { finish = resolve; });
  await mock(page, 'auth/login', (route) => respond(route, loginResult(token)));
  await mock(page, 'users/me', async (route) => {
    authorization = route.request().headers().authorization;
    expect(route.request().url()).not.toContain(token);
    await gate;
    return respond(route, user);
  });
  await page.goto('/login');
  await fillLogin(page);
  await page.getByRole('button', { name: 'Увійти', exact: true }).click();
  try {
    await expect(page).toHaveURL('/dashboard');
    await expect(page.locator('#profile-loading')).toBeVisible();
    await expect(page.locator('#profile-content')).toBeHidden();
    await expect(page.locator('#profile-email')).toHaveText('');
    expect(await page.evaluate((key) => sessionStorage.getItem(key), tokenKey)).toBe(token);
    expect(await page.evaluate(() => localStorage.length)).toBe(0);
  } finally { finish(); }
  await expect(page.locator('#profile-email')).toHaveText(user.email);
  await expect(page.locator('#profile-firstName')).toHaveText(user.firstName);
  await expect(page.locator('#profile-lastName')).toHaveText(user.lastName);
  await expect(page.locator('#profile-role')).toHaveText(user.role);
  expect(authorization).toBe(`Bearer ${token}`);
});

test('a direct authenticated dashboard survives reload and fits the viewport', async ({ page }, info) => {
  let calls = 0;
  await mock(page, 'users/me', (route) => { calls++; return respond(route, user); });
  await seedSession(page);
  await page.goto('/dashboard');
  await expect(page.locator('#profile-email')).toHaveText(user.email);
  await page.reload();
  await expect(page.locator('#profile-email')).toHaveText(user.email);
  expect(calls).toBe(2);
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
  await page.screenshot({ path: info.outputPath('dashboard.png'), fullPage: true });
});

test('a 401 from me clears the token and redirects without exposing profile data', async ({ page }) => {
  await mock(page, 'users/me', (route) => respond(route, {}, 401));
  await seedSession(page);
  await page.goto('/dashboard');
  await expect(page).toHaveURL('/login');
  await expect(page.getByRole('alert')).toContainText('Сесія завершилася');
  expect(await page.evaluate((key) => sessionStorage.getItem(key), tokenKey)).toBeNull();
  await expect(page.locator('#profile-content')).toHaveCount(0);
});

test('logout clears the client session and the dashboard stays protected on revisit', async ({ page }) => {
  let calls = 0;
  await mock(page, 'users/me', (route) => { calls++; return respond(route, user); });
  await seedSession(page);
  await page.goto('/dashboard');
  await expect(page.locator('#profile-email')).toHaveText(user.email);
  await page.getByRole('button', { name: 'Вийти', exact: true }).click();
  await expect(page).toHaveURL('/login');
  expect(await page.evaluate((key) => sessionStorage.getItem(key), tokenKey)).toBeNull();
  await page.goto('/dashboard');
  await expect(page).toHaveURL('/login');
  await expect(page.getByText(user.email, { exact: true })).toHaveCount(0);
  expect(calls).toBe(1);
});

test('a network failure reveals no profile and supports retry', async ({ page }) => {
  await mock(page, 'users/me', (route) => route.abort('failed'));
  const token = await seedSession(page);
  await page.goto('/dashboard');
  await expect(page.getByRole('alert')).toContainText('Не вдалося під’єднатися');
  await expect(page.locator('#profile-content')).toBeHidden();
  expect(await page.evaluate((key) => sessionStorage.getItem(key), tokenKey)).toBe(token);
  await mock(page, 'users/me', (route) => respond(route, user));
  await page.getByRole('button', { name: /Спробувати ще раз/ }).click();
  await expect(page.locator('#profile-email')).toHaveText(user.email);
});

test('profile strings are rendered as text and cannot execute HTML', async ({ page }) => {
  const firstName = '<img src=x onerror="window.injected=true">';
  await mock(page, 'users/me', (route) => respond(route, { ...user, firstName }));
  await seedSession(page);
  await page.goto('/dashboard');
  await expect(page.locator('#profile-firstName')).toHaveText(firstName);
  await expect(page.locator('#profile-firstName img')).toHaveCount(0);
  expect(await page.evaluate(() => window.injected)).toBeUndefined();
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
});

test('password visibility toggle is labelled and keyboard accessible', async ({ page }) => {
  await page.goto('/login');
  await expect(page.getByLabel('Пароль', { exact: true })).toHaveAttribute('type', 'password');
  const toggle = page.getByRole('button', { name: 'Показати пароль' });
  await toggle.focus();
  await page.keyboard.press('Enter');
  await expect(page.getByLabel('Пароль', { exact: true })).toHaveAttribute('type', 'text');
  await expect(page.getByRole('button', { name: 'Приховати пароль' })).toHaveAttribute('aria-pressed', 'true');
});

test('loading prevents duplicate submits and a route change cancels a late login', async ({ page }) => {
  let finish;
  let calls = 0;
  const gate = new Promise((resolve) => { finish = resolve; });
  await mock(page, 'auth/login', async (route) => {
    calls++;
    await gate;
    try { await respond(route, loginResult(randomUUID())); } catch { /* The browser may already have cancelled it. */ }
  });
  await page.goto('/login');
  await fillLogin(page);
  await page.getByRole('button', { name: 'Увійти', exact: true }).click();
  await expect.poll(() => calls).toBe(1);
  await expect(page.locator('#auth-form')).toHaveAttribute('aria-busy', 'true');
  await expect(page.getByLabel('Email', { exact: true })).toBeDisabled();
  await page.locator('#auth-form').dispatchEvent('submit');
  await page.getByRole('link', { name: /Створити акаунт/ }).click();
  finish();
  await expect(page).toHaveURL('/register');
  expect(calls).toBe(1);
  expect(await page.evaluate((key) => sessionStorage.getItem(key), tokenKey)).toBeNull();
});

test('a server validation error attaches safe field messages', async ({ page }) => {
  await mock(page, 'auth/login', (route) => respond(route, { errors: { Email: ['Private provider details'] } }, 400));
  await page.goto('/login');
  await fillLogin(page);
  await page.getByRole('button', { name: 'Увійти', exact: true }).click();
  await expect(page.locator('#email-error')).toContainText('Перевір це поле');
  await expect(page.getByLabel('Email', { exact: true })).toHaveAttribute('aria-invalid', 'true');
  await expect(page.getByText('Private provider details')).toHaveCount(0);
});

test('blocked session storage does not report a successful login', async ({ page }) => {
  await page.addInitScript(() => { Storage.prototype.setItem = () => { throw new DOMException('Blocked', 'SecurityError'); }; });
  await mock(page, 'auth/login', (route) => respond(route, loginResult(randomUUID())));
  await page.goto('/login');
  await fillLogin(page);
  await page.getByRole('button', { name: 'Увійти', exact: true }).click();
  await expect(page.getByRole('alert')).toContainText('Браузер не дозволяє зберегти сесію');
  await expect(page).toHaveURL('/login');
});

for (const [field, label] of [['firstName', 'Ім’я'], ['lastName', 'Прізвище']]) {
  test(`numeric ${field} shows a letter validation error without sending a request`, async ({ page }) => {
    let calls = 0;
    await mock(page, 'auth/register', (route) => { calls++; return respond(route, user, 201); });
    await page.goto('/register');
    await fillRegistration(page);
    await page.getByLabel(label, { exact: true }).fill('22');
    await page.getByRole('button', { name: 'Створити акаунт', exact: true }).click();
    await expect(page.locator(`#${field}-error`)).toContainText('принаймні одну літеру');
    await expect(page.getByLabel(label, { exact: true })).toHaveAttribute('aria-invalid', 'true');
    expect(calls).toBe(0);
  });
}

for (const category of ['numeric-sequence', 'repetition', 'common']) {
  test(`a ${category} password shows a safe local error without submitting`, async ({ page }) => {
    let calls = 0;
    await mock(page, 'auth/register', (route) => { calls++; return respond(route, user, 201); });
    await page.goto('/register');
    await fillRegistration(page);
    const password = category === 'numeric-sequence'
      ? Array.from({ length: 16 }, (_, i) => i % 10).join('')
      : category === 'repetition' ? 'x'.repeat(16)
        : Object.keys({ password: null })[0] + Array.from({ length: 5 }, (_, i) => i + 1).join('');
    await page.getByLabel('Пароль', { exact: true }).fill(password);
    await page.getByRole('button', { name: 'Створити акаунт', exact: true }).click();
    await expect(page.locator('#password-error')).toContainText('занадто передбачуваний');
    expect(calls).toBe(0);
  });
}

test('Ukrainian names with apostrophes and hyphens submit successfully', async ({ page }) => {
  let submitted;
  await mock(page, 'auth/register', (route) => { submitted = route.request().postDataJSON(); return respond(route, user, 201); });
  await page.goto('/register');
  await fillRegistration(page);
  await page.getByLabel('Ім’я', { exact: true }).fill('Мар’яна');
  await page.getByLabel('Прізвище', { exact: true }).fill('Анна-Марія');
  await page.getByRole('button', { name: 'Створити акаунт', exact: true }).click();
  await expect(page).toHaveURL('/login');
  expect(submitted.firstName).toBe('Мар’яна');
  expect(submitted.lastName).toBe('Анна-Марія');
});

test('server-side name validation attaches a safe message to the registration field', async ({ page }) => {
  await mock(page, 'auth/register', (route) => respond(route, { errors: { FirstName: ['Private provider details'] } }, 400));
  await page.goto('/register');
  await fillRegistration(page);
  await page.getByRole('button', { name: 'Створити акаунт', exact: true }).click();
  await expect(page.locator('#firstName-error')).toContainText('Перевір це поле');
  await expect(page.getByText('Private provider details')).toHaveCount(0);
});

for (const mode of ['login', 'register']) {
  test(`${mode} restores its nonsecret draft after F5 without storing the password`, async ({ page }) => {
    await page.addInitScript(() => {
      window.storageWrites = [];
      const original = Storage.prototype.setItem;
      Storage.prototype.setItem = function (key, value) {
        window.storageWrites.push(String(value));
        return original.call(this, key, value);
      };
    });
    await page.goto(`/${mode}`);
    const password = randomUUID();
    if (mode === 'register') {
      await page.getByLabel('Ім’я', { exact: true }).fill('Мар’яна');
      await page.getByLabel('Прізвище', { exact: true }).fill('Анна-Марія');
    }
    await page.getByLabel('Email', { exact: true }).fill(user.email);
    await page.getByLabel('Пароль', { exact: true }).fill(password);
    await page.getByRole('button', { name: 'Показати пароль' }).click();
    const safeWrites = await page.evaluate((secret) => window.storageWrites.every((value) => !value.includes(secret)), password);
    await page.getByRole('button', { name: 'Приховати пароль' }).click();
    expect(safeWrites).toBe(true);
    const stored = await page.evaluate((key) => JSON.parse(sessionStorage.getItem(key)), draftKeys[mode]);
    expect(Object.keys(stored).sort()).toEqual(mode === 'login' ? ['email'] : ['email', 'firstName', 'lastName']);
    await page.reload();
    await expect(page.getByLabel('Email', { exact: true })).toHaveValue(user.email);
    await expect(page.getByLabel('Пароль', { exact: true })).toHaveValue('');
    if (mode === 'register') {
      await expect(page.getByLabel('Ім’я', { exact: true })).toHaveValue('Мар’яна');
      await expect(page.getByLabel('Прізвище', { exact: true })).toHaveValue('Анна-Марія');
    }
    expect(await page.evaluate((secret) => [sessionStorage, localStorage].every((storage) =>
      Object.values(storage).every((value) => !value.includes(secret))), password)).toBe(true);
    expect(await page.evaluate(() => localStorage.length)).toBe(0);
  });
}

test('successful registration clears its draft and preserves an unrelated login draft', async ({ page }) => {
  await mock(page, 'auth/register', (route) => respond(route, user, 201));
  await page.goto('/login');
  await page.getByLabel('Email', { exact: true }).fill('login-draft@example.test');
  await page.goto('/register');
  await fillRegistration(page);
  await page.getByRole('button', { name: 'Створити акаунт', exact: true }).click();
  await expect(page).toHaveURL('/login');
  expect(await page.evaluate((key) => sessionStorage.getItem(key), draftKeys.register)).toBeNull();
  await expect(page.getByLabel('Email', { exact: true })).toHaveValue('login-draft@example.test');
  await page.goto('/register');
  await expect(page.getByLabel('Ім’я', { exact: true })).toHaveValue('');
  await expect(page.getByLabel('Прізвище', { exact: true })).toHaveValue('');
  await expect(page.getByLabel('Email', { exact: true })).toHaveValue('');
  await expect(page.getByLabel('Пароль', { exact: true })).toHaveValue('');
});

test('successful login clears both auth drafts and keeps the access token', async ({ page }) => {
  const token = randomUUID();
  await mock(page, 'auth/login', (route) => respond(route, loginResult(token)));
  await mock(page, 'users/me', (route) => respond(route, user));
  await page.goto('/register');
  await page.getByLabel('Ім’я', { exact: true }).fill('Чернетка');
  await page.goto('/login');
  await fillLogin(page);
  await page.getByRole('button', { name: 'Увійти', exact: true }).click();
  await expect(page.locator('#profile-email')).toHaveText(user.email);
  for (const key of Object.values(draftKeys)) {
    expect(await page.evaluate((name) => sessionStorage.getItem(name), key)).toBeNull();
  }
  expect(await page.evaluate((key) => sessionStorage.getItem(key), tokenKey)).toBe(token);
  await page.goto('/login');
  await expect(page.getByLabel('Email', { exact: true })).toHaveValue('');
});

for (const mode of ['login', 'register']) {
  test(`failed ${mode} preserves the nonsecret draft for retry and reload`, async ({ page }) => {
    await mock(page, `auth/${mode}`, (route) => respond(route, {}, mode === 'login' ? 401 : 409));
    await page.goto(`/${mode}`);
    if (mode === 'register') await fillRegistration(page);
    else await fillLogin(page);
    await page.getByRole('button', { name: mode === 'register' ? 'Створити акаунт' : 'Увійти', exact: true }).click();
    await expect(page.getByRole('alert')).toBeVisible();
    await page.reload();
    await expect(page.getByLabel('Email', { exact: true })).toHaveValue(user.email);
    await expect(page.getByLabel('Пароль', { exact: true })).toHaveValue('');
    if (mode === 'register') {
      await expect(page.getByLabel('Ім’я', { exact: true })).toHaveValue(` ${user.firstName} `);
      await expect(page.getByLabel('Прізвище', { exact: true })).toHaveValue(` ${user.lastName} `);
    }
  });
}

test('malformed stored draft does not break the form and is removed', async ({ page }) => {
  await page.goto('/login');
  await page.evaluate((key) => sessionStorage.setItem(key, 'malformed-json'), draftKeys.register);
  await page.goto('/register');
  await expect(page.getByLabel('Ім’я', { exact: true })).toHaveValue('');
  expect(await page.evaluate((key) => sessionStorage.getItem(key), draftKeys.register)).toBeNull();
  await page.getByLabel('Email', { exact: true }).fill(user.email);
  await page.reload();
  await expect(page.getByLabel('Email', { exact: true })).toHaveValue(user.email);
});

test('SPA navigation keeps Login and Register drafts independent', async ({ page }) => {
  await page.goto('/login');
  await page.getByLabel('Email', { exact: true }).fill('login-draft@example.test');
  await page.locator('.form-switch a').click();
  await page.getByLabel('Email', { exact: true }).fill('register-draft@example.test');
  await page.getByLabel('Ім’я', { exact: true }).fill('Тест');
  await page.locator('.form-switch a').click();
  await expect(page.getByLabel('Email', { exact: true })).toHaveValue('login-draft@example.test');
  await page.locator('.form-switch a').click();
  await expect(page.getByLabel('Email', { exact: true })).toHaveValue('register-draft@example.test');
  await expect(page.getByLabel('Ім’я', { exact: true })).toHaveValue('Тест');
});

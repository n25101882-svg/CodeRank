import { test, expect } from '@playwright/test';
import { randomUUID } from 'node:crypto';
import { respond, mock, isolateRequests } from './api-mocks.js';

const tokenKey = 'coderank.accessToken';
const original = { id: 1, title: 'Сума двох чисел', statement: 'Знайдіть суму a та b.',
  inputFormat: 'Два числа.', outputFormat: 'Сума.', difficulty: 'Easy', createdAtUtc: '2026-11-01T10:00:00Z' };
test.beforeEach(async ({ page, baseURL }) => { await isolateRequests(page, baseURL); });

async function session(page) {
  const token = randomUUID();
  await page.goto('/login');
  await page.evaluate(({ key, value }) => sessionStorage.setItem(key, value), { key: tokenKey, value: token });
  return token;
}

async function server(page, token, initial = [original]) {
  let rows = structuredClone(initial);
  const counts = { list: 0, get: 0, post: 0, put: 0, delete: 0 };
  const requests = [];
  function record(route) {
    const request = route.request();
    expect(request.headers().authorization).toBe(`Bearer ${token}`);
    expect(request.url()).not.toContain(token);
    if (['POST', 'PUT'].includes(request.method())) {
      expect(Object.keys(request.postDataJSON()).sort()).toEqual(['difficulty', 'inputFormat', 'outputFormat', 'statement', 'title']);
    }
    requests.push(request.method());
    return request;
  }
  await mock(page, 'problems', (route) => {
    const request = record(route);
    if (request.method() === 'GET') { counts.list++; return respond(route, rows); }
    counts.post++;
    const row = { id: Math.max(0, ...rows.map((item) => item.id)) + 1, ...request.postDataJSON(), createdAtUtc: original.createdAtUtc };
    rows.push(row);
    return respond(route, row, 201);
  });
  await mock(page, 'problems/*', (route) => {
    const request = record(route);
    const id = Number(new URL(request.url()).pathname.split('/').at(-1));
    const row = rows.find((item) => item.id === id);
    if (!row) return respond(route, {}, 404);
    if (request.method() === 'GET') { counts.get++; return respond(route, row); }
    if (request.method() === 'PUT') { counts.put++; Object.assign(row, request.postDataJSON()); return respond(route, null, 204); }
    counts.delete++;
    rows = rows.filter((item) => item.id !== id);
    return respond(route, null, 204);
  });
  return { counts, requests, get rows() { return rows; } };
}

async function fill(page, title = 'Нова задача') {
  await page.getByLabel('Назва', { exact: true }).fill(title);
  await page.getByLabel('Умова', { exact: true }).fill('  Повна умова задачі.\n');
  await page.getByLabel('Складність', { exact: true }).selectOption('Medium');
}

async function ready(page) {
  await page.goto('/problems');
  await expect(page.locator('#problems-loading')).toBeHidden();
}

test('problems without a JWT redirects before any protected API request', async ({ page }) => {
  let calls = 0;
  await mock(page, 'problems', (route) => { calls++; return respond(route, [original]); });
  await page.goto('/problems');
  await expect(page).toHaveURL('/login');
  await expect(page.locator('.problem-card')).toHaveCount(0);
  expect(calls).toBe(0);
});

test('authenticated list survives F5, shows fields and fits desktop, mobile and narrow widths', async ({ page }, info) => {
  const token = await session(page);
  const state = await server(page, token);
  await ready(page);
  await expect(page.locator('.problem-card h3')).toHaveText(original.title);
  await expect(page.locator('.problem-statement')).toHaveText(original.statement);
  await page.locator('.problem-formats summary').click();
  await expect(page.locator('.problem-formats dd')).toHaveText([original.inputFormat, original.outputFormat]);
  await expect(page.locator('.problem-card .role-badge')).toHaveText('Легка');
  await page.reload();
  await expect(page.locator('.problem-card h3')).toHaveText(original.title);
  await expect(page.getByRole('link', { name: 'Мої задачі', exact: true })).toHaveAttribute('aria-current', 'page');
  expect(state.counts.list).toBe(2);
  await page.screenshot({ path: info.outputPath('problems.png'), fullPage: true });
  for (const width of [320, 600, 768]) {
    await page.setViewportSize({ width, height: 900 });
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
  }
});

test('loading hides records and disables writes until the list is verified', async ({ page }) => {
  const token = await session(page);
  await server(page, token);
  let finish;
  const gate = new Promise((resolve) => { finish = resolve; });
  await mock(page, 'problems', async (route) => { await gate; return respond(route, [original]); });
  await page.goto('/problems');
  try {
    await expect(page.locator('#problems-loading')).toBeVisible();
    await expect(page.locator('#problems-list')).toBeHidden();
    await expect(page.getByRole('button', { name: 'Створити задачу', exact: true })).toBeDisabled();
  } finally { finish(); }
  await expect(page.locator('.problem-card h3')).toHaveText(original.title);
  await expect(page.getByRole('button', { name: 'Створити задачу', exact: true })).toBeEnabled();
});

test('an empty list keeps the create form usable', async ({ page }) => {
  const token = await session(page);
  await server(page, token, []);
  await ready(page);
  await expect(page.getByText('У тебе ще немає задач', { exact: true })).toBeVisible();
  await expect(page.getByRole('button', { name: 'Створити задачу', exact: true })).toBeEnabled();
});

test('POST creates a task and reloads the server list without a page reload', async ({ page }) => {
  const token = await session(page);
  const state = await server(page, token, []);
  await ready(page);
  await page.evaluate(() => { window.sameDocument = true; });
  await fill(page, '  Нова задача  ');
  await page.getByRole('button', { name: 'Створити задачу', exact: true }).click();
  await expect(page.locator('#problem-notice')).toHaveText(/Задачу створено/);
  await expect(page.locator('.problem-card h3')).toHaveText('Нова задача');
  expect(state.counts).toMatchObject({ post: 1, list: 2 });
  expect(state.rows[0].statement).toBe('  Повна умова задачі.\n');
  expect(state.rows[0].inputFormat).toBeNull();
  expect(await page.evaluate(() => window.sameDocument)).toBe(true);
  await expect(page.getByLabel('Назва', { exact: true })).toHaveValue('');
});

test('editing reads a fresh detail DTO, sends PUT and handles its empty 204', async ({ page }) => {
  const token = await session(page);
  const state = await server(page, token);
  await ready(page);
  state.rows[0].title = 'Свіжа версія';
  await page.getByRole('button', { name: 'Редагувати', exact: true }).click();
  await expect(page.getByLabel('Назва', { exact: true })).toHaveValue('Свіжа версія');
  await expect(page.getByLabel('Формат введення', { exact: false })).toHaveValue(original.inputFormat);
  await fill(page, 'Змінена задача');
  await page.getByRole('button', { name: 'Зберегти зміни', exact: true }).click();
  await expect(page.locator('#problem-notice')).toContainText('Зміни збережено');
  await expect(page.locator('.problem-card h3')).toHaveText('Змінена задача');
  expect(state.counts).toMatchObject({ get: 1, put: 1, list: 2 });
});

test('delete requires confirmation, supports cancel and refreshes after a 204', async ({ page }) => {
  const token = await session(page);
  const state = await server(page, token);
  await ready(page);
  await page.getByRole('button', { name: 'Видалити', exact: true }).click();
  await expect(page.getByRole('dialog')).toBeVisible();
  await expect(page.locator('#delete-problem-warning')).toContainText('Приклади цієї задачі також буде видалено');
  expect(state.counts.delete).toBe(0);
  await page.getByRole('button', { name: 'Скасувати', exact: true }).click();
  await expect(page.getByRole('dialog')).toBeHidden();
  expect(state.counts.delete).toBe(0);
  await page.getByRole('button', { name: 'Видалити', exact: true }).click();
  await page.getByRole('button', { name: 'Видалити задачу', exact: true }).click();
  await expect(page.getByRole('dialog')).toBeHidden();
  await expect(page.locator('#problem-notice')).toContainText('Задачу видалено');
  await expect(page.locator('#problems-empty')).toBeVisible();
  expect(state.counts).toMatchObject({ delete: 1, list: 2 });
});

test('form validation prevents empty and oversized writes', async ({ page }) => {
  const token = await session(page);
  const state = await server(page, token, []);
  await ready(page);
  await page.getByRole('button', { name: 'Створити задачу', exact: true }).click();
  await expect(page.locator('#problem-title-error')).toContainText('Введи назву');
  await expect(page.locator('#problem-statement-error')).toContainText('Введи умову');
  await fill(page);
  await page.getByLabel('Назва', { exact: true }).evaluate((input) => { input.value = 'x'.repeat(201); });
  await page.getByRole('button', { name: 'Створити задачу', exact: true }).click();
  await expect(page.locator('#problem-title-error')).toContainText('200');
  expect(state.counts.post).toBe(0);
});

test('a server 400 attaches safe field errors and preserves the form', async ({ page }) => {
  const token = await session(page);
  await server(page, token, []);
  await ready(page);
  await mock(page, 'problems', (route) => respond(route, { errors: { Title: ['Private SQL details'] } }, 400));
  await fill(page);
  await page.getByRole('button', { name: 'Створити задачу', exact: true }).click();
  await expect(page.locator('#problem-notice')).toContainText('Перевір заповнення');
  await expect(page.locator('#problem-title')).toHaveAttribute('aria-invalid', 'true');
  await expect(page.getByLabel('Назва', { exact: true })).toHaveValue('Нова задача');
  await expect(page.getByText('Private SQL details')).toHaveCount(0);
  await expect(page.getByRole('button', { name: 'Створити задачу', exact: true })).toBeEnabled();
});

for (const action of ['get', 'put']) {
  test(`${action} 404 remains usable without exposing foreign task data`, async ({ page }) => {
    const token = await session(page);
    await server(page, token);
    await ready(page);
    if (action === 'put') {
      await page.getByRole('button', { name: 'Редагувати', exact: true }).click();
      await expect(page.getByLabel('Назва', { exact: true })).toHaveValue(original.title);
    }
    await mock(page, 'problems/1', (route) => respond(route, { title: 'Private details' }, 404));
    if (action === 'get') await page.getByRole('button', { name: 'Редагувати', exact: true }).click();
    else await page.getByRole('button', { name: 'Зберегти зміни', exact: true }).click();
    await expect(page.locator('#problem-notice')).toContainText('Задачу не знайдено');
    await expect(page.getByRole('button', { name: 'Оновити список', exact: true })).toBeEnabled();
    await expect(page).toHaveURL('/problems');
  });
}

test('delete 409 explains the olympiad link and leaves the task and dialog available', async ({ page }) => {
  const token = await session(page);
  const state = await server(page, token);
  await ready(page);
  await mock(page, 'problems/1', (route) => respond(route, { detail: 'Private FK details' }, 409));
  await page.getByRole('button', { name: 'Видалити', exact: true }).click();
  await page.getByRole('button', { name: 'Видалити задачу', exact: true }).click();
  await expect(page.locator('#delete-problem-error')).toContainText('прив’язано до олімпіади');
  await expect(page.getByRole('dialog')).toBeVisible();
  await expect(page.getByRole('button', { name: 'Скасувати', exact: true })).toBeEnabled();
  expect(state.rows).toHaveLength(1);
  await expect(page.getByText('Private FK details')).toHaveCount(0);
});

for (const failure of ['500', 'network']) {
  test(`list ${failure} supports a new GET after retry`, async ({ page }) => {
    const token = await session(page);
    const state = await server(page, token);
    await mock(page, 'problems', (route) => failure === '500' ? respond(route, {}, 500) : route.abort('failed'));
    await ready(page);
    await expect(page.locator('#problems-error')).toBeVisible();
    await server(page, token);
    await page.getByRole('button', { name: 'Спробувати ще раз', exact: true }).click();
    await expect(page.locator('.problem-card h3')).toHaveText(original.title);
    expect(state.counts.list).toBe(0);
  });
}

test('save 500 restores controls and preserves entered data', async ({ page }) => {
  const token = await session(page);
  await server(page, token);
  await ready(page);
  await mock(page, 'problems', (route) => respond(route, { detail: 'Private exception' }, 500));
  await fill(page);
  await page.getByRole('button', { name: 'Створити задачу', exact: true }).click();
  await expect(page.locator('#problem-notice')).toContainText('не вдалося');
  await expect(page.getByLabel('Назва', { exact: true })).toHaveValue('Нова задача');
  await expect(page.getByRole('button', { name: 'Створити задачу', exact: true })).toBeEnabled();
  await expect(page.getByText('Private exception')).toHaveCount(0);
});

for (const action of ['list', 'get', 'post', 'put', 'delete']) {
  test(`${action} 401 clears the session and returns to Login`, async ({ page }) => {
    const token = await session(page);
    await server(page, token);
    if (action !== 'list') await ready(page);
    if (action === 'put') {
      await page.getByRole('button', { name: 'Редагувати', exact: true }).click();
      await expect(page.getByLabel('Назва', { exact: true })).toHaveValue(original.title);
    }
    await mock(page, ['list', 'post'].includes(action) ? 'problems' : 'problems/1', (route) => respond(route, {}, 401));
    if (action === 'list') await page.goto('/problems');
    else if (action === 'get') await page.getByRole('button', { name: 'Редагувати', exact: true }).click();
    else if (action === 'post') { await fill(page); await page.getByRole('button', { name: 'Створити задачу', exact: true }).click(); }
    else if (action === 'put') await page.getByRole('button', { name: 'Зберегти зміни', exact: true }).click();
    else {
      await page.getByRole('button', { name: 'Видалити', exact: true }).click();
      await page.getByRole('button', { name: 'Видалити задачу', exact: true }).click();
    }
    await expect(page).toHaveURL('/login');
    expect(await page.evaluate((key) => sessionStorage.getItem(key), tokenKey)).toBeNull();
    await expect(page.locator('.problem-card')).toHaveCount(0);
  });
}

test('server text stays inert in cards, form and deletion confirmation', async ({ page }) => {
  const token = await session(page);
  const attack = '<img src=x onerror="window.injected=true">';
  const problem = { ...original, title: attack, statement: '<script>window.injected=true</script>' + 'x'.repeat(800), inputFormat: attack, outputFormat: attack };
  await server(page, token, [problem]);
  await ready(page);
  await expect(page.locator('.problem-card h3')).toHaveText(attack);
  await expect(page.locator('.problem-card img, .problem-card script')).toHaveCount(0);
  await page.getByRole('button', { name: 'Редагувати', exact: true }).click();
  await expect(page.getByLabel('Назва', { exact: true })).toHaveValue(attack);
  await page.getByRole('button', { name: 'Видалити', exact: true }).click();
  await expect(page.locator('#delete-problem-name')).toHaveText(attack);
  await expect(page.locator('#delete-problem-name img')).toHaveCount(0);
  expect(await page.evaluate(() => window.injected)).toBeUndefined();
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
});

test('successful POST stays successful when the follow-up list refresh fails', async ({ page }) => {
  const token = await session(page);
  const state = await server(page, token, []);
  await ready(page);
  await mock(page, 'problems', async (route) => {
    if (route.request().method() === 'GET') return respond(route, {}, 500);
    state.rows.push({ ...original, title: route.request().postDataJSON().title });
    return respond(route, state.rows[0], 201);
  });
  await fill(page);
  await page.getByRole('button', { name: 'Створити задачу', exact: true }).click();
  await expect(page.locator('#problem-notice')).toContainText('Задачу створено');
  await expect(page.locator('#problem-notice')).toHaveAttribute('role', 'status');
  await expect(page.locator('#problems-error')).toBeVisible();
  await server(page, token, state.rows);
  await page.getByRole('button', { name: 'Спробувати ще раз', exact: true }).click();
  await expect(page.locator('.problem-card h3')).toHaveText('Нова задача');
});

for (const pending of ['GET', 'PUT']) {
  test(`route navigation aborts pending ${pending} and late data cannot replace Login`, async ({ page }) => {
    // createApiClient captures fetch at construction, so instrument before loading the SPA.
    await page.addInitScript(() => {
      const originalFetch = window.fetch;
      window.fetch = (url, options) => {
        if (String(url).includes('/api/v1/problems')) options.signal.addEventListener('abort', () => { window.problemsAborted = true; });
        return originalFetch(url, options);
      };
    });
    const token = await session(page);
    await server(page, token);
    await ready(page);
    let finish;
    const gate = new Promise((resolve) => { finish = resolve; });
    await mock(page, pending === 'GET' ? 'problems' : 'problems/1', async (route) => {
      if (pending === 'PUT' && route.request().method() === 'GET') return respond(route, original);
      await gate;
      try { await respond(route, pending === 'GET' ? [original] : null, pending === 'PUT' ? 204 : 200); } catch { /* Already aborted. */ }
    });
    try {
      if (pending === 'GET') await page.getByRole('button', { name: 'Оновити список', exact: true }).click();
      else {
        await page.getByRole('button', { name: 'Редагувати', exact: true }).click();
        await expect(page.getByLabel('Назва', { exact: true })).toHaveValue(original.title);
        await page.getByRole('button', { name: 'Зберегти зміни', exact: true }).click();
      }
      await expect(page.locator('.problems-page')).toHaveAttribute('aria-busy', 'true');
      await page.getByRole('link', { name: 'CodeRank — вхід' }).click();
      await expect(page).toHaveURL('/login');
      expect(await page.evaluate(() => window.problemsAborted)).toBe(true);
    } finally { finish(); }
    await expect(page.getByLabel('Email', { exact: true })).toBeVisible();
    await expect(page.locator('.problem-card')).toHaveCount(0);
  });
}

test('a list timeout stops loading and allows retry', async ({ page }) => {
  const token = await session(page);
  await server(page, token);
  await page.clock.install();
  let finish;
  let calls = 0;
  const gate = new Promise((resolve) => { finish = resolve; });
  await mock(page, 'problems', async (route) => {
    calls++;
    if (calls === 1) { await gate; try { await respond(route, [original]); } catch { /* Already aborted. */ } }
    else await respond(route, [original]);
  });
  await page.goto('/problems');
  try {
    await expect.poll(() => calls).toBe(1);
    await page.clock.fastForward(15_001);
    await expect(page.locator('#problems-error')).toContainText('надто довго');
    await page.getByRole('button', { name: 'Спробувати ще раз', exact: true }).click();
    await expect(page.locator('.problem-card h3')).toHaveText(original.title);
    expect(calls).toBe(2);
  } finally { finish(); }
});

test('cancel editing returns to creation without PUT and navigation preserves Dashboard', async ({ page }) => {
  const token = await session(page);
  const state = await server(page, token);
  await mock(page, 'users/me', (route) => respond(route, { email: 'test@example.test', firstName: 'Тест', lastName: 'Учасник', role: 'User' }));
  await ready(page);
  await page.getByRole('button', { name: 'Редагувати', exact: true }).click();
  await expect(page.getByLabel('Назва', { exact: true })).toHaveValue(original.title);
  await page.getByRole('button', { name: 'Скасувати редагування', exact: true }).click();
  await expect(page.getByLabel('Назва', { exact: true })).toHaveValue('');
  expect(state.counts.put).toBe(0);
  await page.getByRole('link', { name: 'Мій профіль', exact: true }).click();
  await expect(page.locator('#profile-email')).toHaveText('test@example.test');
  await page.getByRole('link', { name: 'Мої задачі', exact: true }).click();
  await expect(page.locator('.problem-card h3')).toHaveText(original.title);
});

test('invalid DTO data cannot be rendered as a verified list', async ({ page }) => {
  const token = await session(page);
  await server(page, token);
  await mock(page, 'problems', (route) => respond(route, [{ ...original, statement: null }]));
  await ready(page);
  await expect(page.locator('#problems-error')).toContainText('некоректну відповідь');
  await expect(page.locator('.problem-card')).toHaveCount(0);
});

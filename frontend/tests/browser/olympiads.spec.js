import { test, expect } from '@playwright/test';
import { randomUUID } from 'node:crypto';
import { respond, mock, isolateRequests } from './api-mock.js';

const contest = { id: 31, title: 'Алгоритмічний старт', description: 'Змагання для першого кроку.',
  startsAtUtc: '2026-11-10T10:00:00Z', endsAtUtc: '2026-11-10T12:00:00Z' };
const tokenKey = 'coderank.accessToken';
test.use({ timezoneId: 'America/Los_Angeles' }); // UTC must not become the browser's local time.
test.beforeEach(async ({ page, baseURL }) => { await isolateRequests(page, baseURL); });

test('public olympiads opens directly without JWT, shows UTC and survives F5 on each viewport', async ({ page }, info) => {
  let calls = 0;
  await mock(page, 'olympiads', (route) => {
    calls++;
    expect(route.request().headers().authorization).toBeUndefined();
    expect(route.request().method()).toBe('GET');
    return respond(route, [contest]);
  });
  await page.goto('/olympiads');
  await expect(page.getByRole('heading', { name: 'Олімпіади', exact: true })).toBeVisible();
  await expect(page.getByRole('heading', { name: contest.title })).toBeVisible();
  await expect(page.locator('time').first()).toHaveText(/10\.11\.2026,?\s+10:00 UTC/);
  await expect(page.locator('time').last()).toHaveText(/10\.11\.2026,?\s+12:00 UTC/);
  await expect(page.locator('time').first()).toHaveAttribute('datetime', contest.startsAtUtc);
  await page.reload();
  await expect(page).toHaveURL('/olympiads');
  await expect(page.getByRole('heading', { name: contest.title })).toBeVisible();
  await expect(page.getByRole('link', { name: 'Олімпіади', exact: true })).toHaveAttribute('aria-current', 'page');
  expect(calls).toBe(2);
  expect(await page.evaluate((key) => sessionStorage.getItem(key), tokenKey)).toBeNull();
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
  await page.screenshot({ path: info.outputPath('olympiads.png'), fullPage: true });
  for (const width of [320, 600, 768]) {
    await page.setViewportSize({ width, height: 900 });
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
  }
});

test('loading remains visible until the response arrives and exposes no partial cards', async ({ page }) => {
  let finish;
  const gate = new Promise((resolve) => { finish = resolve; });
  await mock(page, 'olympiads', async (route) => { await gate; return respond(route, [contest]); });
  await page.goto('/olympiads');
  try {
    await expect(page.locator('#olympiads-loading')).toBeVisible();
    await expect(page.locator('.olympiads-page')).toHaveAttribute('aria-busy', 'true');
    await expect(page.locator('#olympiads-list')).toBeHidden();
    await expect(page.locator('#olympiads-empty')).toBeHidden();
    await expect(page.locator('#olympiads-error')).toBeHidden();
  } finally { finish(); }
  await expect(page.getByRole('heading', { name: contest.title })).toBeVisible();
  await expect(page.locator('#olympiads-loading')).toBeHidden();
  await expect(page.locator('.olympiads-page')).not.toHaveAttribute('aria-busy');
});

test('cards preserve response order, both date labels and nullable descriptions', async ({ page }) => {
  const second = { ...contest, id: 7, title: 'Наступний виклик', description: null };
  await mock(page, 'olympiads', (route) => respond(route, [contest, second]));
  await page.goto('/olympiads');
  const cards = page.locator('.olympiad-card');
  await expect(cards).toHaveCount(2);
  await expect(cards.locator('h2')).toHaveText([contest.title, second.title]);
  await expect(cards.first().locator('.olympiad-description')).toHaveText(contest.description);
  await expect(cards.last().locator('.olympiad-description')).toHaveCount(0);
  await expect(cards.last()).not.toContainText('null');
  await expect(cards.last().locator('dt')).toHaveText(['Початок', 'Завершення']);
});

test('empty response shows the specified message instead of cards', async ({ page }) => {
  await mock(page, 'olympiads', (route) => respond(route, []));
  await page.goto('/olympiads');
  await expect(page.getByText('Поки немає доступних олімпіад', { exact: true })).toBeVisible();
  await expect(page.locator('#olympiads-list')).toBeHidden();
  await expect(page.locator('#olympiads-loading')).toBeHidden();
});

for (const failure of ['http', 'network']) {
  test(`${failure} failure gives a safe message and retry performs a new request`, async ({ page }) => {
    let calls = 0;
    await mock(page, 'olympiads', (route) => {
      calls++;
      if (calls > 1) return respond(route, [contest]);
      return failure === 'http' ? respond(route, { detail: 'Private server details' }, 503) : route.abort('failed');
    });
    await page.goto('/olympiads');
    await expect(page.getByRole('alert')).toContainText(failure === 'http' ? 'Не вдалося завантажити' : 'Не вдалося під’єднатися');
    await expect(page.getByText('Private server details')).toHaveCount(0);
    await expect(page.locator('#olympiads-list')).toBeHidden();
    await page.getByRole('button', { name: 'Спробувати ще раз', exact: true }).click();
    await expect(page.getByRole('heading', { name: contest.title })).toBeVisible();
    await expect(page.locator('#olympiads-error')).toBeHidden();
    expect(calls).toBe(2);
  });
}

for (const [name, payload] of [
  ['wrapped object', { items: [contest] }],
  ['invalid item', [null]],
  ['invalid date', [{ ...contest, startsAtUtc: '2026-02-30T10:00:00Z' }]],
]) {
  test(`${name} response is rejected without displaying partial data`, async ({ page }) => {
    await mock(page, 'olympiads', (route) => respond(route, payload));
    await page.goto('/olympiads');
    await expect(page.getByRole('alert')).toContainText('некоректні дані олімпіад');
    await expect(page.locator('.olympiad-card')).toHaveCount(0);
    await expect(page.getByRole('button', { name: 'Спробувати ще раз' })).toBeVisible();
  });
}

test('titles and descriptions are plain text with no executable HTML or overflow', async ({ page }) => {
  const title = '<img src=x onerror="window.injected=true">' + 'x'.repeat(150);
  const description = '<script>window.injected=true</script>\n<svg onload="window.injected=true">';
  await mock(page, 'olympiads', (route) => respond(route, [{ ...contest, title, description }]));
  await page.goto('/olympiads');
  await expect(page.locator('.olympiad-card h2')).toHaveText(title);
  await expect(page.locator('.olympiad-description')).toHaveText(description);
  await expect(page.locator('.olympiad-card img, .olympiad-card script, .olympiad-card svg')).toHaveCount(0);
  expect(await page.evaluate(() => window.injected)).toBeUndefined();
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
});

test('a timeout exits loading and allows a successful retry', async ({ page }) => {
  await page.clock.install();
  let finish;
  let calls = 0;
  const gate = new Promise((resolve) => { finish = resolve; });
  await mock(page, 'olympiads', async (route) => {
    calls++;
    if (calls === 1) {
      await gate;
      try { await respond(route, [contest]); } catch { /* The timed out request is already aborted. */ }
    } else await respond(route, [contest]);
  });
  await page.goto('/olympiads');
  try {
    await expect.poll(() => calls).toBe(1);
    await page.clock.fastForward(15_001);
    await expect(page.getByRole('alert')).toContainText('надто довго');
    await expect(page.locator('#olympiads-loading')).toBeHidden();
    await page.getByRole('button', { name: 'Спробувати ще раз' }).click();
    await expect(page.getByRole('heading', { name: contest.title })).toBeVisible();
    expect(calls).toBe(2);
  } finally { finish(); }
});

test('leaving the route aborts its fetch and a late response cannot replace Login', async ({ page }) => {
  await page.addInitScript(() => {
    const original = window.fetch;
    window.fetch = (url, options) => {
      if (String(url).endsWith('/api/v1/olympiads')) {
        options.signal.addEventListener('abort', () => { window.olympiadsAborted = true; });
      }
      return original(url, options);
    };
  });
  let finish;
  const gate = new Promise((resolve) => { finish = resolve; });
  await mock(page, 'olympiads', async (route) => {
    await gate;
    try { await respond(route, [contest]); } catch { /* Route navigation aborted the request. */ }
  });
  await page.goto('/olympiads');
  try {
    await expect(page.locator('#olympiads-loading')).toBeVisible();
    await page.getByRole('link', { name: 'CodeRank — вхід' }).click();
    await expect(page).toHaveURL('/login');
    expect(await page.evaluate(() => window.olympiadsAborted)).toBe(true);
  } finally { finish(); }
  await expect(page.getByLabel('Email', { exact: true })).toBeVisible();
  await expect(page.locator('.olympiad-card')).toHaveCount(0);
  await expect(page.getByRole('alert')).toBeHidden();
});

test('navigation connects Register, Olympiads and Dashboard without losing the session', async ({ page }) => {
  const token = randomUUID();
  const user = { email: 'test@example.test', firstName: 'Тест', lastName: 'Учасник', role: 'User' };
  await mock(page, 'olympiads', (route) => {
    expect(route.request().headers().authorization).toBeUndefined();
    return respond(route, [contest]);
  });
  await mock(page, 'users/me', (route) => respond(route, user));
  await page.goto('/register');
  await page.getByRole('link', { name: 'Олімпіади', exact: true }).click();
  await expect(page.getByRole('heading', { name: contest.title })).toBeVisible();
  await page.evaluate(({ key, value }) => sessionStorage.setItem(key, value), { key: tokenKey, value: token });
  await page.reload();
  await expect(page.getByRole('heading', { name: contest.title })).toBeVisible();
  await page.getByRole('link', { name: 'Мій профіль', exact: true }).click();
  await expect(page.locator('#profile-email')).toHaveText(user.email);
  await page.getByRole('link', { name: 'Олімпіади', exact: true }).click();
  await expect(page.getByRole('heading', { name: contest.title })).toBeVisible();
  expect(await page.evaluate((key) => sessionStorage.getItem(key), tokenKey)).toBe(token);
  await page.getByRole('button', { name: 'Вийти', exact: true }).click();
  await expect(page).toHaveURL('/login');
  expect(await page.evaluate((key) => sessionStorage.getItem(key), tokenKey)).toBeNull();
});

test('a public API 401 stays on Olympiads and does not clear an existing token', async ({ page }) => {
  const token = randomUUID();
  await mock(page, 'olympiads', (route) => respond(route, {}, 401));
  await page.goto('/login');
  await page.evaluate(({ key, value }) => sessionStorage.setItem(key, value), { key: tokenKey, value: token });
  await page.getByRole('link', { name: 'Олімпіади', exact: true }).click();
  await expect(page.getByRole('alert')).toContainText('Не вдалося завантажити');
  await expect(page).toHaveURL('/olympiads');
  expect(await page.evaluate((key) => sessionStorage.getItem(key), tokenKey)).toBe(token);
});

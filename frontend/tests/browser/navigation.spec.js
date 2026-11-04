import { test, expect } from '@playwright/test';
import { randomUUID } from 'node:crypto';
import { respond, mock, isolateRequests } from './api-mocks.js';

const tokenKey = 'coderank.accessToken';
const contest = { id: 1, title: 'Олімпіада після об’єднання', description: null,
  startsAtUtc: '2026-11-10T10:00:00Z', endsAtUtc: '2026-11-10T12:00:00Z' };
const problem = { id: 1, title: 'Власна задача після об’єднання', statement: 'Повна умова.',
  inputFormat: null, outputFormat: null, difficulty: 'Easy', createdAtUtc: '2026-11-01T10:00:00Z' };
const user = { email: 'navigation@coderank.test', firstName: 'Тест', lastName: 'Навігації', role: 'User' };

test.beforeEach(async ({ page, baseURL }) => { await isolateRequests(page, baseURL); });

async function seedSession(page) {
  const token = randomUUID();
  await page.goto('/login');
  await page.evaluate(({ key, value }) => sessionStorage.setItem(key, value), { key: tokenKey, value: token });
  return token;
}

test('shared navigation connects Dashboard, public Olympiads and protected Problems without losing the session', async ({ page }) => {
  const token = await seedSession(page);
  await mock(page, 'olympiads', (route) => {
    expect(route.request().headers().authorization).toBeUndefined();
    return respond(route, [contest]);
  });
  await mock(page, 'problems', (route) => {
    expect(route.request().headers().authorization).toBe(`Bearer ${token}`);
    return respond(route, [problem]);
  });
  await mock(page, 'users/me', (route) => {
    expect(route.request().headers().authorization).toBe(`Bearer ${token}`);
    return respond(route, user);
  });
  await page.goto('/dashboard');
  await expect(page.locator('#profile-email')).toHaveText(user.email);
  for (const name of ['Олімпіади', 'Мій профіль', 'Мої задачі']) {
    await expect(page.getByRole('link', { name, exact: true })).toHaveCount(1);
  }
  await page.getByRole('link', { name: 'Олімпіади', exact: true }).click();
  await expect(page.locator('.olympiad-card h2')).toHaveText(contest.title);
  await page.getByRole('link', { name: 'Мої задачі', exact: true }).click();
  await expect(page.locator('.problem-card h3')).toHaveText(problem.title);
  await expect(page.getByRole('link', { name: 'Мої задачі', exact: true })).toHaveAttribute('aria-current', 'page');
  await page.goBack();
  await expect(page).toHaveURL('/olympiads');
  await expect(page.locator('.olympiad-card h2')).toHaveText(contest.title);
  await page.goForward();
  await expect(page).toHaveURL('/problems');
  await expect(page.locator('.problem-card h3')).toHaveText(problem.title);
  for (const width of [320, 600, 768]) {
    await page.setViewportSize({ width, height: 900 });
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
    for (const name of ['Олімпіади', 'Мій профіль', 'Мої задачі']) {
      await expect(page.getByRole('link', { name, exact: true })).toBeVisible();
    }
  }
  await page.getByRole('link', { name: 'Мій профіль', exact: true }).click();
  await expect(page.locator('#profile-email')).toHaveText(user.email);
  expect(await page.evaluate((key) => sessionStorage.getItem(key), tokenKey)).toBe(token);
  await page.getByRole('button', { name: 'Вийти', exact: true }).click();
  await expect(page).toHaveURL('/login');
  await expect(page.getByRole('link', { name: 'Мої задачі', exact: true })).toBeHidden();
  await page.getByRole('link', { name: 'Олімпіади', exact: true }).click();
  await expect(page.locator('.olympiad-card h2')).toHaveText(contest.title);
  expect(await page.evaluate((key) => sessionStorage.getItem(key), tokenKey)).toBeNull();
});

test('public Olympiads does not grant anonymous access to protected Problems', async ({ page }) => {
  let protectedCalls = 0;
  await mock(page, 'olympiads', (route) => respond(route, [contest]));
  await mock(page, 'problems', (route) => { protectedCalls++; return respond(route, [problem]); });
  await page.goto('/olympiads');
  await expect(page.locator('.olympiad-card h2')).toHaveText(contest.title);
  await expect(page.getByRole('link', { name: 'Мої задачі', exact: true })).toBeHidden();
  await page.goto('/problems');
  await expect(page).toHaveURL('/login');
  expect(protectedCalls).toBe(0);
  await expect(page.locator('.problem-card')).toHaveCount(0);
});

for (const [from, to, link, loading, card] of [
  ['problems', 'olympiads', 'Олімпіади', '#problems-loading', '.olympiad-card h2'],
  ['olympiads', 'problems', 'Мої задачі', '#olympiads-loading', '.problem-card h3'],
]) {
  test(`switching ${from} to ${to} aborts the old request and ignores its late response`, async ({ page }) => {
    await page.addInitScript(() => {
      const original = window.fetch;
      window.abortedModuleRequests = [];
      window.fetch = (url, options) => {
        const path = new URL(url).pathname;
        if (options?.signal && path.startsWith('/api/v1/')) {
          options.signal.addEventListener('abort', () => { window.abortedModuleRequests.push(path); });
        }
        return original(url, options);
      };
    });
    const token = await seedSession(page);
    let finish;
    const gate = new Promise((resolve) => { finish = resolve; });
    const data = { olympiads: contest, problems: problem };
    await mock(page, from, async (route) => {
      await gate;
      try { await respond(route, [data[from]]); } catch { /* The old route's request has been cancelled. */ }
    });
    await mock(page, to, (route) => respond(route, [data[to]]));
    await page.goto(`/${from}`);
    try {
      await expect(page.locator(loading)).toBeVisible();
      await page.getByRole('link', { name: link, exact: true }).click();
      await expect(page.locator(card)).toHaveText(data[to].title);
      await expect.poll(() => page.evaluate(() => window.abortedModuleRequests)).toContain(`/api/v1/${from}`);
    } finally { finish(); }
    await expect(page).toHaveURL(`/${to}`);
    await expect(page.locator(card)).toHaveText(data[to].title);
    await expect(page.locator(`.${from}-page`)).toHaveCount(0);
    expect(await page.evaluate((key) => sessionStorage.getItem(key), tokenKey)).toBe(token);
  });
}

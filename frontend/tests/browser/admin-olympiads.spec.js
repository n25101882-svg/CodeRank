import { test, expect } from '@playwright/test';
import { API_ROOT, isolateRequests, mock, respond } from './api-mocks.js';

const adminPath = '/admin/olympiads';
const detailPath = `${adminPath}/31`;
const resource = 'admin/olympiads';
const tokenKey = 'coderank.accessToken';
const token = 'admin-test-session';
const contest = { id: 31, title: 'Алгоритмічна олімпіада', description: 'Чернетка змагання',
  startsAtUtc: '2030-07-01T12:00:00Z', endsAtUtc: '2030-07-01T14:00:00Z', isPublished: false, isOwn: true };
const emptyLinks = { revision: 'A'.repeat(64), items: [] };
const first = { problemId: 9, title: 'Сума', position: 1, maxPoints: 40, isVisible: true };
const second = { problemId: 4, title: 'Масиви', position: 2, maxPoints: 50, isVisible: false };
const catalog = [{ id: 9, title: 'Сума' }, { id: 4, title: 'Масиви' }, { id: 12, title: 'Графи' }];

test.beforeEach(async ({ page, baseURL }) => { await isolateRequests(page, baseURL); });

async function session(page) {
  await page.addInitScript(({ key, value }) => sessionStorage.setItem(key, value), { key: tokenKey, value: token });
}

async function editor(page, problems = emptyLinks, olympiad = contest) {
  await mock(page, `${resource}/31`, route => respond(route, { olympiad, problems }));
  await mock(page, `${resource}/problems`, route => respond(route, catalog));
}

test('Dashboard shows the management link for an admin and opens the draft list', async ({ page }) => {
  await session(page);
  await mock(page, 'users/me', route => respond(route, { email: 'admin@coderank.test', firstName: null, lastName: null, role: 'Admin' }));
  await mock(page, resource, route => {
    expect(route.request().headers().authorization).toBe(`Bearer ${token}`);
    return respond(route, [contest, { ...contest, id: 32, title: 'Спільне змагання', isOwn: false, isPublished: true }]);
  });
  await page.goto('/dashboard');
  await page.getByRole('link', { name: 'Керування олімпіадами' }).click();
  await expect(page).toHaveURL(adminPath);
  await expect(page.getByRole('link', { name: contest.title })).toContainText('Чернетка · Моя');
  await expect(page.getByRole('link', { name: 'Спільне змагання' })).toContainText('Опублікована · Спільна');
});

test('anonymous navigation redirects to login before an admin request', async ({ page }) => {
  let adminCalls = 0;
  page.on('request', request => { if (request.url().startsWith(`${API_ROOT}/api/v1/${resource}`)) adminCalls++; });
  await page.goto(detailPath);
  await expect(page).toHaveURL('/login');
  expect(adminCalls).toBe(0);
});

test('forbidden admin routes hide controls and keep the signed-in user session', async ({ page }) => {
  await session(page);
  await mock(page, resource, route => respond(route, { title: 'Private server details' }, 403));
  await page.goto(adminPath);
  await expect(page.getByRole('alert')).toContainText('лише активним адміністраторам');
  await expect(page.getByRole('button', { name: 'Створити чернетку' })).toHaveCount(0);
  await expect(page.getByRole('button', { name: 'Спробувати ще раз' })).toHaveCount(0);
  await expect(page.getByText('Private server details')).toHaveCount(0);
  expect(await page.evaluate(key => sessionStorage.getItem(key), tokenKey)).toBe(token);
});

test('an expired session on the admin page returns to login and clears the token', async ({ page }) => {
  await session(page);
  await mock(page, resource, route => respond(route, {}, 401));
  await page.goto(adminPath);
  await expect(page).toHaveURL('/login');
  expect(await page.evaluate(key => sessionStorage.getItem(key), tokenKey)).toBeNull();
});

test('the empty list can create a draft and rejects reversed dates before posting', async ({ page }) => {
  await session(page);
  await editor(page);
  let posts = 0;
  await mock(page, resource, route => {
    if (route.request().method() === 'GET') return respond(route, []);
    posts++;
    expect(route.request().method()).toBe('POST');
    expect(route.request().postDataJSON()).toEqual({
      title: contest.title, description: null,
      startsAtUtc: '2030-07-01T12:00:00.000Z', endsAtUtc: '2030-07-01T14:00:00.000Z',
    });
    return respond(route, contest, 201);
  });
  await page.goto(adminPath);
  await expect(page.getByText('Поки немає олімпіад', { exact: false })).toBeVisible();
  await page.getByLabel('Назва').fill(contest.title);
  await page.getByLabel('Початок (UTC)').fill('2030-07-01T12:00');
  await page.getByLabel('Завершення (UTC)').fill('2030-07-01T11:00');
  await page.getByRole('button', { name: 'Створити чернетку' }).click();
  await expect(page.getByRole('alert')).toContainText('Завершення має бути пізніше початку');
  expect(posts).toBe(0);
  await page.getByLabel('Завершення (UTC)').fill('2030-07-01T14:00');
  await page.getByRole('button', { name: 'Створити чернетку' }).click();
  await expect(page).toHaveURL(detailPath);
  await expect(page.getByRole('heading', { name: contest.title })).toBeVisible();
  await expect(page.locator('#admin-publication-status')).toHaveText('Чернетка');
  expect(posts).toBe(1);
});

test('metadata edits are saved and disable publishing while there are unsaved details', async ({ page }) => {
  await session(page);
  await editor(page);
  let saved = false;
  await mock(page, `${resource}/31`, route => {
    if (route.request().method() === 'GET') return respond(route, { olympiad: contest, problems: emptyLinks });
    expect(route.request().postDataJSON()).toEqual({
      title: 'Оновлена олімпіада', description: contest.description,
      startsAtUtc: contest.startsAtUtc, endsAtUtc: contest.endsAtUtc,
    });
    saved = true;
    return respond(route, null, 204);
  });
  await page.goto(detailPath);
  await expect(page.getByRole('heading', { name: contest.title })).toBeVisible();
  await page.getByLabel('Назва').fill('Оновлена олімпіада');
  await expect(page.getByRole('button', { name: 'Опублікувати', exact: true })).toBeDisabled();
  await page.getByRole('button', { name: 'Зберегти налаштування' }).click();
  await expect(page.getByRole('heading', { name: 'Оновлена олімпіада' })).toBeVisible();
  await expect(page.getByRole('button', { name: 'Опублікувати', exact: true })).toBeEnabled();
  expect(saved).toBe(true);
});

test('positions, points, visibility, attachment and detachment are sent in one validated batch', async ({ page }) => {
  await session(page);
  const problems = { ...emptyLinks, items: [first, second] };
  await editor(page, problems);
  let saves = 0;
  await mock(page, `${resource}/31/problems`, route => {
    saves++;
    const { revision, items } = route.request().postDataJSON();
    expect(revision).toBe(problems.revision);
    expect(items).toEqual([
      { problemId: 4, position: 1, maxPoints: 75, isVisible: true },
      { problemId: 12, position: 2, maxPoints: 100, isVisible: false },
    ]);
    return respond(route, { revision: 'B'.repeat(64), items: [
      { ...items[0], title: second.title }, { ...items[1], title: 'Графи' },
    ] });
  });
  await page.goto(detailPath);
  const row = title => page.locator('.admin-link-row').filter({ has: page.getByRole('heading', { name: title, exact: true }) });
  await row('Масиви').getByLabel('Позиція').fill('1');
  await page.getByRole('button', { name: 'Зберегти задачі' }).click();
  await expect(page.getByRole('alert')).toContainText('Позиції задач мають бути унікальні');
  expect(saves).toBe(0);
  await row('Сума').getByRole('button', { name: 'Від’єднати' }).click();
  await row('Масиви').getByLabel('Максимум балів').fill('75');
  await row('Масиви').getByLabel('Видима учасникам').check();
  await page.getByLabel('Задача з каталогу адміністраторів').selectOption('12');
  await page.getByRole('button', { name: 'Додати задачу' }).click();
  await expect(page.getByRole('button', { name: 'Опублікувати', exact: true })).toBeDisabled();
  await page.getByRole('button', { name: 'Зберегти задачі' }).click();
  await expect(page.getByText('Задачі олімпіади збережено.')).toBeVisible();
  await expect(page.locator('.admin-link-row h3')).toHaveText(['Масиви', 'Графи']);
  expect(saves).toBe(1);
  await page.setViewportSize({ width: 320, height: 850 });
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
});

test('publish and unpublish require confirmation and change the public catalog', async ({ page }) => {
  await session(page);
  let published = false;
  let changes = 0;
  await editor(page);
  await mock(page, `${resource}/31`, route => respond(route, {
    olympiad: { ...contest, isPublished: published }, problems: emptyLinks,
  }));
  await mock(page, `${resource}/31/publication`, route => {
    const value = route.request().postDataJSON();
    expect(value).toEqual({ isPublished: !published });
    published = value.isPublished;
    changes++;
    return respond(route, null, 204);
  });
  await mock(page, 'olympiads', route => respond(route, published ? [contest] : []));
  await page.goto(detailPath);
  const publish = page.getByRole('button', { name: 'Опублікувати', exact: true });
  page.once('dialog', dialog => dialog.dismiss());
  await publish.click();
  expect(changes).toBe(0);
  page.once('dialog', dialog => dialog.accept());
  await publish.click();
  await expect(page.locator('#admin-publication-status')).toHaveText('Опублікована');
  await page.getByRole('link', { name: 'Олімпіади', exact: true }).click();
  await expect(page.getByRole('link', { name: contest.title })).toBeVisible();
  await page.goBack();
  page.once('dialog', dialog => dialog.accept());
  await page.getByRole('button', { name: 'Зняти з публікації' }).click();
  await expect(page.locator('#admin-publication-status')).toHaveText('Чернетка');
  await page.getByRole('link', { name: 'Олімпіади', exact: true }).click();
  await expect(page.getByText('Поки немає доступних олімпіад')).toBeVisible();
  expect(changes).toBe(2);
});

test('network failure exposes Retry and server text renders literally after recovery', async ({ page }) => {
  await session(page);
  let release;
  let calls = 0;
  const gate = new Promise(resolve => { release = resolve; });
  const title = '<img src=x onerror="window.adminInjected=true">';
  await mock(page, resource, async route => {
    calls++;
    if (calls > 1) return respond(route, [{ ...contest, title }]);
    await gate;
    return route.abort('failed');
  });
  await page.goto(adminPath);
  try {
    await expect(page.getByText('Завантажуємо дані олімпіад…')).toBeVisible();
    await expect(page.getByRole('button', { name: 'Створити чернетку' })).toHaveCount(0);
  } finally { release(); }
  await expect(page.getByRole('alert')).toContainText('Не вдалося під’єднатися');
  await page.getByRole('button', { name: 'Спробувати ще раз' }).click();
  await expect(page.getByRole('link', { name: title, exact: true })).toBeVisible();
  await expect(page.locator('#admin-list img')).toHaveCount(0);
  expect(await page.evaluate(() => window.adminInjected)).toBeUndefined();
});

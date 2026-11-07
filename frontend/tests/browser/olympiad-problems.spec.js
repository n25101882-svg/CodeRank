import { test, expect } from '@playwright/test';
import { API_ROOT, respond, mock, isolateRequests } from './api-mocks.js';

const contest = { id: 31, title: 'Алгоритмічний старт', description: null,
  startsAtUtc: '2030-06-01T10:00:00Z', endsAtUtc: '2030-06-01T12:00:00Z' };
const sum = { id: 20, title: 'Сума чисел', difficulty: 'Medium', maxPoints: 125 };
const arrays = { id: 5, title: 'Масиви', difficulty: null, maxPoints: 50 };
const detail = { ...sum, statement: 'Додай два числа.\nВиведи суму.', inputFormat: 'Два цілі числа.',
  outputFormat: 'Одне ціле число.', examples: [
    { inputData: '1 2\n', expectedOutput: '3\n', explanation: 'Один плюс два.' },
    { inputData: '-4 1', expectedOutput: '-3', explanation: null },
  ] };
const listPath = '/olympiads/31';
const detailPath = `${listPath}/problems/20`;

function publicResponse(route, data, status) {
  expect(route.request().method()).toBe('GET');
  expect(route.request().headers().authorization).toBeUndefined();
  return respond(route, data, status);
}

test.beforeEach(async ({ page, baseURL }) => { await isolateRequests(page, baseURL); });

test('the olympiad card opens the ordered list and the selected problem opens its full statement', async ({ page }) => {
  await mock(page, 'olympiads', (route) => publicResponse(route, [contest]));
  await mock(page, 'olympiads/31', (route) => publicResponse(route, contest));
  await mock(page, 'olympiads/31/problems', (route) => publicResponse(route, [sum, arrays]));
  await mock(page, 'olympiads/31/problems/20', (route) => publicResponse(route, detail));

  await page.goto('/olympiads');
  await page.getByRole('link', { name: contest.title, exact: true }).click();
  await expect(page).toHaveURL(listPath);
  await expect(page.getByRole('heading', { name: contest.title })).toBeVisible();
  const cards = page.getByRole('list', { name: 'Задачі олімпіади' }).getByRole('link');
  await expect(cards).toHaveCount(2);
  await expect(cards.first()).toHaveAccessibleName(sum.title);
  await expect(cards.last()).toHaveAccessibleName(arrays.title);
  await expect(cards.first()).toContainText('Складність: Середня');
  await expect(cards.first()).toContainText('Бали: 125');
  await expect(cards.last()).toContainText('Складність: Не вказано');

  await cards.first().click();
  await expect(page).toHaveURL(detailPath);
  await expect(page.getByRole('heading', { name: sum.title, exact: true })).toBeVisible();
  await expect(page.locator('.olympiad-problem-detail .problem-statement').first())
    .toHaveJSProperty('textContent', detail.statement);
  await expect(page.getByRole('heading', { name: 'Формат введення' })).toBeVisible();
  await expect(page.getByText(detail.inputFormat, { exact: true })).toBeVisible();
  await expect(page.getByRole('heading', { name: 'Формат виведення' })).toBeVisible();
  await expect(page.getByText(detail.outputFormat, { exact: true })).toBeVisible();
  const examples = page.locator('.olympiad-problem-example');
  await expect(examples.locator('h3')).toHaveText(['Приклад 1', 'Приклад 2']);
  await expect(examples.first().locator('pre').first()).toHaveJSProperty('textContent', '1 2\n');
  await expect(examples.last().locator('pre').last()).toHaveText('-3');
  await expect(page.getByText('Один плюс два.')).toBeVisible();
  await page.reload();
  await expect(page.getByRole('heading', { name: sum.title, exact: true })).toBeVisible();
  await page.getByRole('link', { name: '← До задач олімпіади' }).click();
  await expect(page).toHaveURL(listPath);
  await expect(cards).toHaveCount(2);
  await page.goBack();
  await expect(page).toHaveURL(detailPath);
  await expect(page.getByRole('heading', { name: sum.title, exact: true })).toBeVisible();
});

test('a direct olympiad URL shows loading and then an empty state', async ({ page }) => {
  let finish;
  const gate = new Promise((resolve) => { finish = resolve; });
  await mock(page, 'olympiads/31', (route) => respond(route, contest));
  await mock(page, 'olympiads/31/problems', async (route) => {
    await gate;
    return respond(route, []);
  });
  await page.goto(listPath);
  try {
    await expect(page.getByText('Завантажуємо задачі олімпіади…')).toBeVisible();
    await expect(page.locator('#olympiad-problems-content')).toBeHidden();
    await expect(page.getByText('Поки немає доступних задач')).toBeHidden();
  } finally { finish(); }
  await expect(page.getByRole('heading', { name: contest.title })).toBeVisible();
  await expect(page.getByRole('heading', { name: 'Поки немає доступних задач' })).toBeVisible();
  await expect(page.getByText('Завантажуємо задачі олімпіади…')).toBeHidden();
});

for (const [path, message] of [
  [listPath, 'Олімпіаду не знайдено або вона недоступна.'],
  [detailPath, 'Задачу не знайдено або вона недоступна.'],
]) {
  test(`a 404 at ${path} shows a safe unavailable message`, async ({ page }) => {
    await mock(page, 'olympiads/31', (route) => respond(route, {}, 404));
    await mock(page, 'olympiads/31/problems', (route) => respond(route, { detail: 'Private contest' }, 404));
    await mock(page, 'olympiads/31/problems/20', (route) => respond(route, { detail: 'Private statement' }, 404));
    await page.goto(path);
    await expect(page.getByRole('alert').locator('p')).toHaveText(message);
    await expect(page.locator('#olympiad-problems-content')).toBeHidden();
    await expect(page.getByRole('button', { name: 'Спробувати ще раз' })).toHaveCount(0);
    await expect(page.getByText(/Private (contest|statement)/)).toHaveCount(0);
  });
}

test('invalid olympiad and problem route IDs do not issue API requests', async ({ page }) => {
  let requests = 0;
  page.on('request', (request) => { if (request.url().startsWith(API_ROOT)) requests++; });
  await page.goto('/olympiads/not-an-id');
  await expect(page.getByRole('alert')).toContainText('Олімпіаду не знайдено');
  await page.goto('/olympiads/31/problems/0');
  await expect(page.getByRole('alert')).toContainText('Задачу не знайдено');
  expect(requests).toBe(0);
});

for (const [path, endpoint, payload, title] of [
  [listPath, 'olympiads/31/problems', [sum], contest.title],
  [detailPath, 'olympiads/31/problems/20', detail, sum.title],
]) {
  test(`a network error at ${path} allows a successful retry`, async ({ page }) => {
    let attempts = 0;
    await mock(page, 'olympiads/31', (route) => respond(route, contest));
    await mock(page, endpoint, (route) => {
      attempts++;
      return attempts === 1 ? route.abort('failed') : respond(route, payload);
    });
    await page.goto(path);
    await expect(page.getByRole('alert')).toContainText('Не вдалося під’єднатися');
    await expect(page.locator('#olympiad-problems-loading')).toBeHidden();
    await page.getByRole('button', { name: 'Спробувати ще раз' }).click();
    await expect(page.getByRole('heading', { name: title, exact: true })).toBeVisible();
    await expect(page.getByRole('alert')).toHaveCount(0);
    expect(attempts).toBe(2);
  });
}

test('problem fields and examples render as text without horizontal overflow', async ({ page }) => {
  const payload = '<img src=x onerror="window.problemInjected = true">';
  const unsafe = { ...detail, title: payload + 'x'.repeat(140), statement: payload, inputFormat: payload,
    outputFormat: payload, examples: [{ inputData: payload + 'x'.repeat(400), expectedOutput: payload, explanation: payload }] };
  await mock(page, 'olympiads/31/problems/20', (route) => respond(route, unsafe));
  await page.goto(detailPath);
  await expect(page.getByRole('heading', { name: unsafe.title, exact: true })).toBeVisible();
  const article = page.locator('.olympiad-problem-detail');
  await expect(article.locator('pre').first()).toHaveJSProperty('textContent', unsafe.examples[0].inputData);
  await expect(article.getByText(payload, { exact: true })).toHaveCount(5);
  await expect(page.locator('#main img, #main script, #main svg')).toHaveCount(0);
  expect(await page.evaluate(() => window.problemInjected)).toBeUndefined();
  await page.setViewportSize({ width: 320, height: 850 });
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
});

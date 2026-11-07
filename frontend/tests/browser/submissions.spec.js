import { test, expect } from '@playwright/test';
import { isolateRequests, mock, respond } from './api-mocks.js';

const problemApi = 'olympiads/31/problems/20';
const detailPath = '/' + problemApi;
const token = 'isolated-session';
const problem = { id: 20, title: 'Сума чисел', difficulty: 'Medium', maxPoints: 100,
  statement: 'Додай два числа.', inputFormat: null, outputFormat: null, examples: [] };
const receipt = { id: 41, status: 'Queued', submittedAtUtc: '2030-06-01T10:01:00Z' };
const summary = { ...receipt, olympiadId: 31, problemId: 20, language: 'python3' };
const source = '  # Привіт\nprint("<script>🚀</script>")\n';
const history = (items = [], page = 1, hasMore = false) => ({ items, page, pageSize: 20, hasMore });
const send = page => page.getByRole('button', { name: 'Надіслати', exact: true });
const editor = page => page.getByRole('textbox', { name: 'Вихідний код' });
const historyList = page => page.getByRole('list', { name: 'Надіслані розв’язки' });

async function signIn(page) {
  await page.addInitScript(value => sessionStorage.setItem('coderank.accessToken', value), token);
}

async function mockHistory(page, handle = route => respond(route, history())) {
  await mock(page, 'submissions?**', route => {
    expect(route.request().method()).toBe('GET');
    expect(route.request().headers().authorization).toBe('Bearer ' + token);
    const params = new URL(route.request().url()).searchParams;
    expect(params.get('olympiadId')).toBe('31');
    expect(params.get('problemId')).toBe('20');
    return handle(route, Number(params.get('page')));
  });
}

test.beforeEach(async ({ page, baseURL }) => {
  await isolateRequests(page, baseURL);
  await mock(page, problemApi, route => {
    expect(route.request().headers().authorization).toBeUndefined();
    return respond(route, problem);
  });
});

test('guests can read the problem and are invited to sign in without calling private APIs', async ({ page }) => {
  let privateCalls = 0;
  for (const path of ['submissions?**', problemApi + '/submissions']) {
    await mock(page, path, route => { privateCalls++; return respond(route, {}, 401); });
  }
  await page.goto(detailPath);
  await expect(page.getByText(problem.statement)).toBeVisible();
  await expect(page.getByRole('heading', { name: 'Надіслати розв’язок' })).toBeVisible();
  await expect(page.locator('#submission-guest a')).toHaveAttribute('href', '/login');
  await expect(editor(page)).toBeHidden();
  await expect(page.getByRole('heading', { name: 'Мої спроби' })).toBeHidden();
  expect(privateCalls).toBe(0);
});

test('reads a local file, prevents duplicate submit and refreshes the history after Queued', async ({ page }) => {
  await signIn(page);
  let posts = 0;
  let received = false;
  let release;
  const gate = new Promise(resolve => { release = resolve; });
  await mockHistory(page, route => respond(route, history(received ? [summary] : [])));
  await mock(page, problemApi + '/submissions', async route => {
    posts++;
    expect(route.request().method()).toBe('POST');
    expect(route.request().headers().authorization).toBe('Bearer ' + token);
    expect(route.request().postDataJSON()).toEqual({ language: 'python3', sourceCode: source });
    await gate;
    received = true;
    return respond(route, receipt, 201);
  });

  await page.goto(detailPath);
  await expect(page.getByText('Поки немає надісланих розв’язків.')).toBeVisible();
  await page.getByLabel('Мова програмування').selectOption('python3');
  await page.locator('#submission-file').setInputFiles({
    name: 'answer.PY', mimeType: 'text/plain', buffer: Buffer.from(source, 'utf8'),
  });
  await expect(editor(page)).toHaveValue(source);
  await expect(page.locator('#submission-size')).toContainText('байтів UTF-8');
  await send(page).click();
  await expect(page.getByText('Надсилаємо розв’язок…')).toBeVisible();
  await expect(send(page)).toBeDisabled();
  await page.locator('#submission-form').dispatchEvent('submit');
  release();
  await expect(page.getByText('Розв’язок #41 збережено. Очікує перевірки.')).toBeVisible();
  await expect(editor(page)).toHaveValue('');
  await expect(historyList(page)).toContainText('#41');
  await expect(historyList(page)).toContainText('Python 3');
  await expect(historyList(page)).toContainText('Очікує перевірки');
  await expect(historyList(page).locator('time')).toHaveAttribute('datetime', receipt.submittedAtUtc);
  await expect(historyList(page).locator('time')).toContainText('UTC');
  expect(posts).toBe(1);
  expect(await page.evaluate(() => document.documentElement.scrollWidth)).toBeLessThanOrEqual(page.viewportSize().width);
});

test('blank code, oversized UTF-8 and an invalid local file do not post', async ({ page }) => {
  await signIn(page);
  await mockHistory(page);
  let posts = 0;
  await mock(page, problemApi + '/submissions', route => { posts++; return respond(route, receipt, 201); });
  await page.goto(detailPath);
  await send(page).click();
  await expect(page.getByText('Введи вихідний код.')).toBeVisible();
  await editor(page).fill('ї'.repeat(32769));
  await expect(page.getByText('Код має займати не більше 64 КіБ у UTF-8.')).toBeVisible();
  await send(page).click();
  await expect(editor(page)).toHaveAttribute('aria-invalid', 'true');
  await page.locator('#submission-file').setInputFiles({ name: 'binary.exe', mimeType: 'text/plain', buffer: Buffer.from('ignored') });
  await expect(page.getByText('Обери текстовий файл .cpp, .py або .txt.')).toBeVisible();
  expect(posts).toBe(0);
});

test('history shows loading then supports paging and reload on a direct URL', async ({ page }) => {
  await signIn(page);
  let release;
  const gate = new Promise(resolve => { release = resolve; });
  await mockHistory(page, async (route, number) => {
    await gate;
    return respond(route, number === 1 ? history([summary], 1, true) : history([{ ...summary, id: 40 }], 2));
  });
  await page.goto(detailPath);
  await expect(page.getByText('Завантажуємо спроби…')).toBeVisible();
  await expect(historyList(page).getByRole('listitem')).toHaveCount(0);
  release();
  await expect(historyList(page)).toContainText('#41');
  await page.getByRole('button', { name: 'Наступна' }).click();
  await expect(historyList(page)).toContainText('#40');
  await expect(page.getByRole('button', { name: 'Наступна' })).toBeDisabled();
  await page.getByRole('button', { name: 'Попередня' }).click();
  await expect(historyList(page)).toContainText('#41');
  await page.reload();
  await expect(historyList(page)).toContainText('#41');
});

test('retries history and preserves the editor when delivery is uncertain', async ({ page }) => {
  await signIn(page);
  let reads = 0;
  let posts = 0;
  await mockHistory(page, route => ++reads === 1 ? route.abort('failed') : respond(route, history()));
  await mock(page, problemApi + '/submissions', route => {
    posts++;
    return posts === 1 ? route.abort('failed') : respond(route, receipt, 201);
  });
  await page.goto(detailPath);
  await expect(page.locator('#submission-history-error')).toBeVisible();
  await page.getByRole('button', { name: 'Спробувати ще раз' }).click();
  await expect(page.getByText('Поки немає надісланих розв’язків.')).toBeVisible();
  await editor(page).fill(source);
  await send(page).click();
  await expect(page.getByText('Не вдалося підтвердити надсилання. Онови історію перед повторною спробою.')).toBeVisible();
  await expect(editor(page)).toHaveValue(source);
  expect(posts).toBe(1);
  await page.getByRole('button', { name: 'Оновити', exact: true }).click();
  await expect.poll(() => reads).toBe(3);
  await send(page).click();
  await expect(page.getByText('Розв’язок #41 збережено. Очікує перевірки.')).toBeVisible();
});

for (const [code, text] of [
  ['olympiad_not_started', 'Олімпіада ще не почалася.'],
  ['olympiad_ended', 'Олімпіада завершилася. Нові розв’язки вже не приймаються.'],
]) {
  test('explains a closed submission window: ' + code, async ({ page }) => {
    await signIn(page);
    await mockHistory(page);
    await mock(page, problemApi + '/submissions', route => respond(route, { code, title: 'Internal text' }, 409));
    await page.goto(detailPath);
    await editor(page).fill(source);
    await send(page).click();
    await expect(page.getByText(text)).toBeVisible();
    await expect(editor(page)).toHaveValue(source);
    await expect(page.getByText('Internal text')).toBeHidden();
  });
}

for (const [status, text] of [[401, 'Сесія завершилася.'], [403, 'Надсилання доступне активним користувачам.']]) {
  test('keeps the problem public when the private history returns ' + status, async ({ page }) => {
    await signIn(page);
    await mockHistory(page, route => respond(route, {}, status));
    await page.goto(detailPath);
    await expect(page.locator('#submission-guest-message')).toContainText(text);
    await expect(page.getByText(problem.statement)).toBeVisible();
    await expect(editor(page)).toBeHidden();
    await expect(historyList(page)).toBeHidden();
    await expect(page).toHaveURL(detailPath);
    expect(await page.evaluate(() => sessionStorage.getItem('coderank.accessToken') === null)).toBe(true);
  });
}

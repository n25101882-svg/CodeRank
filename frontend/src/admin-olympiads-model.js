const dateInputPattern = /^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}(?::\d{2}(?:\.\d{1,3})?)?$/;
export const maxLinkValue = 2_147_483_647;

export function matchAdminOlympiadRoute(path) {
  const match = /^\/admin\/olympiads(?:\/([^/]+))?$/.exec(path);
  if (!match) return null;
  if (match[1] === undefined) return { id: undefined };
  const id = Number(match[1]);
  return { id: /^[1-9]\d*$/.test(match[1]) && Number.isSafeInteger(id) ? id : null };
}

export const dateTimeInput = (utc) => utc.slice(0, -1).slice(0, 23);

export function utcOrderKey(value) {
  const [second, fraction = ''] = value.slice(0, -1).split('.');
  return `${second}.${fraction.padEnd(7, '0')}`;
}

function toUtc(value, saved) {
  if (typeof value !== 'string' || !dateInputPattern.test(value) || value.startsWith('0000')) return null;
  const date = new Date(`${value}Z`);
  if (!Number.isFinite(date.getTime())) return null;
  const utc = date.toISOString();
  if (!utc.startsWith(value)) return null;
  return saved && dateTimeInput(saved).startsWith(value) && new Date(saved).toISOString() === utc ? saved : utc;
}

export function validateOlympiadForm(fields, saved) {
  const title = String(fields.title ?? '').trim();
  if (!title || title.length > 200) return { error: 'Введи назву від 1 до 200 символів.' };
  const startsAtUtc = toUtc(fields.startsAtUtc, saved?.startsAtUtc);
  const endsAtUtc = toUtc(fields.endsAtUtc, saved?.endsAtUtc);
  if (!startsAtUtc || !endsAtUtc) return { error: 'Вкажи коректні дату й час початку та завершення.' };
  if (utcOrderKey(endsAtUtc) <= utcOrderKey(startsAtUtc)) return { error: 'Завершення має бути пізніше початку.' };
  return { value: { title, description: fields.description || null, startsAtUtc, endsAtUtc } };
}

export function validateOlympiadLinks(rows) {
  const ids = new Set();
  const positions = new Set();
  const items = [];
  const validNumber = (value) => Number.isInteger(value) && value > 0 && value <= maxLinkValue;
  for (const row of rows) {
    const { problemId, isVisible } = row;
    const position = Number(row.position);
    const maxPoints = Number(row.maxPoints);
    if (!Number.isSafeInteger(problemId) || problemId <= 0 || typeof isVisible !== 'boolean') {
      return { error: 'Одна із задач має некоректні дані.' };
    }
    if (!validNumber(position) || !validNumber(maxPoints)) {
      return { error: 'Позиція й бали мають бути цілими числами від 1 до 2 147 483 647.' };
    }
    if (ids.has(problemId)) return { error: 'Задачу можна додати лише один раз.' };
    if (positions.has(position)) return { error: 'Позиції задач мають бути унікальні.' };
    if (row.title === null && isVisible) return { error: 'Приховай або від’єднай задачу поза каталогом.' };
    ids.add(problemId);
    positions.add(position);
    items.push({ problemId, position, maxPoints, isVisible });
  }
  return { value: items };
}

export function nextOlympiadPosition(rows) {
  const used = new Set(rows.map(row => Number(row.position)));
  let position = 1;
  while (used.has(position)) position++;
  return position;
}

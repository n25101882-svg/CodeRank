import { errorMessage } from './validation.js';

const utcFormatter = new Intl.DateTimeFormat('uk-UA', {
  day: '2-digit', month: '2-digit', year: 'numeric',
  hour: '2-digit', minute: '2-digit', hourCycle: 'h23', timeZone: 'UTC',
});

export function formatUtcDate(value) {
  return `${utcFormatter.format(new Date(value))} UTC`;
}

function createCard(olympiad) {
  const item = document.createElement('li');
  const card = document.createElement('a');
  card.className = 'panel olympiad-card';
  card.href = `/olympiads/${olympiad.id}`;
  card.dataset.route = '';
  card.setAttribute('aria-label', olympiad.title);
  const eyebrow = document.createElement('p');
  eyebrow.className = 'eyebrow';
  eyebrow.textContent = 'ОЛІМПІАДА / CODERANK';
  const title = document.createElement('h2');
  title.textContent = olympiad.title;
  card.append(eyebrow, title);
  if (olympiad.description?.trim()) {
    const description = document.createElement('p');
    description.className = 'olympiad-description';
    description.textContent = olympiad.description;
    card.append(description);
  }
  const dates = document.createElement('dl');
  dates.className = 'olympiad-dates';
  for (const [label, value] of [['Початок', olympiad.startsAtUtc], ['Завершення', olympiad.endsAtUtc]]) {
    const row = document.createElement('div');
    const term = document.createElement('dt');
    term.textContent = label;
    const detail = document.createElement('dd');
    const time = document.createElement('time');
    time.dateTime = value;
    time.textContent = formatUtcDate(value);
    detail.append(time);
    row.append(term, detail);
    dates.append(row);
  }
  const action = document.createElement('span');
  action.className = 'olympiad-card-action';
  action.textContent = 'Переглянути задачі →';
  card.append(dates, action);
  item.append(card);
  return item;
}

export function setupOlympiads(root, api, signal) {
  const list = root.querySelector('#olympiads-list');
  const loading = root.querySelector('#olympiads-loading');
  const empty = root.querySelector('#olympiads-empty');
  const errorPanel = root.querySelector('#olympiads-error');
  let busy = false;

  async function load() {
    if (busy || signal.aborted) return;
    busy = true;
    list.hidden = empty.hidden = errorPanel.hidden = true;
    list.replaceChildren();
    loading.hidden = false;
    root.setAttribute('aria-busy', 'true');
    try {
      if (!api) throw new Error('Service unavailable');
      const olympiads = await api.olympiads({ signal });
      if (signal.aborted) return;
      if (olympiads.length === 0) empty.hidden = false;
      else {
        const fragment = document.createDocumentFragment();
        for (const olympiad of olympiads) fragment.append(createCard(olympiad));
        list.append(fragment);
        list.hidden = false;
      }
    } catch (error) {
      if (signal.aborted) return;
      errorPanel.querySelector('p').textContent = error.code === 'response'
        ? 'CodeRank повернув некоректні дані олімпіад. Спробуй ще раз.'
        : error.status ? 'Не вдалося завантажити олімпіади. Спробуй ще раз.' : errorMessage(error, 'olympiads');
      errorPanel.hidden = false;
    } finally {
      if (!signal.aborted) {
        busy = false;
        loading.hidden = true;
        root.removeAttribute('aria-busy');
      }
    }
  }

  root.querySelector('#retry-olympiads').addEventListener('click', load);
  load();
}

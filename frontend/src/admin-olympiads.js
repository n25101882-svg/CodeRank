import { dateTimeInput, nextOlympiadPosition, validateOlympiadForm, validateOlympiadLinks } from './admin-olympiads-model.js';
import { formatUtcDate } from './olympiads.js';
import { errorMessage } from './validation.js';

const adminPath = '/admin/olympiads';

function adminErrorMessage(error) {
  if (error.status === 403) return 'Доступ дозволено лише активним адміністраторам.';
  if (error.status === 404) return 'Олімпіаду або задачу не знайдено чи вона недоступна.';
  if (error.status === 409) {
    switch (error.problemCode) {
      case 'duplicate_position': return 'Позиції задач мають бути унікальні.';
      case 'duplicate_problem': return 'Задачу можна додати лише один раз.';
      case 'unavailable_visible_problem': return 'Приховай або від’єднай задачу поза каталогом перед публікацією.';
      default: return 'Інший запит змінив олімпіаду. Онови сторінку перед повторним збереженням.';
    }
  }
  return errorMessage(error, 'admin');
}

function contestCard(olympiad) {
  const item = document.createElement('li');
  const card = document.createElement('a');
  card.className = 'panel olympiad-card';
  card.href = `${adminPath}/${olympiad.id}`;
  card.dataset.route = '';
  card.setAttribute('aria-label', olympiad.title);
  const title = document.createElement('h2');
  title.textContent = olympiad.title;
  const status = document.createElement('p');
  status.className = 'olympiad-description';
  status.textContent = `${olympiad.isPublished ? 'Опублікована' : 'Чернетка'} · ${olympiad.isOwn ? 'Моя' : 'Спільна'}`;
  const dates = document.createElement('p');
  dates.className = 'olympiads-timezone';
  dates.textContent = `${formatUtcDate(olympiad.startsAtUtc)} — ${formatUtcDate(olympiad.endsAtUtc)}`;
  card.append(title, status, dates);
  item.append(card);
  return item;
}

export function setupAdminOlympiads(root, { api, id, session, signal, navigate, onUnauthorized }) {
  const find = selector => root.querySelector(selector);
  const isDetail = id !== undefined;
  const requests = api?.adminOlympiads;
  const options = { signal };
  const controls = find('#admin-controls');
  const content = find('#admin-content');
  const loading = find('#admin-loading');
  const errorPanel = find('#admin-error');
  const retry = find('#admin-retry');
  const notice = find('#admin-notice');
  const detailsForm = find('#admin-details-form');
  const rowsContainer = find('#admin-link-rows');
  const select = find('#admin-problem-select');
  const addLink = find('#admin-add-link');
  const saveLinks = find('#admin-save-links');
  const publish = find('#admin-publish');
  const rows = new Map();
  let olympiad;
  let revision;
  let catalog = [];
  let detailsDirty = false;
  let linksDirty = false;
  let busy = false;

  function showNotice(text, success = false) {
    notice.hidden = false;
    notice.classList.toggle('notice-success', success);
    notice.setAttribute('role', success ? 'status' : 'alert');
    notice.querySelector('.notice-symbol').textContent = success ? '✓' : '!';
    notice.querySelector('p').textContent = text;
  }

  function showLoadError(error) {
    content.hidden = true;
    errorPanel.hidden = false;
    errorPanel.querySelector('p').textContent = adminErrorMessage(error);
    retry.hidden = error.status === 403 || error.status === 404;
  }

  function refreshActions() {
    const unsaved = detailsDirty || linksDirty;
    publish.disabled = unsaved;
    saveLinks.disabled = !linksDirty;
    find('#admin-unsaved').hidden = !unsaved;
  }

  function refreshChoices() {
    const selected = select.value;
    const available = catalog.filter(problem => !rows.has(problem.id));
    select.replaceChildren(new Option(available.length ? 'Обери задачу' : 'Немає доступних задач', ''));
    for (const problem of available) select.add(new Option(problem.title, String(problem.id)));
    if (available.some(problem => String(problem.id) === selected)) select.value = selected;
    select.disabled = available.length === 0;
    addLink.disabled = !select.value;
    find('#admin-links-empty').hidden = rows.size !== 0;
  }

  function linkDrafts() {
    return [...rows.values()].map(({ item, node }) => ({
      problemId: item.problemId, title: item.title,
      position: node.querySelector('[name=position]').value,
      maxPoints: node.querySelector('[name=maxPoints]').value,
      isVisible: node.querySelector('[name=isVisible]').checked,
    }));
  }

  function markLinksDirty() {
    linksDirty = true;
    refreshActions();
  }

  function appendRow(item) {
    const node = find('#admin-link-row').content.firstElementChild.cloneNode(true);
    node.querySelector('h3').textContent = item.title ?? `Задача #${item.problemId} · поза каталогом`;
    node.querySelector('[name=position]').value = item.position;
    node.querySelector('[name=maxPoints]').value = item.maxPoints;
    node.querySelector('[name=isVisible]').checked = item.isVisible;
    node.querySelector('.admin-detach').addEventListener('click', () => {
      node.remove();
      rows.delete(item.problemId);
      refreshChoices();
      markLinksDirty();
    });
    rows.set(item.problemId, { item, node });
    rowsContainer.append(node);
  }

  function renderLinks(problems) {
    revision = problems.revision;
    rows.clear();
    rowsContainer.replaceChildren();
    for (const item of problems.items) appendRow(item);
    linksDirty = false;
    refreshChoices();
    refreshActions();
  }

  function renderPublication() {
    publish.textContent = olympiad.isPublished ? 'Зняти з публікації' : 'Опублікувати';
    find('#admin-publication-status').textContent = olympiad.isPublished ? 'Опублікована' : 'Чернетка';
    const publicLink = find('#admin-public-link');
    publicLink.href = `/olympiads/${id}`;
    publicLink.hidden = !olympiad.isPublished;
  }

  async function load() {
    if (busy || signal.aborted) return;
    if (id === null) {
      loading.hidden = true;
      showLoadError({ status: 404 });
      return;
    }
    busy = true;
    loading.hidden = false;
    content.hidden = errorPanel.hidden = true;
    root.setAttribute('aria-busy', 'true');
    try {
      if (!requests) throw new Error('API unavailable');
      const token = session.read();
      if (isDetail) {
        const [detail, problems] = await Promise.all([requests.get(id, token, options), requests.catalog(token, options)]);
        if (signal.aborted) return;
        olympiad = detail.olympiad;
        catalog = problems;
        find('#admin-title').textContent = olympiad.title;
        for (const field of ['title', 'description']) detailsForm.elements.namedItem(field).value = olympiad[field] ?? '';
        for (const field of ['startsAtUtc', 'endsAtUtc']) detailsForm.elements.namedItem(field).value = dateTimeInput(olympiad[field]);
        renderLinks(detail.problems);
        renderPublication();
      } else {
        const list = await requests.list(token, options);
        if (signal.aborted) return;
        find('#admin-list').replaceChildren(...list.map(contestCard));
        find('#admin-list-empty').hidden = list.length > 0;
      }
      content.hidden = false;
    } catch (error) {
      if (!signal.aborted) {
        if (error.status === 401) onUnauthorized();
        else showLoadError(error);
      }
    } finally {
      if (!signal.aborted) {
        busy = false;
        loading.hidden = true;
        root.removeAttribute('aria-busy');
      }
    }
  }

  async function runAction(action) {
    if (busy || signal.aborted) return;
    busy = true;
    controls.disabled = true;
    controls.setAttribute('aria-busy', 'true');
    notice.hidden = true;
    try {
      await action(session.read());
    } catch (error) {
      if (signal.aborted) return;
      if (error.status === 401) onUnauthorized();
      else if (error.status === 403) showLoadError(error);
      else if (!isDetail && ['network', 'timeout'].includes(error.code))
        showNotice('Створення могло завершитися. Онови список і перевір чернетки перед повторною спробою.');
      else showNotice(adminErrorMessage(error));
    } finally {
      if (!signal.aborted) {
        busy = controls.disabled = false;
        controls.removeAttribute('aria-busy');
      }
    }
  }

  detailsForm.addEventListener('submit', event => {
    event.preventDefault();
    const result = validateOlympiadForm(Object.fromEntries(new FormData(detailsForm)), olympiad);
    if (result.error) { showNotice(result.error); return; }
    runAction(async token => {
      if (!isDetail) {
        const created = await requests.create(result.value, token, options);
        if (!signal.aborted) navigate(`${adminPath}/${created.id}`);
        return;
      }
      await requests.update(id, result.value, token, options);
      if (signal.aborted) return;
      olympiad = { ...olympiad, ...result.value };
      detailsForm.elements.namedItem('title').value = olympiad.title;
      find('#admin-title').textContent = olympiad.title;
      detailsDirty = false;
      refreshActions();
      showNotice('Налаштування олімпіади збережено.', true);
    });
  });

  if (isDetail) {
    find('#admin-back').hidden = false;
    find('#admin-list-panel').hidden = true;
    root.classList.add('admin-detail');
    find('#admin-editor-title').textContent = 'Налаштування олімпіади';
    find('#admin-save-details').textContent = 'Зберегти налаштування';
    find('#admin-links-panel').hidden = find('#admin-publication-panel').hidden = false;
    detailsForm.addEventListener('input', () => { detailsDirty = true; refreshActions(); });
    rowsContainer.addEventListener('input', markLinksDirty);
    select.addEventListener('change', () => { addLink.disabled = !select.value; });
    addLink.addEventListener('click', () => {
      const problem = catalog.find(item => item.id === Number(select.value));
      if (!problem || rows.has(problem.id)) return;
      appendRow({ problemId: problem.id, title: problem.title, position: nextOlympiadPosition(linkDrafts()),
        maxPoints: 100, isVisible: false });
      refreshChoices();
      markLinksDirty();
    });
    find('#admin-links-form').addEventListener('submit', event => {
      event.preventDefault();
      if (!linksDirty) return;
      const result = validateOlympiadLinks(linkDrafts());
      if (result.error) { showNotice(result.error); return; }
      runAction(async token => {
        const updated = await requests.replaceProblems(id, { revision, items: result.value }, token, options);
        if (signal.aborted) return;
        renderLinks(updated);
        showNotice('Задачі олімпіади збережено.', true);
      });
    });
    publish.addEventListener('click', () => {
      if (detailsDirty || linksDirty || busy) return;
      const isPublished = !olympiad.isPublished;
      if (!window.confirm(isPublished ? 'Опублікувати олімпіаду в каталозі CodeRank?' : 'Зняти олімпіаду з публікації?')) return;
      runAction(async token => {
        await requests.setPublication(id, isPublished, token, options);
        if (signal.aborted) return;
        olympiad.isPublished = isPublished;
        renderPublication();
        showNotice(isPublished ? 'Олімпіаду опубліковано.' : 'Олімпіаду знято з публікації.', true);
      });
    });
  }
  retry.addEventListener('click', load);
  load();
}

import { DIFFICULTIES, PROBLEM_FIELDS, validateProblem } from './problems-model.js';
import { errorMessage } from './validation.js';

function element(tag, className, text) {
  const node = document.createElement(tag);
  if (className) node.className = className;
  if (text !== undefined) node.textContent = text;
  return node;
}

function problemCard(problem) {
  const item = element('li');
  const card = element('article', 'panel problem-card');
  const heading = element('div', 'problem-card-heading');
  heading.append(element('h3', '', problem.title), element('span', 'role-badge', DIFFICULTIES[problem.difficulty] || 'Без складності'));
  card.append(heading, element('p', 'problem-statement', problem.statement));
  const formats = element('details', 'problem-formats');
  formats.append(element('summary', '', 'Формати введення та виведення'));
  const details = element('dl');
  for (const [label, field] of [['Введення', 'inputFormat'], ['Виведення', 'outputFormat']]) {
    const row = element('div');
    row.append(element('dt', '', label), element('dd', '', problem[field] || 'Не зазначено'));
    details.append(row);
  }
  formats.append(details);
  const actions = element('div', 'problem-card-actions');
  for (const [action, label] of [['edit', 'Редагувати'], ['delete', 'Видалити']]) {
    const button = element('button', `button button-small ${action === 'delete' ? 'button-danger-outline' : 'button-outline'}`, label);
    button.type = 'button';
    button.dataset.action = action;
    button.dataset.id = String(problem.id);
    actions.append(button);
  }
  card.append(formats, actions);
  item.append(card);
  return item;
}

function failureMessage(error, writing = false) {
  if (error.status === 409) return 'Задачу не можна видалити: її прив’язано до олімпіади.';
  if (error.status === 404) return 'Задачу не знайдено. Онови список — можливо, її вже видалено.';
  if (error.status === 400 || error.code === 'validation') return 'Перевір заповнення полів та спробуй ще раз.';
  if (error.code === 'response') return 'CodeRank повернув некоректну відповідь. Перед повторенням онови список.';
  const message = errorMessage(error, 'problems');
  return writing && ['network', 'timeout'].includes(error.code)
    ? `${message} Перед повторенням перевір оновлений список.` : message;
}

export function setupProblems(root, { api, session, signal, onUnauthorized }) {
  const form = root.querySelector('#problem-form');
  const notice = root.querySelector('#problem-notice');
  const list = root.querySelector('#problems-list');
  const loading = root.querySelector('#problems-loading');
  const empty = root.querySelector('#problems-empty');
  const listError = root.querySelector('#problems-error');
  const operation = root.querySelector('#problem-operation');
  const dialog = root.querySelector('#delete-problem-dialog');
  const dialogError = root.querySelector('#delete-problem-error');
  let busy = false;
  let editingId = null;
  let deleting = null;
  let records = [];

  function setBusy(value, message = '') {
    busy = value;
    root.setAttribute('aria-busy', String(value));
    form.querySelector('fieldset').disabled = value;
    for (const button of root.querySelectorAll('button')) button.disabled = value;
    operation.hidden = !value || !message || dialog.open;
    root.querySelector('#delete-problem-loading').hidden = !value || !dialog.open;
    operation.querySelector('span:last-child').textContent = message;
  }

  function message(text, success = false) {
    notice.querySelector('p').textContent = text;
    notice.querySelector('.notice-symbol').textContent = success ? '✓' : '!';
    notice.classList.toggle('notice-success', success);
    notice.setAttribute('role', success ? 'status' : 'alert');
    notice.hidden = false;
  }

  function fieldErrors(errors = {}) {
    for (const field of PROBLEM_FIELDS) {
      const input = form.elements.namedItem(field);
      root.querySelector(`#problem-${field}-error`).textContent = errors[field] || '';
      if (errors[field]) input.setAttribute('aria-invalid', 'true');
      else input.removeAttribute('aria-invalid');
    }
  }

  function token() {
    const value = session.read();
    if (!value) onUnauthorized();
    return value;
  }

  function handleError(error, writing = false, inDialog = false) {
    if (error.status === 401) { onUnauthorized(); return; }
    if (inDialog) {
      dialogError.textContent = failureMessage(error, writing);
      dialogError.hidden = false;
    } else {
      const errors = {};
      for (const field of PROBLEM_FIELDS) {
        if ((error.fields || []).some((name) => name.replace(/^\$\./, '').toLowerCase() === field.toLowerCase())) {
          errors[field] = 'Перевір це поле та спробуй ще раз.';
        }
      }
      fieldErrors(errors);
      message(failureMessage(error, writing));
    }
  }

  function resetEditor(focus = false) {
    editingId = null;
    form.reset();
    fieldErrors();
    root.querySelector('#problem-editor-title').textContent = 'Нова задача';
    root.querySelector('#save-problem').textContent = 'Створити задачу';
    root.querySelector('#cancel-problem-edit').hidden = true;
    if (focus) {
      form.elements.namedItem('title').focus();
      form.scrollIntoView({ block: 'start' });
    }
  }

  async function loadList(preserveNotice = false) {
    if (busy || signal.aborted) return;
    const accessToken = token();
    if (!accessToken) return;
    if (!preserveNotice) notice.hidden = true;
    list.hidden = empty.hidden = listError.hidden = true;
    list.replaceChildren();
    records = [];
    loading.hidden = false;
    setBusy(true);
    try {
      if (!api) throw new Error('Service unavailable');
      const result = await api.listProblems(accessToken, { signal });
      if (signal.aborted) return;
      records = result;
      if (!records.length) empty.hidden = false;
      else {
        const fragment = document.createDocumentFragment();
        for (const problem of records) fragment.append(problemCard(problem));
        list.append(fragment);
        list.hidden = false;
      }
    } catch (error) {
      if (signal.aborted) return;
      if (error.status === 401) { onUnauthorized(); return; }
      listError.querySelector('p').textContent = failureMessage(error);
      listError.hidden = false;
    } finally {
      if (!signal.aborted) { loading.hidden = true; setBusy(false); }
    }
  }

  async function edit(id) {
    if (busy || signal.aborted) return;
    const accessToken = token();
    if (!accessToken) return;
    notice.hidden = true;
    setBusy(true, 'Завантажуємо задачу…');
    try {
      const problem = await api.getProblem(id, accessToken, { signal });
      if (signal.aborted) return;
      editingId = problem.id;
      fieldErrors();
      for (const field of PROBLEM_FIELDS) form.elements.namedItem(field).value = problem[field] ?? '';
      root.querySelector('#problem-editor-title').textContent = 'Редагування задачі';
      root.querySelector('#save-problem').textContent = 'Зберегти зміни';
      root.querySelector('#cancel-problem-edit').hidden = false;
    } catch (error) { if (!signal.aborted) handleError(error); }
    finally { if (!signal.aborted) setBusy(false); }
    if (!signal.aborted && editingId === id) { form.elements.namedItem('title').focus(); form.scrollIntoView({ block: 'start' }); }
  }

  async function save(event) {
    event.preventDefault();
    if (busy || signal.aborted) return;
    const accessToken = token();
    if (!accessToken) return;
    const { values, errors } = validateProblem(Object.fromEntries(new FormData(form)));
    fieldErrors(errors);
    if (Object.keys(errors).length) { form.querySelector('[aria-invalid=true]').focus(); return; }
    const creating = editingId === null;
    let saved = false;
    notice.hidden = true;
    setBusy(true, creating ? 'Створюємо задачу…' : 'Зберігаємо зміни…');
    try {
      if (creating) await api.createProblem(values, accessToken, { signal });
      else await api.updateProblem(editingId, values, accessToken, { signal });
      if (signal.aborted) return;
      resetEditor();
      message(creating ? 'Задачу створено.' : 'Зміни збережено.', true);
      saved = true;
    } catch (error) { if (!signal.aborted) handleError(error, true); }
    finally { if (!signal.aborted) setBusy(false); }
    // A failed refresh must not turn a committed write into an apparent failed save.
    if (saved && !signal.aborted) await loadList(true);
  }

  async function remove() {
    if (busy || !deleting || signal.aborted) return;
    const accessToken = token();
    if (!accessToken) return;
    let removed = false;
    dialogError.hidden = true;
    setBusy(true, 'Видаляємо задачу…');
    try {
      await api.deleteProblem(deleting.id, accessToken, { signal });
      if (signal.aborted) return;
      if (editingId === deleting.id) resetEditor();
      dialog.close();
      message('Задачу видалено.', true);
      removed = true;
    } catch (error) { if (!signal.aborted) handleError(error, true, true); }
    finally { if (!signal.aborted) setBusy(false); }
    if (removed && !signal.aborted) await loadList(true);
  }

  list.addEventListener('click', (event) => {
    const button = event.target.closest('button[data-action]');
    if (!button || busy) return;
    const record = records.find((problem) => problem.id === Number(button.dataset.id));
    if (!record) return;
    if (button.dataset.action === 'edit') edit(record.id);
    else {
      deleting = record;
      dialogError.hidden = true;
      root.querySelector('#delete-problem-name').textContent = record.title;
      dialog.showModal();
    }
  });
  dialog.addEventListener('cancel', (event) => { if (busy) event.preventDefault(); });
  dialog.addEventListener('close', () => { deleting = null; });
  signal.addEventListener('abort', () => { if (dialog.open) dialog.close(); }, { once: true });
  root.querySelector('#confirm-delete-problem').addEventListener('click', remove);
  root.querySelector('#cancel-delete-problem').addEventListener('click', () => { if (!busy) dialog.close(); });
  root.querySelector('#new-problem').addEventListener('click', () => { if (!busy) { notice.hidden = true; resetEditor(true); } });
  root.querySelector('#cancel-problem-edit').addEventListener('click', () => { if (!busy) resetEditor(true); });
  root.querySelector('#refresh-problems').addEventListener('click', () => loadList());
  root.querySelector('#retry-problems').addEventListener('click', () => loadList());
  form.addEventListener('submit', save);
  form.addEventListener('input', () => { fieldErrors(); if (!busy) notice.hidden = true; });
  loadList();
}
